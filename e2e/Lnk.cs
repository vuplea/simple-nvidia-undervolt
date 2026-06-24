using System.Diagnostics;

namespace SimpleNvidiaUndervolt.E2E;

/// <summary>Reads a Windows shortcut's fields back via WScript.Shell, for asserting what the app wrote.</summary>
internal static class Lnk
{
    public static (string Target, string Arguments, string WorkingDirectory) Read(string lnkPath)
    {
        string script =
            $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{lnkPath.Replace("'", "''")}');"
            + "Write-Output $s.TargetPath; Write-Output $s.Arguments; Write-Output $s.WorkingDirectory";

        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using Process process = Process.Start(psi)!;
        string[] lines = process.StandardOutput.ReadToEnd().Replace("\r", "").TrimEnd('\n').Split('\n');
        process.WaitForExit();
        return (lines[0], lines[1], lines[2]);
    }
}
