namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for <see cref="Compat"/>: which saved documents came from a release that read
/// every curve as plain kHz.</summary>
public class CompatTests
{
    [Theory]
    [InlineData("1.0.5+0dd8ee135", true)]
    [InlineData("1.0.9+beace06d9", true)]
    [InlineData("1.0.9", true)]
    [InlineData("1.0.10+abc123def", false)]
    [InlineData("1.1.0", false)]
    [InlineData("0.0.0+d87eb13aa", false)] // a development build
    [InlineData("unknown", false)]
    [InlineData(null, false)]              // a hand-written document
    public void SavedByPlainKhzOnlyRelease_NamesReleases105Through109(string? appVersion, bool expected)
    {
        Assert.Equal(expected, Compat.SavedByPlainKhzOnlyRelease(appVersion));
    }
}
