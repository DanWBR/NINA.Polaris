namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Unsharp-mask sharpening for single-channel ushort images. Used by
/// STUDIO's "sharpen" op. The classic recipe is:
///
///   sharpened = original + amount × (original − blurred)
///
/// where <c>blurred</c> is a Gaussian-blurred copy of the original. The
/// difference (original − blurred) isolates high-frequency content
/// (edges, fine star detail); scaling that by <em>amount</em> and
/// adding it back boosts the edges without amplifying low-frequency
/// noise.
///
/// Why an explicit "threshold" guard? Without it, sharpening
/// indiscriminately amplifies noise alongside signal. The threshold
/// only applies the local boost when |original − blurred| exceeds it,
/// so noise floor (small differences) is left alone.
/// </summary>
public static class UnsharpMask {

    /// <summary>
    /// Apply unsharp mask. <paramref name="amount"/> = 1.0 is a typical
    /// moderate sharpen; 2-3 is aggressive. <paramref name="radius"/>
    /// is the Gaussian blur radius in pixels (small = sharpens fine
    /// detail, large = sharpens broad structure). <paramref name="threshold"/>
    /// in ADU; differences smaller than this aren't boosted (keeps
    /// noise floor calm).
    /// </summary>
    public static ushort[] Apply(ushort[] data, int width, int height,
                                 double amount = 1.0, int radius = 2, int threshold = 0) {
        if (amount <= 0 || radius < 1) return (ushort[])data.Clone();

        var blurred = GaussianBlur.Apply(data, width, height, radius);
        var output = new ushort[data.Length];
        for (int i = 0; i < data.Length; i++) {
            int diff = data[i] - blurred[i];
            if (threshold > 0 && Math.Abs(diff) < threshold) {
                output[i] = data[i];
                continue;
            }
            double v = data[i] + amount * diff;
            output[i] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
        }
        return output;
    }
}
