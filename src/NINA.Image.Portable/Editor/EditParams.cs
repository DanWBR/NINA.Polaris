namespace NINA.Image.Editor;

/// <summary>
/// Full editor edit-parameter graph. Lightroom-style: a single immutable
/// record bundles every slider's current value. All numerics are normalised
/// to "Lightroom feel" ranges (mostly -1..1, exposure in stops) so sliders
/// translate directly to user intuition and JSON sidecars stay human-readable.
///
/// A `null` sub-record means "section at defaults, skip in the pipeline" —
/// the JSON serialiser keeps these out of the sidecar entirely (smaller
/// files, easier diffs).
///
/// Why records-of-records, not a single flat record: the pipeline checks
/// each section's "is this op at default" once per section instead of
/// per-property, and the JSON shape mirrors the Lightroom panel grouping
/// 1:1, which is the right mental model for the user.
/// </summary>
public sealed record EditParams(
    WhiteBalanceParams? WhiteBalance = null,
    LightParams? Light = null,
    ColorParams? Color = null,
    DetailParams? Detail = null,
    EffectsParams? Effects = null,
    ToneCurveParams? ToneCurve = null,
    CropParams? Crop = null,
    int Rotate = 0
) {
    /// <summary>All-defaults instance (no-op pipeline).</summary>
    public static EditParams Defaults => new();
}

/// <summary>
/// Temperature in Kelvin (2000..50000, 6500 = neutral); tint -1..1
/// (negative = magenta, positive = green).
/// </summary>
public sealed record WhiteBalanceParams(double TempK = 6500, double Tint = 0) {
    public bool IsDefault => Math.Abs(TempK - 6500) < 1 && Math.Abs(Tint) < 1e-4;
}

/// <summary>
/// Tonal sliders. Exposure in stops (-5..+5, 0 = no change). The other
/// six are "Lightroom feel": -1..+1, 0 = no change. The pipeline maps
/// each one to a different transform — see EditPipeline.
/// </summary>
public sealed record LightParams(
    double Exposure = 0,
    double Contrast = 0,
    double Highlights = 0,
    double Shadows = 0,
    double Whites = 0,
    double Blacks = 0
) {
    public bool IsDefault =>
        Math.Abs(Exposure) < 1e-4 && Math.Abs(Contrast) < 1e-4 &&
        Math.Abs(Highlights) < 1e-4 && Math.Abs(Shadows) < 1e-4 &&
        Math.Abs(Whites) < 1e-4 && Math.Abs(Blacks) < 1e-4;
}

/// <summary>
/// Vibrance protects already-saturated pixels (gentle on faces, harsh
/// on neon — matches Lightroom). Saturation is a flat HSL scale.
/// Hue rotates the whole wheel (-180..+180 degrees).
/// </summary>
public sealed record ColorParams(
    double Vibrance = 0,
    double Saturation = 0,
    double Hue = 0
) {
    public bool IsDefault =>
        Math.Abs(Vibrance) < 1e-4 && Math.Abs(Saturation) < 1e-4 &&
        Math.Abs(Hue) < 1e-4;
}

/// <summary>
/// SharpenAmount 0..1 (≈ 0.5 = moderate, 1.0 = aggressive).
/// SharpenRadius in pixels (typical 0.5..3).
/// SharpenThreshold in ADU (0..255 for 8-bit, scaled internally).
/// NoiseReduce 0..1 (luminance-only median, conservative by design — for
/// heavy NR the user should run GraXpert Denoise on the FITS first).
/// </summary>
public sealed record DetailParams(
    double SharpenAmount = 0,
    double SharpenRadius = 1.0,
    int SharpenThreshold = 0,
    double NoiseReduce = 0
) {
    public bool IsDefault =>
        Math.Abs(SharpenAmount) < 1e-4 && Math.Abs(NoiseReduce) < 1e-4;
}

/// <summary>
/// Texture: mid-frequency contrast boost (USM, ~3px radius).
/// Clarity: low-frequency contrast (USM, ~20px radius, on luminance).
/// Dehaze: global luminance contrast + saturation boost weighted by
///         distance from white point — clears haze without blowing colour.
/// Vignette: -1..1 (negative = darken corners, positive = brighten).
///           Feather 0..1 (small = harsh edge, large = smooth gradient).
/// </summary>
public sealed record EffectsParams(
    double Texture = 0,
    double Clarity = 0,
    double Dehaze = 0,
    double VignetteAmount = 0,
    double VignetteFeather = 0.5
) {
    public bool IsDefault =>
        Math.Abs(Texture) < 1e-4 && Math.Abs(Clarity) < 1e-4 &&
        Math.Abs(Dehaze) < 1e-4 && Math.Abs(VignetteAmount) < 1e-4;
}

/// <summary>
/// Tone curve. Each list is a sorted set of (x,y) anchors in 0..255 space.
/// Null = identity (skip). RGB is the master curve, applied after the
/// per-channel curves. A null RGB with non-null per-channel still runs
/// the per-channel pass.
/// </summary>
public sealed record ToneCurveParams(
    IReadOnlyList<CurvePoint>? Rgb = null,
    IReadOnlyList<CurvePoint>? R = null,
    IReadOnlyList<CurvePoint>? G = null,
    IReadOnlyList<CurvePoint>? B = null
) {
    public bool IsDefault =>
        (Rgb == null || IsIdentity(Rgb)) &&
        (R == null || IsIdentity(R)) &&
        (G == null || IsIdentity(G)) &&
        (B == null || IsIdentity(B));

    private static bool IsIdentity(IReadOnlyList<CurvePoint> p)
        => p.Count == 2 && p[0].X == 0 && p[0].Y == 0 && p[1].X == 255 && p[1].Y == 255;
}

public sealed record CurvePoint(double X, double Y);

/// <summary>
/// Rectangular crop in source-image pixel coordinates. Origin top-left.
/// X/Y/Width/Height all non-negative; pipeline clamps to image bounds.
/// </summary>
public sealed record CropParams(int X, int Y, int Width, int Height);
