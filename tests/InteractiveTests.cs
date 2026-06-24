namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for how <c>--interactive</c> is parsed into a mode: absent, a bare flag (always show the
/// box), or with <c>errors</c> (show it only on failure).</summary>
public class InteractiveTests
{
    [Fact]
    public void NoFlag_IsOff()
    {
        Assert.Equal(InteractiveMode.Off, InteractiveOutput.ParseMode(new[] { "undervolt", "--mv", "960" }));
    }

    [Fact]
    public void BareFlag_IsAlways()
    {
        Assert.Equal(InteractiveMode.Always,
            InteractiveOutput.ParseMode(new[] { "undervolt", "--mv", "960", "--interactive" }));
    }

    [Fact]
    public void ErrorsValue_IsErrorsOnly()
    {
        Assert.Equal(InteractiveMode.ErrorsOnly,
            InteractiveOutput.ParseMode(new[] { "undervolt", "--mv", "960", "--interactive", "errors" }));
    }

    [Fact]
    public void FlagFollowedByAnotherFlag_IsAlways()
    {
        Assert.Equal(InteractiveMode.Always,
            InteractiveOutput.ParseMode(new[] { "undervolt", "--interactive", "--no-persist" }));
    }
}
