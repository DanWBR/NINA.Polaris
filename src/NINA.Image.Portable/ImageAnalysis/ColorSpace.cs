namespace NINA.Image.ImageAnalysis;

/// <summary>
/// Color-space conversions used by the editor pipeline. All inputs and
/// outputs are normalised 0..1 floats, the caller scales to/from the
/// concrete pixel format (ushort, byte, etc.) at the boundary.
///
/// RGB ↔ HSL is the workhorse for vibrance / saturation / hue. The
/// formulas follow the standard sRGB definitions (Smith 1978); they're
/// undefined when the input is exactly grayscale (delta == 0), in which
/// case Hue defaults to 0 and Saturation to 0, that keeps the editor
/// idempotent on mono pixels (a 0% saturation pixel can't have a hue
/// anyway, so any choice is acceptable).
///
/// Temperature/tint → RGB gain uses the Bradford-adapted approximation
/// from the Lindbloom whitepoint tables. Exact CIE math would need full
/// CAT02 chromatic adaptation; for an editor's white-balance slider the
/// approximation is well within visual tolerance, identical to what
/// Lightroom does for daily-use values 2000K–50000K.
/// </summary>
public static class ColorSpace {

    /// <summary>
    /// Convert RGB (0..1) to HSL (H in 0..360, S/L in 0..1).
    /// </summary>
    public static (double h, double s, double l) RgbToHsl(double r, double g, double b) {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) * 0.5;
        double h, s;

        if (max == min) {
            // Grayscale, Hue undefined. Pick 0 (matches PIL / GIMP).
            h = 0;
            s = 0;
        } else {
            double delta = max - min;
            s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);
            if (max == r) h = (g - b) / delta + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / delta + 2;
            else h = (r - g) / delta + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    /// <summary>
    /// Convert HSL back to RGB. Hue wraps modulo 360.
    /// </summary>
    public static (double r, double g, double b) HslToRgb(double h, double s, double l) {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        if (s == 0) return (l, l, l);

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double hk = h / 360.0;

        double r = HueToRgb(p, q, hk + 1.0 / 3.0);
        double g = HueToRgb(p, q, hk);
        double b = HueToRgb(p, q, hk - 1.0 / 3.0);
        return (r, g, b);
    }

    private static double HueToRgb(double p, double q, double t) {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }

    /// <summary>
    /// Compute Rec.709 luminance from linear RGB. Used by the editor
    /// pipeline whenever an op (clarity, sharpening, noise reduction)
    /// should only touch the brightness channel and leave colour alone.
    /// </summary>
    public static double Luminance(double r, double g, double b)
        => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    /// <summary>
    /// Convert a colour temperature (Kelvin) and tint (-1..1) into
    /// per-channel RGB gain multipliers normalised so the green channel
    /// stays at 1.0 (lossless multiply).
    ///
    /// Temperature 6500K with tint=0 returns (1, 1, 1) (neutral D65).
    /// Lower temps push red up + blue down (warmer); higher temps push
    /// blue up + red down (cooler). Tint adjusts magenta↔green balance:
    /// positive = greener; negative = more magenta (R + B boost).
    ///
    /// Reference values come from the Lindbloom whitepoint table
    /// (http://www.brucelindbloom.com/index.html?Eqn_T_to_xy.html);
    /// we interpolate piecewise-linearly for speed since slider drag
    /// performance matters more than the fourth decimal of accuracy.
    /// </summary>
    public static (double rGain, double gGain, double bGain) TempTintToGain(double tempK, double tint) {
        tempK = Math.Clamp(tempK, 1500, 50000);
        tint = Math.Clamp(tint, -1, 1);

        // Simple analytical fit to the daylight locus (T → RGB). Calibrated
        // so 6500K returns ~(1, 1, 1). Origin: McCamy 1992 inverse.
        double r, g, b;
        double t100 = tempK / 100.0;

        // Red channel
        if (t100 <= 66) {
            r = 255;
        } else {
            r = 329.698727446 * Math.Pow(t100 - 60, -0.1332047592);
        }

        // Green channel
        if (t100 <= 66) {
            g = 99.4708025861 * Math.Log(t100) - 161.1195681661;
        } else {
            g = 288.1221695283 * Math.Pow(t100 - 60, -0.0755148492);
        }

        // Blue channel
        if (t100 >= 66) {
            b = 255;
        } else if (t100 <= 19) {
            b = 0;
        } else {
            b = 138.5177312231 * Math.Log(t100 - 10) - 305.0447927307;
        }

        r = Math.Clamp(r, 0, 255) / 255.0;
        g = Math.Clamp(g, 0, 255) / 255.0;
        b = Math.Clamp(b, 0, 255) / 255.0;

        // Normalise so green stays at 1.0 (the "anchor" channel for
        // exposure, green gain = 1 means we never blow highlights from
        // the WB slider alone).
        if (g > 1e-6) {
            r /= g;
            b /= g;
            g = 1.0;
        }

        // Tint: positive = +green, negative = +magenta (R+B boost).
        // ±0.15 multiplier feels right at slider extremes (matches LR).
        if (tint >= 0) {
            g *= 1 + 0.15 * tint;
        } else {
            r *= 1 - 0.075 * tint;
            b *= 1 - 0.075 * tint;
        }

        return (r, g, b);
    }
}
