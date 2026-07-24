namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Tests for the raw read's request-mask bound: the word count is a command-line argument,
/// so it must be rejected before the mask is materialized (a huge count would allocate its full
/// size, or overflow the byte count outright) and before anything reaches the driver.</summary>
public class RawReadGuardTests
{
    [Theory]
    [InlineData(int.MaxValue)] // the byte count overflows while building the mask
    [InlineData(70_000)]       // builds fine but exceeds the probe allocation
    [InlineData(-1)]
    public void RawRead_RejectsMaskWordsThatDontFitTheBuffer(int maskWords)
    {
        // A zero GPU handle proves the rejection happens before any driver call.
        Assert.Throws<CliError>(() => NvApi.ReadRaw(IntPtr.Zero, 0x21537AD4, 1, 7208,
            NvApi.ProbeAllocSize, maskWords));
    }
}
