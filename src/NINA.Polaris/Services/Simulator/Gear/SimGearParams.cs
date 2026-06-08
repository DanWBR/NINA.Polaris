// Ported to C# from PHD2 (OpenPHDGuiding) src/gear_simulator.cpp.
//
// PHD2 is Copyright (c) Craig Stark, Bret McKee, Dad Dog Development Ltd.
// Licensed under the BSD 3-Clause License. See licenses/PHD2-LICENSE.txt.
//
// This file mirrors the SimCamParams tunables (and their defaults) that drive
// the simulated star field + error model. UI-facing units are arc-seconds; the
// pixel-domain conversions follow load_sim_params() in the original.

using NINA.Core.Enum;

namespace NINA.Polaris.Services.Simulator.Gear;

/// <summary>
/// Tunables for the built-in gear simulator, ported from PHD2's SimCamParams.
/// Defaults reproduce PHD2's out-of-the-box behaviour. Error sources can be
/// toggled off individually (<see cref="UsePeriodicError"/>,
/// <see cref="UseDrift"/>, <see cref="UseSeeing"/>) for deterministic tests.
/// </summary>
public sealed class SimGearParams {
    // ----- Sensor geometry -----
    public int Width { get; set; } = 752;     // SimCamParams::width
    public int Height { get; set; } = 580;    // SimCamParams::height
    public int Border { get; set; } = 12;     // keep stars off the edges
    public int Stars { get; set; } = 20;      // NR_STARS_DEFAULT
    public int HotPixels { get; set; } = 8;   // NR_HOT_PIXELS_DEFAULT

    /// <summary>Arc-seconds per pixel. The conversion factor between the
    /// arc-second UI tunables below and the pixel-domain shifts the renderer
    /// applies. PHD2 derives this from the configured guide-cam pixel scale.</summary>
    public double ImageScale { get; set; } = 1.0;

    // ----- Mount / guiding -----
    /// <summary>Guide rate in arc-seconds per second (PHD2 GUIDE_RATE_DEFAULT =
    /// 1x sidereal = 15 a-s/s). Drives the pulse -> pixel conversion in ST4.</summary>
    public double GuideRateArcsecPerSec { get; set; } = 15.0;

    // ----- Error model (UI units = arc-seconds) -----
    public bool UsePeriodicError { get; set; } = true;
    /// <summary>Peak periodic-error amplitude in arc-seconds (PE_SCALE_DEFAULT).</summary>
    public double PeAmplitudeArcsec { get; set; } = 5.0;

    public bool UseDrift { get; set; } = true;
    /// <summary>Declination drift in arc-seconds per minute (DEC_DRIFT_DEFAULT).</summary>
    public double DecDriftArcsecPerMin { get; set; } = 5.0;

    public bool UseSeeing { get; set; } = true;
    /// <summary>Seeing FWHM in arc-seconds (SEEING_DEFAULT).</summary>
    public double SeeingArcsecFwhm { get; set; } = 2.0;

    /// <summary>Declination backlash in arc-seconds (DEC_BACKLASH_DEFAULT).</summary>
    public double DecBacklashArcsec { get; set; } = 5.0;

    /// <summary>Camera rotation in degrees (CAM_ANGLE_DEFAULT).</summary>
    public double CameraAngleDeg { get; set; } = 15.0;

    /// <summary>Starting pier side. PHD2 PIER_SIDE_DEFAULT = East.</summary>
    public PierSide PierSide { get; set; } = PierSide.pierEast;

    /// <summary>When true, North/South Dec pulses reverse after a flip to the
    /// west side (REVERSE_DEC_PULSE_ON_WEST_SIDE_DEFAULT). This is what lets
    /// the native guider's pier-side handling be exercised.</summary>
    public bool ReverseDecOnWestSide { get; set; } = true;

    // ----- Rendering -----
    /// <summary>Per-unit-intensity star brightness multiplier. Chosen so the
    /// always-saturated star (intensity 30.1) clips at 16-bit and dim stars sit
    /// just above the noise floor.</summary>
    public double StarGain { get; set; } = 4500.0;
    /// <summary>Background pedestal in ADU.</summary>
    public double Background { get; set; } = 800.0;
    /// <summary>Per-pixel Gaussian read-noise sigma in ADU, before the
    /// multiplier (NOISE_DEFAULT = 2.0).</summary>
    public double NoiseSigma { get; set; } = 120.0;
    public double NoiseMultiplier { get; set; } = 2.0;

    // ----- Derived (pixel domain) -----
    public double DecBacklashPx => ImageScale > 0 ? DecBacklashArcsec / ImageScale : DecBacklashArcsec;
    public double DecDriftPxPerSec => ImageScale > 0 ? DecDriftArcsecPerMin / (ImageScale * 60.0) : 0;

    public SimGearParams Clone() => (SimGearParams)MemberwiseClone();
}
