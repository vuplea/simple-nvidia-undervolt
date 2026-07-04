using System.Reflection;

namespace SimpleNvidiaUndervolt.E2E;

/// <summary>Reads a Windows shortcut's fields back via WScript.Shell, for asserting what the app wrote.
/// Late-bound COM, since the type is only known at runtime — fine in this test host, unlike the AOT
/// app itself, which must shell out to write.</summary>
internal static class Lnk
{
    public static (string Target, string Arguments, string WorkingDirectory) Read(string lnkPath)
    {
        object lnk = Load(lnkPath);
        return (Property(lnk, "TargetPath"), Property(lnk, "Arguments"), Property(lnk, "WorkingDirectory"));
    }

    public static string ReadIcon(string lnkPath) => Property(Load(lnkPath), "IconLocation");

    private static object Load(string lnkPath)
    {
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        return shell.GetType().InvokeMember(
            "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath })!;
    }

    private static string Property(object comObject, string name)
        => (string)comObject.GetType().InvokeMember(name, BindingFlags.GetProperty, null, comObject, null)!;
}
