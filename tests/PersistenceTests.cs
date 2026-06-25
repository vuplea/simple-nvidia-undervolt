namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the args the persisted logon task is given — it must re-apply the same undervolt
/// but never re-persist, touch links, run a dry run or relay to a pipe, and it surfaces failures via an
/// errors-only message box.</summary>
public class PersistenceTests
{
    // The fixed flags every persisted run carries (it is itself the persistence, runs unattended and
    // already elevated): don't re-persist, don't touch links, show only failures.
    private static readonly string[] Fixed = { "--no-persist", "--no-shortcut-rename", "--interactive", "errors" };

    private static string[] Expected(params string[] head) => head.Concat(Fixed).ToArray();

    [Fact]
    public void StartupArgs_DropDryRun()
    {
        var result = Persistence.StartupArgs(
            new[] { "undervolt", "--mv", "960", "--mhz", "2850", "--dry-run" });

        Assert.Equal(Expected("undervolt", "--mv", "960", "--mhz", "2850"), result);
    }

    [Fact]
    public void StartupArgs_DropUserInteractiveAndSaveShortcut()
    {
        var result = Persistence.StartupArgs(
            new[] { "undervolt", "--mv", "925", "--interactive", "--save-shortcut" });

        Assert.Equal(Expected("undervolt", "--mv", "925"), result);
    }

    [Fact]
    public void StartupArgs_DropExistingNoPersistAndInteractiveErrors()
    {
        var result = Persistence.StartupArgs(
            new[] { "undervolt", "--mv", "925", "--no-persist", "--interactive", "errors" });

        Assert.Equal(Expected("undervolt", "--mv", "925"), result);
    }

    [Fact]
    public void StartupArgs_DropPipeNameAndShortcutNameWithValues()
    {
        // A persist from the auto-elevated child / a saved link receives --pipe-name / --shortcut-name;
        // neither must leak into the task.
        var result = Persistence.StartupArgs(
            new[] { "undervolt", "--mv", "960", "--pipe-name", "abc123", "--shortcut-name", "My OC" });

        Assert.Equal(Expected("undervolt", "--mv", "960"), result);
    }

    [Fact]
    public void StartupArgs_DropSaveShortcutNameValue()
    {
        var result = Persistence.StartupArgs(
            new[] { "undervolt", "--mv", "960", "--save-shortcut", "My OC" });

        Assert.Equal(Expected("undervolt", "--mv", "960"), result);
    }
}
