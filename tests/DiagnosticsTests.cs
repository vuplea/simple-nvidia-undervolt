namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the diagnostics' argument parsing: hex function ids and the watch poll interval.</summary>
public class DiagnosticsTests
{
    [Theory]
    [InlineData("21537AD4")]
    [InlineData("0x21537AD4")]
    [InlineData("0X21537AD4")]
    public void TryParseHex_AcceptsAnOptionalPrefix(string token)
    {
        Assert.True(Diagnostics.TryParseHex(token, out uint value));
        Assert.Equal(0x21537AD4u, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xyz")]
    [InlineData("210x53")] // '0x' is a prefix, not something to strip mid-token
    public void TryParseHex_RejectsNonHex(string? token)
    {
        Assert.False(Diagnostics.TryParseHex(token, out _));
    }

    [Fact]
    public void ParseRawRequest_RejectsASizeTheVersionWordCannotEncode()
    {
        // MakeVersion packs the claimed size into 16 bits; a larger claim would wrap into a garbage
        // version word the driver rejects with a misleading error, so the parse rejects it up front.
        Assert.Throws<CliError>(() => Diagnostics.ParseRawRequest(new[] { "21537AD4", "1", "70000" }));
    }

    [Fact]
    public void ParseIntervalMs_DefaultsToOneSecond()
    {
        Assert.Equal(1000, Diagnostics.ParseIntervalMs(ParseWatch("watch")));
    }

    [Theory]
    [InlineData("0.25", 250)]
    [InlineData("5", 5000)]
    public void ParseIntervalMs_TakesSeconds_FractionsAllowed(string value, int expectedMs)
    {
        Assert.Equal(expectedMs, Diagnostics.ParseIntervalMs(ParseWatch("watch", "--interval", value)));
    }

    [Theory]
    [InlineData("0")]     // below the 0.1s floor - would hammer the driver
    [InlineData("3601")]  // above the 1h ceiling - would read as a hang
    [InlineData("abc")]
    [InlineData("NaN")]
    public void ParseIntervalMs_RejectsOutOfRangeOrNonNumeric(string value)
    {
        Assert.Throws<CliError>(() => Diagnostics.ParseIntervalMs(ParseWatch("watch", "--interval", value)));
    }

    /// <summary>Parses args the way the watch dispatch does: the global flags plus <c>--interval</c>.</summary>
    private static Args.Parsed ParseWatch(params string[] args)
        => Args.Global.WithValue("--interval").Parse(args);
}
