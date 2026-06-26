namespace SimpleNvidiaUndervolt.Tests;

/// <summary>Synthetic V/F curves for the pure-logic tests. Real curves are ascending in both voltage
/// and frequency; these mirror that shape with values chosen so expected results are easy to compute.</summary>
internal static class TestCurves
{
    /// <summary>A clean, readable curve: <paramref name="n"/> points, voltage 800 mV in 20 mV steps and
    /// frequency 2000 MHz in 50 MHz steps (so anchor i is (800 + 20i mV, 2000 + 50i MHz)). With n = 20
    /// it satisfies <see cref="GpuTuning.CurveFreqsReadable"/>: ≥16 points, monotonic, peak ≥1500.</summary>
    public static List<(int Mv, int Mhz)> Realistic(int n = 20)
        => Enumerable.Range(0, n).Select(i => (800 + i * 20, 2000 + i * 50)).ToList();

    /// <summary>A 20-point curve whose frequency column has collapsed to an idle value — the shape the
    /// driver returns in a non-3D power state. Voltages stay valid; the peak frequency never reaches a
    /// boost clock, so it reads as not usable.</summary>
    public static List<(int Mv, int Mhz)> Collapsed(int n = 20)
        => Enumerable.Range(0, n).Select(i => (800 + i * 20, 195)).ToList();

    /// <summary>Reconstructs the effective frequency per anchor from a stock curve and the kHz deltas a
    /// <see cref="GpuTuning.CurvePlan"/> would write.</summary>
    public static int[] Apply(IReadOnlyList<(int Mv, int Mhz)> stock, int[] deltasKhz)
        => stock.Select((p, i) => p.Mhz + deltasKhz[i] / 1000).ToArray();
}
