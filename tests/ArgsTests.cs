namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the argument validation behind the dispatch: every command (not just
/// <c>tune</c>) must reject a mistyped <c>--flag</c>, a stray positional token or a duplicated flag
/// rather than silently ignore it. The dispatch cases go through <see cref="Cli.Run"/> and rely on the
/// rejection happening before any GPU or elevation work, so they need no hardware.</summary>
public class ArgsTests
{
    private static readonly Args.Options PositionalOptions =
        Args.Global.WithValue("--interval").WithOptionalValue("--save-shortcut");

    [Theory]
    [InlineData("--no-persit")]  // a misspelled known flag
    [InlineData("--bogus")]
    public void Positionals_ThrowsOnAnUnknownFlag(string flag)
    {
        Assert.Throws<CliError>(
            () => PositionalOptions.Positionals(new[] { "scan", flag }));
    }

    [Fact]
    public void Positionals_CollectsValuesAndNegativeNumbers_ConsumingKnownFlags()
    {
        // Values and single-dash negative numbers are positionals; known flags (and their values,
        // like --save-shortcut's optional name) are consumed and don't leak into the result.
        string[] rest = PositionalOptions.Positionals(
            new[] { "scan", "-100", "--save-shortcut", "Quiet", "117000" });

        Assert.Equal(new[] { "-100", "117000" }, rest);
    }

    [Fact]
    public void TakeRelayPipeName_StripsOnlyTheTrailingRelayShapedPair()
    {
        string relayName = Product.Name + "-" + new string('0', 32); // the shape Elevate generates

        string[] relayed = { "clear", "--pipe-name", relayName };
        Assert.Equal(relayName, ElevationRelay.TakeRelayPipeName(ref relayed));
        Assert.Equal(new[] { "clear" }, relayed);

        // Not the final token pair, or not the generated shape: left in the args, so an ordinary parse
        // rejects the hand-typed flag instead of the run turning into a relay child.
        string[] notTrailing = { "clear", "--pipe-name", relayName, "--no-elevate" };
        Assert.Null(ElevationRelay.TakeRelayPipeName(ref notTrailing));
        Assert.Equal(4, notTrailing.Length);

        string[] handTyped = { "clear", "--pipe-name", "pipe" };
        Assert.Null(ElevationRelay.TakeRelayPipeName(ref handTyped));
        Assert.Equal(3, handTyped.Length);
    }

    [Fact]
    public void Run_RejectsPipeNameOutsideTheInternalRelay()
    {
        // The relay flag isn't part of any command's accepted set, so hand-typed it fails validation.
        Assert.Equal(1, Cli.Run(new[] { "status", "--pipe-name", "pipe" }));
    }

    [Theory]
    [InlineData("status", "--bogus")]
    [InlineData("clear", "--no-persit")]  // a misspelled flag on a command with no options of its own
    [InlineData("watch", "--mv")]          // a tune-only flag isn't valid here
    public void Run_RejectsUnknownFlagOnNonTuneCommands(string command, string flag)
    {
        // Any failure exits 1. The rejection happens before any elevation or GPU access, so this needs
        // no hardware.
        Assert.Equal(1, Cli.Run(new[] { command, flag }));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("watch")]
    [InlineData("scan")]
    public void Run_RejectsNoElevateOnReadOnlyCommands(string command)
    {
        Assert.Equal(1, Cli.Run(new[] { command, "--no-elevate" }));
    }

    [Theory]
    [InlineData("status", "extra")]
    [InlineData("clear", "no-elevate")]   // forgot the dashes
    public void Run_RejectsAStrayPositionalToken(string command, string token)
    {
        Assert.Equal(1, Cli.Run(new[] { command, token }));
    }

    [Fact]
    public void Run_RejectsADuplicatedFlag()
    {
        Assert.Equal(1, Cli.Run(new[] { "watch", "--interval", "1", "--interval", "2" }));
    }

    [Fact]
    public void Run_HonoursHelpAndVersionOnlyAsTheFirstToken()
    {
        Assert.Equal(0, Cli.Run(new[] { "--version" }));

        // Buried in a command's arguments they must not swallow it - strict validation rejects them
        // (before any elevation or GPU access, so this needs no hardware).
        Assert.Equal(1, Cli.Run(new[] { "tune", "--mv", "960", "--version" }));
        Assert.Equal(1, Cli.Run(new[] { "status", "--help" }));
    }

    [Fact]
    public void Run_RejectsSilentOnWatch()
    {
        // --silent holds output back until the run ends, but watch is its live output - the
        // combination would poll invisibly and discard everything on Ctrl+C.
        var (exit, error) = RunCapturingError("watch", "--silent");

        Assert.Equal(1, exit);
        Assert.Contains("doesn't support --silent", error);
    }

    [Fact]
    public void Run_RejectsABadWatchIntervalAsAUsageError()
    {
        // --interval is validated at dispatch, before any GPU access, so a typo fails there (exit 1)
        // instead of surfacing later as a run failure.
        var (exit, error) = RunCapturingError("watch", "--interval", "abc");

        Assert.Equal(1, exit);
        Assert.Contains("--interval requires a numeric value", error);
    }

    [Theory]
    [InlineData("scan", "bad", "Usage: simple-nvidia-undervolt scan")]
    [InlineData("probe", "xyz", "Usage: simple-nvidia-undervolt probe")]
    [InlineData("extent", "bad", "Usage: simple-nvidia-undervolt extent")]
    [InlineData("raw", "bad", "Usage: simple-nvidia-undervolt raw")]
    public void Run_RejectsBadDiagnosticPositionalsAsUsageErrors(string command, string arg, string expected)
    {
        // Positional diagnostics parse their own arguments before NVAPI starts too; otherwise a typo
        // would be hidden behind "driver unavailable" on machines without a GPU.
        var (exit, error) = RunCapturingError(command, arg);

        Assert.Equal(1, exit);
        Assert.Contains(expected, error);
    }

    private static (int Exit, string Error) RunCapturingError(params string[] args)
    {
        TextWriter originalError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            int exit = Cli.Run(args);
            return (exit, error.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
