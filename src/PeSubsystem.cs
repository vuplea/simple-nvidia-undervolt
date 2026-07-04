using System.Buffers.Binary;

namespace SimpleNvidiaUndervolt;

/// <summary>
/// Switches the installed <c>-nocmd</c> copy of the exe to the GUI subsystem (a 2-byte field in the PE
/// header), so the logon task launches it without flashing a console window. A GUI-subsystem process gets no console:
/// stdout and stderr silently discard — fine for the task, whose output nobody watches and whose failures
/// surface through the message box — while the exe the user downloads keeps
/// the console subsystem, so terminal use is untouched. Patching the copy beats the alternatives: a
/// second shipped binary breaks the single-file download, a PowerShell/cmd launcher flashes its own
/// console, a wscript launcher drops a script into Program Files, and hiding the window from inside the
/// process (ShowWindow) leaves a visible flicker because the window exists before the code runs.
/// The patch rewrites the PE header, so it invalidates an Authenticode signature — if releases are ever
/// signed, the installed copy needs a different windowless strategy (e.g. embedding a pre-signed GUI
/// variant to extract instead of patching).
/// </summary>
internal static class PeSubsystem
{
    private const ushort WindowsGui = 2; // IMAGE_SUBSYSTEM_WINDOWS_GUI
    private const ushort WindowsCui = 3; // IMAGE_SUBSYSTEM_WINDOWS_CUI

    /// <summary>Headers live well inside this on any real executable.</summary>
    private const int HeaderRead = 4096;

    /// <summary>Patches <paramref name="exePath"/> to the GUI subsystem. Idempotent; anything that isn't
    /// a console or GUI Windows executable throws, so a corrupt copy fails the install loudly.</summary>
    public static void MakeWindowless(string exePath)
    {
        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite);
        var (offset, _) = ReadSubsystemField(stream);

        Span<byte> gui = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(gui, WindowsGui);
        stream.Position = offset;
        stream.Write(gui);
    }

    /// <summary>Whether the executable already has the GUI subsystem. False for a file that is missing,
    /// unreadable or not a console/GUI executable — the caller treats any of those as "not patched".
    /// Lets the already-installed check see an interrupted install that copied the <c>-nocmd</c> file
    /// but died before patching it: its bytes then still match the console executable, so a content
    /// comparison alone would skip the reinstall forever.</summary>
    public static bool IsWindowless(string exePath)
    {
        try
        {
            using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read);
            return ReadSubsystemField(stream).Subsystem == WindowsGui;
        }
        catch (Exception ex) when (ex is CliError or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads the header from the stream's start and returns the Subsystem field's file offset
    /// and current value (both validated by <see cref="SubsystemOffset"/>).</summary>
    private static (int Offset, ushort Subsystem) ReadSubsystemField(FileStream stream)
    {
        var header = new byte[HeaderRead];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        var image = header.AsSpan(0, read);
        int offset = SubsystemOffset(image);
        return (offset, BinaryPrimitives.ReadUInt16LittleEndian(image[offset..]));
    }

    /// <summary>The file offset of the optional header's Subsystem field, after validating the DOS/PE
    /// magics around it and that the current subsystem is the console or GUI one.</summary>
    internal static int SubsystemOffset(ReadOnlySpan<byte> image)
    {
        // IMAGE_DOS_HEADER: 'MZ', with e_lfanew (the PE header offset) at +0x3C.
        if (image.Length < 0x40 || image[0] != 'M' || image[1] != 'Z')
        {
            throw new CliError("Not a Windows executable (no MZ header).");
        }

        // IMAGE_NT_HEADERS: "PE\0\0" signature, the 20-byte COFF header, then the optional header,
        // whose Subsystem field sits at +68 in both the PE32 (0x10B) and PE32+ (0x20B) layouts.
        // e_lfanew is bounded against the image length BEFORE any arithmetic on it: summing first
        // would let a huge value overflow int, slip past the range check, and turn the validation
        // into an out-of-range read.
        const int headersThroughSubsystem = 4 + 20 + 68 + 2;
        int pe = BinaryPrimitives.ReadInt32LittleEndian(image[0x3C..]);
        if (pe < 0x40 || pe > image.Length - headersThroughSubsystem
            || image[pe] != 'P' || image[pe + 1] != 'E' || image[pe + 2] != 0 || image[pe + 3] != 0)
        {
            throw new CliError("Not a valid PE executable.");
        }

        int subsystemOffset = pe + 4 + 20 + 68;

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(image[(pe + 24)..]);
        if (magic is not (0x10B or 0x20B))
        {
            throw new CliError($"Unrecognized PE optional-header magic 0x{magic:X}.");
        }

        ushort subsystem = BinaryPrimitives.ReadUInt16LittleEndian(image[subsystemOffset..]);
        if (subsystem is not (WindowsGui or WindowsCui))
        {
            throw new CliError($"Unexpected PE subsystem {subsystem}.");
        }

        return subsystemOffset;
    }
}
