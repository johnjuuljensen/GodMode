using System.ComponentModel;
using System.Runtime.CompilerServices;
using GodMode.Shared.Models;

namespace GodMode.Avalonia.Models;

/// <summary>
/// Wraps either a <see cref="ClaudeMessage"/> or a tool-call summary count
/// for display in the chat view. Used by the simple/detailed view toggle.
/// </summary>
public sealed class ChatDisplayItem : INotifyPropertyChanged
{
    /// <summary>The wrapped message (null for tool summaries).</summary>
    public ClaudeMessage? Message { get; }

    /// <summary>Whether this item is a collapsed tool-call summary.</summary>
    public bool IsToolSummary { get; }

    /// <summary>Whether this item represents a regular (non-summary) message.</summary>
    public bool IsMessage => !IsToolSummary;

    /// <summary>Whether the parent view is in simple mode (affects content display).</summary>
    public bool IsSimpleView { get; }

    private int _toolCallCount;
    /// <summary>Number of collapsed tool-call messages in this summary.</summary>
    public int ToolCallCount
    {
        get => _toolCallCount;
        set
        {
            if (_toolCallCount != value)
            {
                _toolCallCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ToolSummaryText));
            }
        }
    }

    /// <summary>Display text for tool-call summaries, e.g. "{ 5 tool calls }".</summary>
    public string ToolSummaryText => $"{{ {ToolCallCount} tool call{(ToolCallCount == 1 ? "" : "s")} }}";

    /// <summary>Whether the message contains error content.</summary>
    public bool HasErrorContent => Message?.HasErrorContent ?? false;

    /// <summary>
    /// Content summary appropriate for the current view mode.
    /// In simple view, shows only text content; in detailed view, shows all content.
    /// </summary>
    public string DisplayContentSummary => IsSimpleView && Message != null
        ? Message.TextOnlyContentSummary
        : Message?.ContentSummary ?? "";

    /// <summary>Creates a display item wrapping a message.</summary>
    public ChatDisplayItem(ClaudeMessage message, bool isSimpleView = false)
    {
        Message = message;
        IsToolSummary = false;
        IsSimpleView = isSimpleView;
    }

    /// <summary>Creates a tool-call summary display item.</summary>
    public ChatDisplayItem(int toolCallCount)
    {
        IsToolSummary = true;
        _toolCallCount = toolCallCount;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
