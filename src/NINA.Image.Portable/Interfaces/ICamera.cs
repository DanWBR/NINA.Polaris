using NINA.Core.Enum;

namespace NINA.Image.Interfaces;

/// <summary>
/// Common contract every camera backend honours so the rest of Polaris
/// (EquipmentManager, status broadcaster, capture endpoints, sequencer)
/// can stay backend-agnostic. INDI cameras, Alpaca cameras and the
/// vendor-specific DSLR drivers (Canon EDSDK, Nikon SDK, Sony Camera
/// Remote SDK) all implement this.
///
/// Properties that don't make sense for a given backend should return
/// a neutral value (zero, NaN, false) and the corresponding capability
/// flag on <see cref="Capabilities"/> should report false so the UI
/// hides the control.
/// </summary>
public interface ICamera {
    string DeviceName { get; }
    bool IsConnected { get; }
    CameraStates State { get; }

    double Temperature { get; }
    bool CoolerOn { get; }
    double CoolerPower { get; }
    int BinX { get; }
    int BinY { get; }
    int BitDepth { get; }
    int MaxX { get; }
    int MaxY { get; }
    double PixelSizeX { get; }
    double PixelSizeY { get; }

    /// <summary>Astronomy cameras report analogue gain; DSLRs report
    /// ISO via <see cref="SelectedIso"/> + <see cref="IsoOptions"/>.
    /// Backends that don't expose gain return 0.</summary>
    int Gain { get; }

    /// <summary>ISO values the camera supports, in ASA. Empty list
    /// means the backend doesn't expose ISO (typical for dedicated
    /// astronomy cameras).</summary>
    IReadOnlyList<int> IsoOptions { get; }

    /// <summary>Currently-selected ISO, or 0 when the backend doesn't
    /// expose one.</summary>
    int SelectedIso { get; }

    /// <summary>Which optional features this backend supports. Drives
    /// UI affordances — cooler controls hidden when SupportsCooler is
    /// false, ISO dropdown hidden when SupportsIso is false, etc.</summary>
    CameraCapabilities Capabilities { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>Take a single exposure. opts is null-safe — when null,
    /// the backend's current property values are used.</summary>
    Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null, CancellationToken ct = default);

    /// <summary>Backwards-compatible overload for callers that don't
    /// need per-capture overrides — equivalent to passing
    /// <c>opts: null</c>. Lets the existing sequence engine + capture
    /// endpoints keep using <c>CaptureAsync(seconds, ct)</c> verbatim.</summary>
    Task<IImageData> CaptureAsync(double exposureSeconds, CancellationToken ct)
        => CaptureAsync(exposureSeconds, null, ct);

    Task SetBinningAsync(int binX, int binY, CancellationToken ct = default);
    Task SetTemperatureAsync(double temperature, CancellationToken ct = default);
    Task SetCoolerAsync(bool on, CancellationToken ct = default);
    Task SetIsoAsync(int iso, CancellationToken ct = default);
    Task AbortExposureAsync(CancellationToken ct = default);
}

/// <summary>Optional per-exposure overrides. Nulls mean "use the
/// camera's current property value". Set the fields the caller cares
/// about before capture.</summary>
public record CaptureOptions(
    int? Gain = null,
    int? Iso = null,
    int? BinX = null,
    int? BinY = null,
    string? ImageType = null,
    string? Filter = null,
    string? TargetName = null);

/// <summary>Optional-feature flags. Used by the UI to decide which
/// controls to render for the currently-selected camera.</summary>
public record CameraCapabilities(
    bool SupportsCooler,
    bool SupportsBinning,
    bool SupportsRoi,
    bool SupportsIso,
    bool SupportsBulb) {
    /// <summary>Typical astronomy-camera profile (INDI / Alpaca CCDs).</summary>
    public static readonly CameraCapabilities Astro = new(
        SupportsCooler: true, SupportsBinning: true, SupportsRoi: true,
        SupportsIso: false, SupportsBulb: false);

    /// <summary>Typical DSLR / mirrorless profile (Canon, Nikon, Sony).
    /// Exposes ISO + bulb mode; no cooler, no binning, no ROI.</summary>
    public static readonly CameraCapabilities Dslr = new(
        SupportsCooler: false, SupportsBinning: false, SupportsRoi: false,
        SupportsIso: true, SupportsBulb: true);
}
