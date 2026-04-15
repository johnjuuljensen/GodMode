using GodMode.Vault.Services;

namespace GodMode.Vault.Tests;

public class TtlParserTests
{
    // --- Parse ---

    [Theory]
    [InlineData("90d", 90, 0, 0)]
    [InlineData("1d", 1, 0, 0)]
    [InlineData("24h", 0, 24, 0)]
    [InlineData("1h", 0, 1, 0)]
    [InlineData("30m", 0, 0, 30)]
    [InlineData("1m", 0, 0, 1)]
    public void Parse_ValidFormats_ReturnsTimeSpan(string input, int days, int hours, int minutes)
    {
        var expected = new TimeSpan(days, hours, minutes, 0);
        Assert.Equal(expected, TtlParser.Parse(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrWhitespace_ReturnsNull(string? input)
    {
        Assert.Null(TtlParser.Parse(input));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("90")]
    [InlineData("d")]
    [InlineData("90x")]
    [InlineData("90 d")]
    [InlineData("-1d")]
    public void Parse_InvalidFormats_ThrowsFormatException(string input)
    {
        Assert.Throws<FormatException>(() => TtlParser.Parse(input));
    }

    // --- Format ---

    [Fact]
    public void Format_Null_ReturnsNull()
    {
        Assert.Null(TtlParser.Format(null));
    }

    [Theory]
    [InlineData(90, 0, 0, "90d")]
    [InlineData(1, 0, 0, "1d")]
    [InlineData(0, 12, 0, "12h")]
    [InlineData(0, 1, 0, "1h")]
    [InlineData(0, 0, 30, "30m")]
    public void Format_ValidTimeSpans_ReturnsString(int days, int hours, int minutes, string expected)
    {
        var ts = new TimeSpan(days, hours, minutes, 0);
        Assert.Equal(expected, TtlParser.Format(ts));
    }

    [Fact]
    public void Format_24Hours_NormalizesToDays()
    {
        // 24h = 1 day, so Format prefers "1d"
        Assert.Equal("1d", TtlParser.Format(TimeSpan.FromHours(24)));
    }

    // --- Round-trip ---

    [Theory]
    [InlineData("90d")]
    [InlineData("12h")]
    [InlineData("30m")]
    public void ParseThenFormat_RoundTrips(string input)
    {
        var parsed = TtlParser.Parse(input);
        Assert.Equal(input, TtlParser.Format(parsed));
    }
}
