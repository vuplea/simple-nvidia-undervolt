namespace SimpleNvidiaUndervolt;

/// <summary>
/// The status (effective curve) buffer's record layout, recovered from the raw bytes: the record stride
/// and the <em>absolute</em> byte offsets of the voltage and frequency columns for entry 0
/// (<see cref="FreqColumn"/> is -1 when the GPU is idle and the frequency column has collapsed below a
/// detectable boost clock). The base/offset split the decode uses is a convention, so detection works in
/// absolute terms and stays unambiguous; callers re-express the columns against whatever base they like.
///
/// This runs only when a read fails the plausibility check (<see cref="GpuTuning.CurveVoltsPlausible"/>),
/// to print what the buffer actually looks like next to the compiled offsets — diagnostic detail for a
/// bug report when a card's tuning-buffer layout isn't the one this build expects.
/// </summary>
internal readonly record struct CurveLayout(int Stride, int VoltColumn, int FreqColumn, int Count)
{
    private const int MinPlausibleUv = 300_000;
    private const int MaxPlausibleUv = 1_300_000;
    private const int MaxPlausibleFreqKhz = 4_000_000;
    private const int BoostFreqKhz = 1_500_000;      // the freq column must reach a real boost clock to be identifiable
    private const int MinFreqRampKhz = 500_000;      // ...and climb to it, so a static max-clock field isn't mistaken for it
    private const int FreqDipToleranceKhz = 100_000; // tolerate small non-monotonic noise in the freq column

    /// <summary>Finds the layout by locating the longest run of strictly-ascending plausible core voltages:
    /// its spacing is the stride and its start the voltage column (the voltage axis is power-state
    /// independent, so this works at idle). The frequency column is the other word in the same record that
    /// climbs to a real boost clock without dropping; at idle it stays collapsed and is reported as -1.
    /// Base and in-record offsets aren't separable from one column, so both columns are absolute.</summary>
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
    /// <paramref name="baseRef"/> (the build's base, so they read as the <c>Status*</c> offsets to paste).
    /// At idle the frequency column is unknown and shown as <c>+0x??</c>.</summary>
    public string DescribeColumns(int baseRef)
        => $"stride {Stride}  volt +0x{VoltColumn - baseRef:X2}  "
           + (FreqColumn >= 0 ? $"freq +0x{FreqColumn - baseRef:X2}" : "freq +0x?? (idle - run under load)")
           + $"  ({Count} anchors)";

    /// <summary>The offsets this build compiled in, formatted like <see cref="DescribeColumns"/> (without
    /// an anchor count) for side-by-side comparison.</summary>
    public static string DescribeCompiled()
        => $"stride {NvApi.StatusEntryStride}  volt +0x{NvApi.StatusVoltOffset:X2}  freq +0x{NvApi.StatusFreqOffset:X2}";

    /// <summary>How the detected layout differs from the offsets compiled into <see cref="NvApi"/>, or null
    /// when it matches (the frequency column is skipped when idle and undetected, since it can't be read).</summary>
    public string? MismatchVsCompiled()
    {
        int b = NvApi.StatusEntryBase;
        bool matches = Stride == NvApi.StatusEntryStride
            && VoltColumn == b + NvApi.StatusVoltOffset
            && (FreqColumn < 0 || FreqColumn == b + NvApi.StatusFreqOffset);
        return matches ? null : $"detected {DescribeColumns(b)}; compiled {DescribeCompiled()}";
    }

    /// <summary>How many consecutive records from the column at absolute offset <paramref name="col"/> hold
    /// a strictly-ascending, plausible core voltage (uV), stepping by <paramref name="stride"/>.</summary>
    private static int AscendingVoltRun(byte[] buf, int col, int stride)
    {
        int run = 0, last = 0;
        for (int pos = col; pos + 4 <= buf.Length; pos += stride)
        {
            int uv = BitConverter.ToInt32(buf, pos);
            if (uv < MinPlausibleUv || uv > MaxPlausibleUv || (run > 0 && uv <= last))
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
    /// if none does. Requiring the climb (not just a high value) means an idle, collapsed curve reports -1
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

            if (ok && max >= BoostFreqKhz && max - first >= MinFreqRampKhz)
            {
                return col;
            }
        }

        return -1;
    }
}
