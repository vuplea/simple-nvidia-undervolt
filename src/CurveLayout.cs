namespace SimpleNvidiaUndervolt;

/// <summary>
/// The status (effective curve) buffer's record layout, recovered from the raw bytes: the record stride
/// and the <em>absolute</em> byte offsets of the voltage and frequency columns for entry 0
/// (<see cref="FreqColumn"/> is -1 when the frequency column has collapsed below a detectable boost
/// clock). The base/offset split the decode uses is a convention, so detection works in absolute
/// terms and stays unambiguous; only the descriptions re-express the columns against the build's base.
///
/// Runs when a read fails the plausibility check (<see cref="GpuTuning.CurveVoltsPlausible"/>) and on
/// demand via the <c>layout</c> command, to print what the buffer actually looks like next to the
/// compiled offsets — diagnostic detail for a bug report when a card's tuning-buffer layout isn't the
/// one this build expects.
/// </summary>
internal readonly record struct CurveLayout(int Stride, int VoltColumn, int FreqColumn, int Count)
{
    private const int MaxPlausibleFreqKhz = 4_000_000;
    // The frequency column must climb to a boost clock so a static max-clock field is not mistaken for it.
    private const int MinFreqRampKhz = 500_000;
    // Small non-monotonic noise the frequency column may carry without being rejected.
    private const int FreqDipToleranceKhz = 100_000;

    /// <summary>Finds the layout by locating the longest run of strictly-ascending plausible core voltages:
    /// its spacing is the stride and its start the voltage column (the voltage axis is power-state
    /// independent, so this always works). The frequency column is the other word in the same record that
    /// climbs to a real boost clock without dropping; in a collapsed read it can't be identified and is
    /// reported as -1. Base and in-record offsets aren't separable from one column, so both are absolute.</summary>
    public static bool TryDetect(byte[] buf, out CurveLayout layout)
    {
        layout = default;
        int bestStride = 0, bestVolt = 0, bestRun = 0;

        for (int stride = 16; stride <= 60; stride += 4)
        {
            for (int col = 0; col <= 320; col += 4)
            {
                int run = AscendingVoltRun(buf, col, stride);
                if (run > bestRun)
                {
                    (bestRun, bestStride, bestVolt) = (run, stride, col);
                }
            }
        }

        if (bestRun < 16)
        {
            return false;
        }

        int freqColumn = DetectFreqColumn(buf, bestVolt, bestStride, bestRun);
        layout = new CurveLayout(bestStride, bestVolt, freqColumn, bestRun);
        return true;
    }

    /// <summary>The detected columns as a one-line description, with the columns expressed relative to
    /// the build's <see cref="NvApi.StatusEntryBase"/> (so they read as the <c>Status*</c> offsets to
    /// paste). An undetected frequency column is shown as <c>+0x??</c>.</summary>
    public string DescribeColumns()
        => $"stride {Stride}  volt +0x{VoltColumn - NvApi.StatusEntryBase:X2}  "
           + (FreqColumn >= 0
               ? $"freq +0x{FreqColumn - NvApi.StatusEntryBase:X2}"
               : "freq +0x?? (undetected - run under a 3D load)")
           + $"  ({Count} anchors)";

    /// <summary>The offsets this build compiled in, formatted like <see cref="DescribeColumns"/> (without
    /// an anchor count) for side-by-side comparison.</summary>
    public static string DescribeCompiled()
        => $"stride {NvApi.StatusEntryStride}  volt +0x{NvApi.StatusVoltOffset:X2}  freq +0x{NvApi.StatusFreqOffset:X2}";

    /// <summary>Whether the detected layout matches the offsets compiled into <see cref="NvApi"/>
    /// (the frequency column is skipped when undetected, since it can't be read).</summary>
    public bool MatchesCompiled()
    {
        int b = NvApi.StatusEntryBase;
        return Stride == NvApi.StatusEntryStride
            && VoltColumn == b + NvApi.StatusVoltOffset
            && (FreqColumn < 0 || FreqColumn == b + NvApi.StatusFreqOffset);
    }

    /// <summary>How many consecutive records from the column at absolute offset <paramref name="col"/> hold
    /// a strictly-ascending, plausible core voltage (uV), stepping by <paramref name="stride"/>.</summary>
    private static int AscendingVoltRun(byte[] buf, int col, int stride)
    {
        int run = 0, last = 0;
        for (int pos = col; pos + 4 <= buf.Length; pos += stride)
        {
            int uv = BitConverter.ToInt32(buf, pos);
            if (uv < NvApi.MinCoreVoltUv || uv > NvApi.MaxCoreVoltUv || (run > 0 && uv <= last))
            {
                break;
            }

            last = uv;
            run++;
        }

        return run;
    }

    /// <summary>The absolute offset of the column in the same record as <paramref name="voltColumn"/> that
    /// reads as a non-decreasing frequency which <em>climbs</em> to a real boost clock across the run, or -1
    /// if none does. Requiring the climb (not just a high value) means a collapsed curve reports -1
    /// rather than latching a static max-clock field that happens to sit at a boost value. Candidates lie
    /// within one stride of the voltage column.</summary>
    private static int DetectFreqColumn(byte[] buf, int voltColumn, int stride, int count)
    {
        for (int col = voltColumn - (stride - 4); col <= voltColumn + (stride - 4); col += 4)
        {
            if (col == voltColumn || col < 0)
            {
                continue;
            }

            int first = -1, last = 0, max = 0;
            bool ok = true;
            for (int k = 0; k < count; k++)
            {
                int pos = col + k * stride;
                if (pos + 4 > buf.Length)
                {
                    ok = false;
                    break;
                }

                int v = BitConverter.ToInt32(buf, pos);
                if (v < 0 || v > MaxPlausibleFreqKhz || v < last - FreqDipToleranceKhz)
                {
                    ok = false;
                    break;
                }

                if (first < 0)
                {
                    first = v;
                }

                last = v;
                max = Math.Max(max, v);
            }

            if (ok && max >= NvApi.MinBoostClockKhz && max - first >= MinFreqRampKhz)
            {
                return col;
            }
        }

        return -1;
    }
}
