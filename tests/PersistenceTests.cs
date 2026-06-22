namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the args the persisted logon task is given — it must re-apply the same undervolt
/// but surface failures, and never re-trigger persistence or a dry run.</summary>
public class PersistenceTests
{
    [Fact]
    public void StartupArgs_DropPersistAndDryRun_AndAddMsgbox()
    {
        var result = Persistence.StartupArgs(
            new[] { "undervolt", "--mv", "960", "--mhz", "2850", "--persist", "--dry-run" });

        Assert.Equal(new[] { "undervolt", "--mv", "960", "--mhz", "2850", "--msgbox" }, result);
    }

    [Fact]
    public void StartupArgs_KeepMsgboxWithoutDuplicating()
    {
        var result = Persistence.StartupArgs(new[] { "undervolt", "--mv", "925", "--msgbox", "--persist" });

        Assert.Equal(new[] { "undervolt", "--mv", "925", "--msgbox" }, result);
    }
}
