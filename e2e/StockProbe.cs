namespace SimpleNvidiaUndervolt.E2E;

/// <summary>Shared curve groundwork for the tests that assert against specific anchors: a clean
/// stock read to measure from, and the anchor a test caps at.</summary>
internal static class StockProbe
{
    /// <summary>Resets the GPU to stock (the tuning tests rewrite it anyway; the fixture restores
    /// the original at suite end) and returns a clean stock curve read, or skips the test on the
    /// same transitional-read condition the app itself refuses to plan from.</summary>
    public static IReadOnlyList<(int Mv, int Mhz)> ResetAndReadStockOrSkip(IntPtr gpu)
    {
        IReadOnlyList<(int Mv, int Mhz)> stock = GpuTuning.ResetAndReadStock(gpu);
        Skip.IfNot(GpuTuning.CurveFreqsReadable(stock),
            "the curve didn't read back cleanly (a brief transitional state) - retry.");
        return stock;
    }

    /// <summary>The anchor a test caps at: the one nearest 925 mV, resolved exactly as a real cap
    /// resolves. The V/F table extends well above the voltage the boost actually operates at (a cap
    /// up there caps nothing), so the anchor must be picked in the real undervolt range - 925 mV
    /// sits below every supported generation's operating ceiling and above every driver cap floor,
    /// the same value the ready-made profiles converge on. Clamped clear of the curve's ends so a
    /// band anchor below and the two anchors a flat needs above both exist.</summary>
    public static int PickCapAnchor(IReadOnlyList<(int Mv, int Mhz)> stock)
        => Math.Clamp(GpuTuning.NearestAnchorIndex(stock, 925), 2, stock.Count - 3);
}
