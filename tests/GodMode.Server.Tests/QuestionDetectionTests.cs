using GodMode.Server.Services;

namespace GodMode.Server.Tests;

public class QuestionDetectionTests
{
    // --- IsQuestion ---

    [Theory]
    [InlineData("Should I continue?", true)]
    [InlineData("Should I continue?  ", true)]
    [InlineData("Should I continue?\n\n", true)]
    [InlineData("This is a statement.", false)]
    [InlineData("Done. Here is the final list:\n- one\n- two\n- three", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("?", true)]
    public void IsQuestion_DeterministicRule(string? text, bool expected)
    {
        Assert.Equal(expected, QuestionDetection.IsQuestion(text));
    }

    [Fact]
    public void IsQuestion_Null_ReturnsFalse()
    {
        Assert.False(QuestionDetection.IsQuestion(null));
    }

    // --- ExtractLastAssistantText ---

    [Fact]
    public void ExtractLastAssistantText_SingleTextBlock_Returns()
    {
        const string json = """
        {
          "type": "assistant",
          "message": {
            "content": [
              { "type": "text", "text": "Do you want to proceed?" }
            ]
          }
        }
        """;

        Assert.Equal("Do you want to proceed?", QuestionDetection.ExtractLastAssistantText(json));
    }

    [Fact]
    public void ExtractLastAssistantText_MultipleTextBlocks_ReturnsLast()
    {
        const string json = """
        {
          "type": "assistant",
          "message": {
            "content": [
              { "type": "text", "text": "Let me think." },
              { "type": "text", "text": "Actually, should I continue?" }
            ]
          }
        }
        """;

        Assert.Equal("Actually, should I continue?", QuestionDetection.ExtractLastAssistantText(json));
    }

    [Fact]
    public void ExtractLastAssistantText_TextThenToolUse_ReturnsText()
    {
        // Text can precede a tool call inside the same assistant turn.
        // We still want the last text block, not "".
        const string json = """
        {
          "type": "assistant",
          "message": {
            "content": [
              { "type": "text", "text": "I'll grep for that." },
              { "type": "tool_use", "name": "Grep", "input": { "pattern": "foo" } }
            ]
          }
        }
        """;

        Assert.Equal("I'll grep for that.", QuestionDetection.ExtractLastAssistantText(json));
    }

    [Fact]
    public void ExtractLastAssistantText_ToolUseOnly_ReturnsNull()
    {
        const string json = """
        {
          "type": "assistant",
          "message": {
            "content": [
              { "type": "tool_use", "name": "Grep", "input": { "pattern": "foo" } }
            ]
          }
        }
        """;

        Assert.Null(QuestionDetection.ExtractLastAssistantText(json));
    }

    [Fact]
    public void ExtractLastAssistantText_NoMessageField_ReturnsNull()
    {
        const string json = """{ "type": "assistant" }""";
        Assert.Null(QuestionDetection.ExtractLastAssistantText(json));
    }

    [Fact]
    public void ExtractLastAssistantText_EmptyContentArray_ReturnsNull()
    {
        const string json = """{ "type": "assistant", "message": { "content": [] } }""";
        Assert.Null(QuestionDetection.ExtractLastAssistantText(json));
    }

    [Fact]
    public void ExtractLastAssistantText_InvalidJson_ReturnsNull()
    {
        Assert.Null(QuestionDetection.ExtractLastAssistantText("not json"));
    }

    [Fact]
    public void ExtractLastAssistantText_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(QuestionDetection.ExtractLastAssistantText(null!));
        Assert.Null(QuestionDetection.ExtractLastAssistantText(""));
        Assert.Null(QuestionDetection.ExtractLastAssistantText("   "));
    }

    [Fact]
    public void ExtractLastAssistantText_FinalListLooksLikeQuestion_ReturnsFinalListText()
    {
        // Regression: the old heuristic false-positived on final lists because
        // they contain '?' somewhere or match phrases. Here the trailing text
        // is a list with no trailing '?' — should be detected as NOT a question.
        const string json = """
        {
          "type": "assistant",
          "message": {
            "content": [
              { "type": "text", "text": "Done! Here is what I changed:\n- file A\n- file B\n- file C" }
            ]
          }
        }
        """;

        var text = QuestionDetection.ExtractLastAssistantText(json);
        Assert.NotNull(text);
        Assert.False(QuestionDetection.IsQuestion(text));
    }
}
