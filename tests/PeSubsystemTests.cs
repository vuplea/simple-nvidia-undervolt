using System.Buffers.Binary;

namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for <see cref="PeSubsystem"/>, which switches the installed copy to the GUI subsystem
/// so the logon task runs it without flashing a console window. Exercised against synthetic PE headers,
/// so no real executable is modified.</summary>
public class PeSubsystemTests
{
    private const ushort Gui = 2;
    private const ushort Cui = 3;

    [Theory]
    [InlineData(0x20B)]  // PE32+ (the shipped x64 build)
    [InlineData(0x10B)]  // PE32 - the Subsystem field sits at the same offset
    public void SubsystemOffset_FindsTheFieldBehindTheValidatedHeaders(int optionalHeaderMagic)
    {
        const int pe = 0x80;
        byte[] image = BuildPe(pe, (ushort)optionalHeaderMagic, Cui);

        Assert.Equal(pe + 4 + 20 + 68, PeSubsystem.SubsystemOffset(image));
    }

    [Fact]
    public void MakeWindowless_FlipsConsoleToGui_AndIsIdempotent()
    {
        const int pe = 0x80;
        WithTempFile(BuildPe(pe, 0x20B, Cui), path =>
        {
            PeSubsystem.MakeWindowless(path);
            Assert.Equal(Gui, ReadSubsystem(path, pe));

            PeSubsystem.MakeWindowless(path);   // an already-GUI copy stays GUI
            Assert.Equal(Gui, ReadSubsystem(path, pe));
        });
    }

    [Fact]
    public void IsWindowless_TracksThePatch()
    {
        // The already-installed check keys on this: a copied-but-unpatched -nocmd file (still the
        // console subsystem, bytes identical to the console exe) must read as not windowless, so an
        // interrupted install reinstalls instead of skipping forever.
        WithTempFile(BuildPe(0x80, 0x20B, Cui), path =>
        {
            Assert.False(PeSubsystem.IsWindowless(path));

            PeSubsystem.MakeWindowless(path);
            Assert.True(PeSubsystem.IsWindowless(path));
        });
    }

    [Fact]
    public void IsWindowless_FalseForAMissingOrMalformedFile()
    {
        Assert.False(PeSubsystem.IsWindowless(TempPath()));

        WithTempFile(new byte[512], path => // no MZ header
            Assert.False(PeSubsystem.IsWindowless(path)));
    }

    [Fact]
    public void SubsystemOffset_RejectsANonExecutable()
    {
        Assert.Throws<CliError>(() => PeSubsystem.SubsystemOffset(new byte[512]));
    }

    [Fact]
    public void SubsystemOffset_RejectsABrokenPeSignature()
    {
        byte[] image = BuildPe(0x80, 0x20B, Cui);
        image[0x80] = (byte)'X';
        Assert.Throws<CliError>(() => PeSubsystem.SubsystemOffset(image));
    }

    [Fact]
    public void SubsystemOffset_RejectsAnUnexpectedSubsystem()
    {
        // e.g. IMAGE_SUBSYSTEM_NATIVE - not something this app's copy can be, so refuse to touch it.
        byte[] image = BuildPe(0x80, 0x20B, subsystem: 1);
        Assert.Throws<CliError>(() => PeSubsystem.SubsystemOffset(image));
    }

    [Fact]
    public void SubsystemOffset_RejectsAHugeHeaderOffsetWithoutOverflowing()
    {
        // An e_lfanew near int.MaxValue would overflow the offset arithmetic and slip past a
        // sum-then-compare bounds check; it must be rejected, not read out of range.
        byte[] image = BuildPe(0x80, 0x20B, Cui);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), int.MaxValue - 64);

        Assert.Throws<CliError>(() => PeSubsystem.SubsystemOffset(image));
    }

    [Fact]
    public void SubsystemOffset_RejectsATruncatedImage()
    {
        byte[] image = BuildPe(0x80, 0x20B, Cui);
        Assert.Throws<CliError>(() => PeSubsystem.SubsystemOffset(image.AsSpan(0, 0x90).ToArray()));
    }

    /// <summary>A minimal image with just the fields the patcher validates: 'MZ', e_lfanew, "PE\0\0",
    /// the optional-header magic, and the Subsystem field at its fixed offset behind them.</summary>
    private static byte[] BuildPe(int peOffset, ushort optionalHeaderMagic, ushort subsystem)
    {
        var image = new byte[peOffset + 4 + 20 + 68 + 2 + 16];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
        image[peOffset] = (byte)'P';
        image[peOffset + 1] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 24), optionalHeaderMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4 + 20 + 68), subsystem);
        return image;
    }

    private static ushort ReadSubsystem(string path, int peOffset)
        => BinaryPrimitives.ReadUInt16LittleEndian(
            File.ReadAllBytes(path).AsSpan(peOffset + 4 + 20 + 68));

    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "nvundervolt-pe-" + Guid.NewGuid().ToString("N"));

    /// <summary>Runs <paramref name="body"/> against a temp file holding <paramref name="content"/>,
    /// deleting it afterwards.</summary>
    private static void WithTempFile(byte[] content, Action<string> body)
    {
        string path = TempPath();
        File.WriteAllBytes(path, content);
        try
        {
            body(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
