namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Estimate a polynomial gradient across an image and subtract it.
/// Used by STUDIO's "Remove gradient" action to flatten sky background
/// before stretch, a tilted gradient that's invisible in the raw
/// linear data becomes very visible after MTF stretch, and a 2nd-order
/// poly captures the usual light-pollution + flat-error gradient most
/// astrophotos suffer from.
///
/// Algorithm:
///   1. Sample the image on a coarse <c>samplesX × samplesY</c> grid of
///      patches (default 8×6).
///   2. In each patch, take the median of the pixels not flagged as
///      stars or other bright features (using the StarDetector hot-pixel
///      threshold as a sigma estimate, anything &gt; median + 3·σ is
///      excluded, so most stellar / nebular signal is rejected).
///   3. Fit a 2D polynomial (default degree 2) to the (x, y, median)
///      samples using normal-equation least squares.
///   4. Per-pixel subtract <c>poly(x, y) − minBackground</c> from the
///      image. We subtract <em>relative</em> to the fit's minimum so
///      the global brightness is preserved and we never push valid
///      signal below zero.
///
/// Why polynomial and not a 2D spline / radial basis? A degree-2
/// polynomial has 6 coefficients, robust to fit with ~48 samples,
/// captures real-world tilt + corner vignetting that survives flat
/// calibration. Splines need more samples and are overkill at this
/// stage in the pipeline.
/// </summary>
public static class BackgroundExtractor {

    public record Options(int SamplesX, int SamplesY, int PolyDegree) {
        public static readonly Options Default = new(SamplesX: 8, SamplesY: 6, PolyDegree: 2);
    }

    /// <summary>Run the full pipeline and return the corrected pixel
    /// buffer (same dimensions as the input).</summary>
    public static ushort[] Subtract(ushort[] data, int width, int height, Options? options = null) {
        var opts = options ?? Options.Default;
        if (opts.PolyDegree < 1 || opts.PolyDegree > 2)
            throw new ArgumentOutOfRangeException(nameof(opts),
                "Only polynomial degree 1 or 2 supported.");

        // Patch size.
        int boxW = Math.Max(8, width  / opts.SamplesX);
        int boxH = Math.Max(8, height / opts.SamplesY);

        var samples = new List<(double X, double Y, double Value)>();
        for (int gy = 0; gy < opts.SamplesY; gy++) {
            for (int gx = 0; gx < opts.SamplesX; gx++) {
                int x0 = gx * boxW;
                int y0 = gy * boxH;
                int x1 = Math.Min(width,  x0 + boxW);
                int y1 = Math.Min(height, y0 + boxH);
                var sample = MedianSample(data, width, x0, y0, x1, y1);
                if (sample.HasValue) {
                    samples.Add((
                        X: x0 + (x1 - x0) / 2.0,
                        Y: y0 + (y1 - y0) / 2.0,
                        Value: sample.Value));
                }
            }
        }

        if (samples.Count < (opts.PolyDegree == 2 ? 6 : 3))
            // Not enough valid background patches; bail out, return
            // the input unchanged.
            return (ushort[])data.Clone();

        var coeffs = FitPoly2D(samples, opts.PolyDegree);

        // Find the minimum of the fitted surface over the image so we
        // can subtract a *relative* gradient, not absolute level.
        double minFit = double.MaxValue;
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                var v = Poly(coeffs, x, y);
                if (v < minFit) minFit = v;
            }
        }

        var output = new ushort[data.Length];
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                int idx = y * width + x;
                double subtract = Poly(coeffs, x, y) - minFit;
                double v = data[idx] - subtract;
                output[idx] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
            }
        }
        return output;
    }

    // --- internals ---

    /// <summary>Median of pixels in a rectangle, with bright-feature
    /// rejection (anything beyond median + 3·MAD on a first pass is
    /// dropped, kills stellar + nebula tails).</summary>
    private static double? MedianSample(ushort[] data, int width, int x0, int y0, int x1, int y1) {
        int n = (x1 - x0) * (y1 - y0);
        if (n < 16) return null;
        var buf = new ushort[n];
        int k = 0;
        for (int y = y0; y < y1; y++) {
            int rowOff = y * width;
            for (int x = x0; x < x1; x++) {
                buf[k++] = data[rowOff + x];
            }
        }
        Array.Sort(buf);
        var med0 = buf[n / 2];

        // MAD = median of |x - med|. Cheap proxy for σ.
        var dev = new ushort[n];
        for (int i = 0; i < n; i++) dev[i] = (ushort)Math.Abs(buf[i] - med0);
        Array.Sort(dev);
        var mad = dev[n / 2];
        double threshold = med0 + 3.0 * 1.4826 * mad; // 1.4826 = MAD→σ scale

        // Re-median over surviving pixels.
        var keep = new List<ushort>(n);
        foreach (var v in buf) {
            if (v <= threshold) keep.Add(v);
        }
        if (keep.Count < n / 4) return null;  // too much rejected → bad patch
        keep.Sort();
        return keep[keep.Count / 2];
    }

    /// <summary>Solve a least-squares 2D polynomial fit via normal
    /// equations. coeffs order for degree 2 is [1, x, y, x², xy, y²];
    /// for degree 1 it's [1, x, y].</summary>
    private static double[] FitPoly2D(IReadOnlyList<(double X, double Y, double V)> samples, int degree) {
        int k = degree == 2 ? 6 : 3;
        // Normal equations: AᵀA · coeffs = Aᵀb
        // Each sample contributes one row to A: terms(x, y).
        var ata = new double[k, k];
        var atb = new double[k];

        var terms = new double[k];
        foreach (var s in samples) {
            FillTerms(terms, s.X, s.Y, degree);
            for (int i = 0; i < k; i++) {
                atb[i] += terms[i] * s.V;
                for (int j = 0; j < k; j++) {
                    ata[i, j] += terms[i] * terms[j];
                }
            }
        }
        return SolveLinear(ata, atb);
    }

    private static void FillTerms(double[] outBuf, double x, double y, int degree) {
        outBuf[0] = 1;
        outBuf[1] = x;
        outBuf[2] = y;
        if (degree == 2) {
            outBuf[3] = x * x;
            outBuf[4] = x * y;
            outBuf[5] = y * y;
        }
    }

    private static double Poly(double[] c, double x, double y) {
        // 1, x, y are always present; degree-2 adds the next three.
        double v = c[0] + c[1] * x + c[2] * y;
        if (c.Length >= 6) v += c[3] * x * x + c[4] * x * y + c[5] * y * y;
        return v;
    }

    /// <summary>Gauss-Jordan elimination with partial pivoting. Big-O
    /// is n³ but n ≤ 6 here, so it's effectively constant time.</summary>
    private static double[] SolveLinear(double[,] a, double[] b) {
        int n = b.Length;
        // Augmented matrix.
        var m = new double[n, n + 1];
        for (int i = 0; i < n; i++) {
            for (int j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n] = b[i];
        }
        for (int col = 0; col < n; col++) {
            // Partial pivot.
            int pivot = col;
            for (int row = col + 1; row < n; row++) {
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
            }
            if (pivot != col) {
                for (int j = 0; j <= n; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            }
            if (Math.Abs(m[col, col]) < 1e-12) {
                // Singular, return zeros so the caller treats it as no fit.
                return new double[n];
            }
            // Eliminate other rows.
            for (int row = 0; row < n; row++) {
                if (row == col) continue;
                var factor = m[row, col] / m[col, col];
                for (int j = col; j <= n; j++) m[row, j] -= factor * m[col, j];
            }
        }
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = m[i, n] / m[i, i];
        return x;
    }
}
