namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the helper-process runner's failure conversion: a helper that cannot start is an
/// environment problem reported as a <see cref="CliError"/> message, not a bug with a stack.</summary>
public class ChildProcessTests
{
    [Fact]
    public void Run_MissingExecutable_ThrowsACliErrorNamingIt()
    {
        var ex = Assert.Throws<CliError>(() => ChildProcess.Run("nvundervolt-no-such-helper.exe"));

        Assert.Contains("nvundervolt-no-such-helper.exe", ex.Message);
    }
}
