// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Linq;
using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;
using NINA.Image.FileFormat.XISF;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Saves captured images to disk as FITS / XISF with extended headers built
/// from the currently-connected equipment state (telescope, filter wheel,
/// focuser, rotator, weather) and the active profile (observer, site, target).
///
/// File naming honours <c>ProfileService.Active.ImageNamePattern</c>; the
/// following placeholders are recognised (NINA convention):
///   {target}    {filter}   {exposure}   {gain}   {binning}   {bitdepth}
///   {date}      {time}     {datetime}   {framenr} {seq}
///   {camera}    {temp}     {imagetype}
/// Missing tokens are silently substituted with "Unknown" so the file always
/// has a well-formed name even if equipment isn't reporting metadata.
///
/// Files are organised under <c>ImageOutputDir</c> in a fixed layout so the
/// STUDIO panel can match calibration frames to lights without scanning
/// every header. The shape is:
///
///   ImageOutputDir/
///     {rig}/                                     ← active equipment-profile name
///       lights/{target}/{filter}/{session}/      ← session = local night (noon-to-noon)
///         light_*.fits
///       calibration/                             ← rig-level, reusable across sessions
///         dark/{exposure}s_g{gain}/dark_*.fits
///         bias/g{gain}/bias_*.fits
///         darkflat/{exposure}s_g{gain}/darkflat_*.fits
///         flat/{filter}_g{gain}/flat_*.fits
///         masters/master_*.fits                  (written by STUDIO ST-3)
///       calibrated/{target}/{filter}/...         (written by STUDIO ST-4)
///       integrated/{target}/{filter}/...         (written by STUDIO ST-5)
///       processed/{target}/...                   (written by STUDIO ST-7, TIFF/PNG/JPEG)
///
/// Rig + session rationale:
///   - **Rig as top-level** means each optical chain (different scope,
///     camera, focal reducer) gets its own self-contained archive. Master
///     darks/biases/flats belong to a specific sensor at a specific
///     temperature setpoint and gain, they're not transferable. Putting
///     them under the rig prevents cross-contamination when the user
///     switches setups.
///   - **Session = astronomical night**. A capture started at 02:30 local
///     time still belongs to the previous evening's session. Computed
///     with a noon-to-noon rollover so the date in the folder name is
///     the date the night *started*, matching how astronomers describe
///     observation runs.
///   - **Calibration stays per-rig (not per-session)** so masters can be
///     reused across nights, typical PixInsight workflow. Raw cal
///     frames accumulate in the same bucket regardless of which night
///     they were shot, then STUDIO ST-3 integrates them into masters.
///
/// The sub-path is derived from IMAGETYP. The filename pattern still
/// controls just the leaf name. Pre-existing flat layouts keep being
/// indexed by the FrameLibraryService scan since it walks recursively.
/// </summary>
public class ImageWriterService {
    private readonly EquipmentManager _equip;
    private readonly ProfileService _profile;
    private readonly ILogger<ImageWriterService> _logger;

    private int _sessionFrameNumber;
    private string? _lastWrittenPath;

    public string? LastWrittenPath => _lastWrittenPath;
    public int SessionFrameCount => _sessionFrameNumber;

    /// <summary>Raised with the absolute path right after a frame is written to
    /// disk. The single post-save choke point — every SaveImage call site
    /// (sequence, live stack, flat wizard, ADV sequencer) funnels through here.
    /// Used by <see cref="StoragePushService"/> to auto-push to network storage.
    /// Handlers must be fast + non-throwing; the invocation is wrapped so a
    /// subscriber can never break a capture.</summary>
    public event Action<string>? ImageSaved;

    private readonly SkyCatalogService? _sky;
    private readonly PlateSolveService? _plateSolve;
    private readonly ActiveGuiderProvider? _guiders;

    public ImageWriterService(EquipmentManager equip, ProfileService profile,
        ILogger<ImageWriterService> logger,
        SkyCatalogService? sky = null, PlateSolveService? plateSolve = null,
        ActiveGuiderProvider? guiders = null) {
        _equip = equip;
        _profile = profile;
        _logger = logger;
        _sky = sky;
        _plateSolve = plateSolve;
        _guiders = guiders;
    }

    public void ResetSessionCounter() => _sessionFrameNumber = 0;

    /// <summary>True when an image output directory is configured. When false,
    /// <see cref="SaveImage"/> silently no-ops, so callers (e.g. the live stack)
    /// can surface a "frames not being saved — set an output folder" warning
    /// instead of dropping frames quietly.</summary>
    public bool HasOutputDir =>
        !string.IsNullOrWhiteSpace(_profile.Active.ImageOutputDir);

    /// <summary>Save the image to disk and return the resulting path, or null
    /// if disabled / output dir missing.</summary>
    public string? SaveImage(IImageData imageData,
        string? targetName = null,
        string imageType = "LIGHT",
        int gain = 0,
        bool stacked = false,
        double? focalLengthMmOverride = null) {

        var profile = _profile.Active;
        var dir = profile.ImageOutputDir;
        if (string.IsNullOrWhiteSpace(dir)) {
            _logger.LogWarning("ImageWriter: no output folder configured — frame NOT saved. " +
                "Set an image output folder in the FILES tab to keep individual frames.");
            return null;
        }

        try {
            Directory.CreateDirectory(dir);

            // When the caller didn't supply a meaningful target name (LIVE
            // "(unnamed)", a blank AUTORUN target, etc.), auto-resolve the
            // most important catalog object in the camera FOV at the current
            // pointing — preferring the last successful plate solve — so the
            // saved-frame folder gets a real name instead of "Unknown".
            // Only for science frames (lights/snaps); calibration frames are
            // foldered by exposure/filter and have no sky target.
            targetName = ResolveTargetName(targetName, imageType, imageData, profile);

            EnrichMetadata(imageData, profile, targetName, imageType, gain);
            // Aux camera frames carry the aux optics' focal length (different
            // OTA than the main rig), so FOV/plate-solve metadata is correct.
            if (focalLengthMmOverride is > 0)
                imageData.MetaData.Telescope.FocalLength = focalLengthMmOverride.Value;
            _sessionFrameNumber++;

            // DSLR / mirrorless drivers attach the camera-native RAW
            // bytes via IHasRawFile. When present, the raw is the
            // authoritative on-disk artefact, we save it verbatim
            // instead of generating a FITS / XISF (which would only
            // hold the embedded JPEG we use for the live preview).
            var hasRaw = imageData is IHasRawFile rf
                         && rf.RawFileBytes != null
                         && !string.IsNullOrEmpty(rf.RawFileExtension);

            var format = (profile.ImageFormat ?? "fits").Trim().ToLowerInvariant();
            var extension = hasRaw
                ? ((IHasRawFile)imageData).RawFileExtension!
                : (format switch { "xisf" => ".xisf", _ => ".fits" });

            var pattern = string.IsNullOrWhiteSpace(profile.ImageNamePattern)
                ? "{target}_{filter}_{exposure}s_g{gain}_{temp}C_{datetime}_{seq}"
                : profile.ImageNamePattern;
            var fileName = SubstitutePattern(pattern, imageData, _sessionFrameNumber) + extension;
            // Sanitise illegal filename characters
            foreach (var c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
            // Spaces aren't illegal but make for awkward paths/URLs; collapse
            // them to underscores so e.g. a "M 31" target writes M_31_... ,
            // matching the underscore convention SanitizeFolder uses on dirs.
            fileName = fileName.Replace(' ', '_');

            // Standard subdirectory layout, keeps lights / calibration /
            // STUDIO outputs separated so the post-processing pipeline can
            // find matching darks by exposure+gain (and flats by filter+gain)
            // without scanning every header. Frames also bucketed under the
            // active rig and the astronomical session date.
            var rigName = _profile.ActiveEquipmentProfile?.Name ?? "Default";
            var sessionDate = SessionDateForLocal(imageData.MetaData.CreationTime.ToLocalTime());
            // User-requested stacked saves go into their own "stacked" tree
            // ({rig}/stacked/{target}/{filter}/{session}) so the integrated
            // master sits apart from the raw lights/calibration frames.
            var subDir = stacked
                ? BuildStackedSubDir(imageData, rigName, sessionDate)
                : BuildSubDir(imageType, imageData, profile, rigName, sessionDate);
            var targetDir = string.IsNullOrEmpty(subDir) ? dir : Path.Combine(dir, subDir);
            Directory.CreateDirectory(targetDir);
            var fullPath = Path.Combine(targetDir, fileName);

            // Avoid clobber: append _N if exists
            int copy = 1;
            while (File.Exists(fullPath)) {
                var name = Path.GetFileNameWithoutExtension(fileName);
                fullPath = Path.Combine(targetDir, $"{name}_{copy++}{extension}");
            }

            RotatorMetaData? rotMeta = null;
            if (_equip.Rotator != null && _equip.Rotator.IsConnected) {
                var ang = _equip.Rotator.Position;
                rotMeta = new RotatorMetaData {
                    Name = _equip.Rotator.DeviceName,
                    Angle = double.IsNaN(ang) ? 0 : ang
                };
            }

            if (hasRaw) {
                File.WriteAllBytes(fullPath, ((IHasRawFile)imageData).RawFileBytes!);
                _logger.LogInformation("Saved RAW ({Ext}): {Path}",
                    extension, fullPath);
            } else if (format == "xisf") {
                XISFWriter.Write(imageData, fullPath, rotator: rotMeta);
                _logger.LogInformation("Saved XISF: {Path}", fullPath);
            } else {
                FITSWriter.Write(imageData, fullPath, rotator: rotMeta);
                _logger.LogInformation("Saved FITS: {Path}", fullPath);
            }
            _lastWrittenPath = fullPath;
            // Fire-and-forget notify for auto-push to network storage. Wrapped
            // so a misbehaving subscriber never fails the capture.
            try { ImageSaved?.Invoke(fullPath); }
            catch (Exception ex) { _logger.LogDebug(ex, "ImageSaved handler threw"); }
            return fullPath;
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to save FITS to {Dir}", dir);
            return null;
        }
    }

    /// <summary>Names that mean "no real target" and should trigger an
    /// auto-resolve from the sky position.</summary>
    private static readonly HashSet<string> PlaceholderTargetNames =
        new(StringComparer.OrdinalIgnoreCase) {
            "unnamed", "(unnamed)", "unknown", "default", "target", "none", "n/a", "na"
        };

    /// <summary>
    /// Resolve a meaningful target name for a science frame about to be saved.
    /// If the caller already supplied a real name it's kept; otherwise the most
    /// important catalog object inside the camera FOV at the current pointing is
    /// used — preferring a recent successful plate solve over the mount's
    /// open-loop coordinates. Keeps capture folders named (e.g. "M_42") instead
    /// of "Unknown". Returns the original value when nothing can be resolved.
    /// </summary>
    private string? ResolveTargetName(string? targetName, string imageType,
        IImageData imageData, UserProfile profile) {

        var trimmed = (targetName ?? "").Trim();
        if (!string.IsNullOrEmpty(trimmed) && !PlaceholderTargetNames.Contains(trimmed))
            return trimmed;   // caller gave a real name — respect it

        // Only science frames carry a sky target; calibration frames are
        // foldered by exposure/filter and have no object.
        var typeUpper = (imageType ?? "LIGHT").Trim().ToUpperInvariant();
        if (typeUpper is "DARK" or "BIAS" or "FLAT" or "DARKFLAT") return targetName;
        if (_sky == null) return targetName;

        // Pointing: mount RA/Dec when connected, plus the last successful solve.
        double? ra = null, dec = null;
        var tel = _equip.Telescope;
        bool mountOk = tel != null && tel.IsConnected
            && !double.IsNaN(tel.RightAscension) && !double.IsNaN(tel.Declination);
        if (mountOk) { ra = tel!.RightAscension; dec = tel.Declination; }

        var solve = _plateSolve?.LastSuccessfulSolve;
        if (solve != null) {
            // Prefer the solve when there's no mount reading, or when it agrees
            // with the mount pointing (it's the solve for THIS field, tighter
            // than open-loop coords). A stale solve on another target is
            // rejected by the 5° separation gate.
            if (!mountOk) { ra = solve.RaHours; dec = solve.DecDeg; }
            else if (AngularSepDeg(ra!.Value, dec!.Value, solve.RaHours, solve.DecDeg) <= 5.0) {
                ra = solve.RaHours; dec = solve.DecDeg;
            }
        }

        if (ra == null || dec == null) return targetName;

        double fovRadius = ComputeFovRadiusDeg(imageData);
        try {
            var hit = _sky.Identify(ra.Value, dec.Value, fovRadius);
            var name = hit?.Object?.Name;
            if (!string.IsNullOrWhiteSpace(name)) {
                _logger.LogInformation(
                    "Auto-resolved capture target '{Name}' at RA={Ra:F3}h Dec={Dec:F2}° (FOV r={Fov:F2}°)",
                    name, ra, dec, fovRadius);
                return name!.Trim();
            }
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Target auto-resolve failed");
        }
        return targetName;
    }

    /// <summary>Half the larger camera FOV dimension (degrees) from the active
    /// rig focal length + camera pixel size + image dimensions. Falls back to
    /// 1° when the geometry isn't known.</summary>
    private double ComputeFovRadiusDeg(IImageData imageData) {
        try {
            double fl = _profile.ActiveEquipmentProfile?.FocalLengthMm ?? 0;
            double pix = _equip.Camera?.PixelSizeY ?? _equip.Camera?.PixelSizeX ?? 0;
            int w = imageData.Properties.Width, h = imageData.Properties.Height;
            if (fl > 0 && pix > 0 && w > 0 && h > 0) {
                double wmm = pix * w / 1000.0, hmm = pix * h / 1000.0;
                double fovW = 2.0 * Math.Atan(wmm / (2.0 * fl)) * (180.0 / Math.PI);
                double fovH = 2.0 * Math.Atan(hmm / (2.0 * fl)) * (180.0 / Math.PI);
                double r = Math.Max(fovW, fovH) / 2.0;
                if (r > 0 && r < 20) return r;
            }
        } catch { /* fall through to default */ }
        return 1.0;
    }

    /// <summary>Great-circle separation in degrees. RA in hours, Dec in degrees.</summary>
    private static double AngularSepDeg(double ra1H, double dec1D, double ra2H, double dec2D) {
        double D2R = Math.PI / 180.0;
        double ra1 = ra1H * 15.0 * D2R, ra2 = ra2H * 15.0 * D2R;
        double d1 = dec1D * D2R, d2 = dec2D * D2R;
        double dRa = ra2 - ra1, dDec = d2 - d1;
        double a = Math.Sin(dDec / 2) * Math.Sin(dDec / 2)
                 + Math.Cos(d1) * Math.Cos(d2) * Math.Sin(dRa / 2) * Math.Sin(dRa / 2);
        return 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)) / D2R;
    }

    /// <summary>Fill <see cref="ImageMetaData.Guiding"/> from the guide steps
    /// recorded inside this frame's exposure window. No-ops when the frame
    /// already carries stats, when no guider is attached, or when the exposure
    /// time is unknown — the header then simply omits the guiding keys rather
    /// than claiming a zero RMS, which would read as perfect tracking.</summary>
    private void StampGuiding(ImageMetaData m) {
        if (m.Guiding.SampleCount > 0) return;
        var guider = _guiders?.Active;
        if (guider == null || !guider.IsConnected) return;
        var exposureSec = m.Exposure.ExposureTime;
        if (!(exposureSec > 0)) return;
        try {
            var end = m.CreationTime;
            var start = end.AddSeconds(-exposureSec);
            m.Guiding = GuidingStatsCollector.Summarise(
                guider.SnapshotSteps(), start, end, guider.Backend);
        } catch (Exception ex) {
            // A header nicety must never cost a frame.
            _logger.LogDebug(ex, "Guiding stats for the frame header failed");
        }
    }

    private void EnrichMetadata(IImageData imageData, UserProfile profile,
        string? targetName, string imageType, int gain) {
        var m = imageData.MetaData;

        // Exposure
        if (string.IsNullOrEmpty(m.Exposure.ImageType)) m.Exposure.ImageType = imageType;
        m.Exposure.ExposureNumber = _sessionFrameNumber + 1;

        // Camera gain. Priority: whatever the capture already stamped >
        // the explicit gain arg from the caller > the live value read off
        // the connected camera. That last fallback is the fix for GAIN
        // missing from some saved FITS: several save paths don't pass a
        // gain (flat wizard, live-stack auto-save, client-saved stacks)
        // AND not every camera driver stamps Gain into the per-frame
        // metadata, so without reading the connected camera those frames
        // ended up with no GAIN header. (FITSWriter only emits GAIN when
        // it's non-zero, so a genuine 0-gain capture still omits it.)
        if (m.Camera.Gain == 0) {
            if (gain > 0) m.Camera.Gain = gain;
            else if (_equip.Camera is { IsConnected: true } gcam && gcam.Gain > 0)
                m.Camera.Gain = gcam.Gain;
        }
        // Camera offset, the same gap class as gain: not every driver stamps
        // OFFSET into the per-frame metadata (and ICamera exposes no live
        // Offset to read back the way Gain can be), so several save paths
        // dropped it entirely and the FITS carried no OFFSET card. Fill from
        // the rig's configured DefaultOffset, which is what actually got
        // applied at capture. FITSWriter only emits OFFSET when non-zero,
        // matching the GAIN behaviour.
        if (m.Camera.Offset == 0) {
            var rigOffset = _profile.ActiveEquipmentProfile?.DefaultOffset ?? 0;
            if (rigOffset > 0) m.Camera.Offset = rigOffset;
        }

        // How the mount tracked DURING this exposure, so the night's subs can
        // be sorted by guiding and the effect on the stars judged from the
        // files. Scoped to the exposure window on purpose: the guider's own
        // running RMS covers the whole session, so one gust would drag every
        // frame's number down equally and the correlation would be lost.
        // CreationTime is stamped when the frame arrives, so the window ends
        // there and reaches back one exposure.
        StampGuiding(m);
        // Camera identity, same gap: fill from the connected camera when
        // the capture didn't stamp a name (drives CAMERAID / INSTRUME).
        if (string.IsNullOrEmpty(m.Camera.Name)
                && _equip.Camera is { IsConnected: true } ncam)
            m.Camera.Name = ncam.DeviceName;

        // Bayer pattern, same gap class — this is the fix for OSC frames
        // that occasionally save WITHOUT a BAYERPAT card and reopen as
        // "mono" (raw mosaic shown in the editor). Two distinct holes
        // funnel through this one chokepoint:
        //   (1) The ASI native SDK adapter only stamps
        //       Properties.BayerPattern, never MetaData.Camera.BayerPattern,
        //       yet both FITSWriter and XISFWriter emit BAYERPAT from the
        //       MetaData side — so ASI OSC saves never carried the card.
        //   (2) INDI OSC drivers advertise the CFA via the CCD_CFA property
        //       (read live per frame); when that property is momentarily
        //       absent (right after a reconnect / driver re-publish) a frame
        //       comes back with BayerPattern=None even on a colour sensor.
        // Priority: trust the frame's own Properties pattern first; fall
        // back to the per-rig override the operator configured for a driver
        // that mis-reports / omits the CFA. The override here is the native
        // (unflipped) sensor pattern: the relay's VerticalFlip row-shift is
        // a wire/display-only concern and the on-disk buffer is in native
        // orientation, so it must NOT be row-shifted.
        if (m.Camera.BayerPattern == BayerPatternEnum.None) {
            var effective = imageData.Properties.BayerPattern;
            if (effective == BayerPatternEnum.None)
                effective = ParseBayerOverride(
                    _profile.ActiveEquipmentProfile?.BayerPatternOverride)
                    ?? BayerPatternEnum.None;
            if (effective != BayerPatternEnum.None) {
                m.Camera.BayerPattern = effective;
                m.Camera.SensorType = effective switch {
                    BayerPatternEnum.RGGB => SensorType.RGGB,
                    BayerPatternEnum.BGGR => SensorType.BGGR,
                    BayerPatternEnum.GBRG => SensorType.GBRG,
                    BayerPatternEnum.GRBG => SensorType.GRBG,
                    _ => m.Camera.SensorType
                };
            }
        }

        // Telescope, focal length comes from the *active rig* (a per-rig
        // optic property), falling back to the legacy profile value only if
        // no rigs have been set up.
        if (_equip.Telescope != null && _equip.Telescope.IsConnected) {
            var rigFocalLen = _profile.ActiveEquipmentProfile.FocalLengthMm;
            var focalLength = rigFocalLen > 0 ? rigFocalLen : profile.FocalLengthMm;
            m.Telescope.Name = _equip.Telescope.DeviceName;
            // OTA brand+model is a per-rig optic property kept distinct from the
            // mount device name (TELESCOP); it drives the "OTA" FITS/XISF keyword.
            var ota = string.Join(" ", new[] {
                _profile.ActiveEquipmentProfile.TelescopeBrand,
                _profile.ActiveEquipmentProfile.TelescopeModel
            }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
            if (!string.IsNullOrWhiteSpace(ota))
                m.Telescope.OpticalTube = ota;
            m.Telescope.FocalLength = focalLength;
            if (focalLength > 0 && profile.SensorWidthMm > 0)
                m.Telescope.FocalRatio = focalLength /
                    Math.Max(profile.SensorWidthMm, 1);
            m.Telescope.RightAscension = Safe(_equip.Telescope.RightAscension);
            m.Telescope.Declination = Safe(_equip.Telescope.Declination);
            m.Telescope.Altitude = Safe(_equip.Telescope.Altitude);
            m.Telescope.Azimuth = Safe(_equip.Telescope.Azimuth);
            m.Telescope.SideOfPier = _equip.Telescope.SideOfPier;
        }

        // Filter wheel
        if (_equip.FilterWheel != null) {
            m.FilterWheel.Name = _equip.FilterWheel.DeviceName;
            m.FilterWheel.Filter = _equip.FilterWheel.CurrentFilterName ?? "";
            m.FilterWheel.Position = _equip.FilterWheel.Position;
            if (string.IsNullOrEmpty(m.Exposure.Filter))
                m.Exposure.Filter = _equip.FilterWheel.CurrentFilterName ?? "";
        } else {
            // No filter wheel: stamp the active rig's fixed "Attached Filter"
            // code (a screwed-in LP / narrowband filter) so the FITS FILTER
            // keyword and the {filter} filename token still record it.
            var attached = _profile.ActiveEquipmentProfile?.AttachedFilter;
            if (string.IsNullOrEmpty(m.Exposure.Filter)
                    && !string.IsNullOrEmpty(attached))
                m.Exposure.Filter = attached;
        }

        // Focuser
        if (_equip.Focuser != null) {
            m.Focuser.Name = _equip.Focuser.DeviceName;
            m.Focuser.Position = _equip.Focuser.Position;
            var t = _equip.Focuser.Temperature;
            m.Focuser.Temperature = double.IsNaN(t) ? 0 : t;
        }

        // Weather
        if (_equip.Weather != null && _equip.Weather.IsConnected) {
            m.Weather.Temperature = Safe(_equip.Weather.Temperature);
            m.Weather.Humidity = Safe(_equip.Weather.Humidity);
            m.Weather.DewPoint = Safe(_equip.Weather.DewPoint);
            m.Weather.Pressure = Safe(_equip.Weather.Pressure);
            m.Weather.SkyQuality = Safe(_equip.Weather.SkyQuality);
        }

        // Observer / site
        m.Observer.Latitude = profile.Latitude;
        m.Observer.Longitude = profile.Longitude;
        m.Observer.Elevation = profile.Altitude;

        // Target
        if (!string.IsNullOrEmpty(targetName)) {
            m.Target.Name = targetName;
            // If telescope is slewed to a target, use its current coords as planned
            if (m.Telescope.RightAscension != 0 || m.Telescope.Declination != 0) {
                if (m.Target.RightAscension == 0) m.Target.RightAscension = m.Telescope.RightAscension;
                if (m.Target.Declination == 0) m.Target.Declination = m.Telescope.Declination;
            }
        }
    }

    private static double Safe(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;

    /// <summary>Parse a per-rig Bayer override string into a concrete
    /// pattern, or null when it's empty / "Auto" / "None" / unrecognised
    /// (in which case the caller leaves the pattern untouched). Mirrors the
    /// parsing rules in <c>ImageRelayService.ResolveBayerOverride</c> so the
    /// saved file and the live display agree on the sensor pattern.</summary>
    private static BayerPatternEnum? ParseBayerOverride(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (string.Equals(raw, "Auto", StringComparison.OrdinalIgnoreCase)) return null;
        if (Enum.TryParse<BayerPatternEnum>(raw, ignoreCase: true, out var p)
                && p != BayerPatternEnum.None
                && p != BayerPatternEnum.Auto) {
            return p;
        }
        return null;
    }

    /// <summary>
    /// Pick the structured subdirectory under ImageOutputDir for a frame
    /// based on the active rig + IMAGETYP. Lights live under
    /// {rig}/lights/{target}/{filter}/{session}; calibration frames are
    /// grouped by the keys that matter for matching them to lights later
    /// (exposure + gain for darks, gain for bias, filter + gain for
    /// flats). Calibration is rig-level (no session bucket) so masters
    /// can be reused across nights.
    /// </summary>
    public static string BuildSubDir(string imageType, IImageData img, UserProfile profile,
                                     string rigName, DateTime sessionDate) {
        var m = img.MetaData;
        var typeUpper = (imageType ?? "LIGHT").Trim().ToUpperInvariant();
        var rig      = SanitizeFolder(string.IsNullOrEmpty(rigName) ? "Default" : rigName);
        var filter   = SanitizeFolder(string.IsNullOrEmpty(m.Exposure.Filter) ? "L" : m.Exposure.Filter);
        var gain     = m.Camera.Gain;
        var exposure = m.Exposure.ExposureTime;

        var subPath = typeUpper switch {
            "DARK"      => Path.Combine("calibration", "dark",
                            FormattableString.Invariant($"{exposure:0.##}s_g{gain}")),
            "BIAS"      => Path.Combine("calibration", "bias",
                            FormattableString.Invariant($"g{gain}")),
            "DARKFLAT"  => Path.Combine("calibration", "darkflat",
                            FormattableString.Invariant($"{exposure:0.##}s_g{gain}")),
            "FLAT"      => Path.Combine("calibration", "flat",
                            FormattableString.Invariant($"{filter}_g{gain}")),
            // PREVIEW-tab snaps live in their own tree so they don't
            // mix with the science lights from a sequence. Folder is
            // {rig}/snaps/{filter}_{session-date}/{snap_NNNN}.fits.
            "SNAP"      => Path.Combine("snaps",
                            FormattableString.Invariant(
                                $"{filter}_{sessionDate:yyyy-MM-dd}")),
            // Auxiliary (second) camera frames live in their own aux/ tree so
            // they never mix with the main camera's lights, even when both are
            // on the same sky target. Same {target}/{filter}/{session} shape.
            "AUX"       => Path.Combine("aux",
                            SanitizeFolder(string.IsNullOrEmpty(m.Target.Name) ? "Unknown" : m.Target.Name),
                            filter,
                            sessionDate.ToString("yyyy-MM-dd",
                                System.Globalization.CultureInfo.InvariantCulture)),
            _           => Path.Combine("lights",
                            SanitizeFolder(string.IsNullOrEmpty(m.Target.Name) ? "Unknown" : m.Target.Name),
                            filter,
                            sessionDate.ToString("yyyy-MM-dd",
                                System.Globalization.CultureInfo.InvariantCulture))
        };
        return Path.Combine(rig, subPath);
    }

    /// <summary>Subdirectory for a user-requested stacked master:
    /// {rig}/stacked/{target}/{filter}/{session}. Kept separate from the
    /// lights/calibration trees so the integrated result is easy to find and
    /// doesn't get mixed in with the raw subs.</summary>
    public static string BuildStackedSubDir(IImageData img, string rigName, DateTime sessionDate) {
        var m = img.MetaData;
        var rig    = SanitizeFolder(string.IsNullOrEmpty(rigName) ? "Default" : rigName);
        var target = SanitizeFolder(string.IsNullOrEmpty(m.Target.Name) ? "Unknown" : m.Target.Name);
        var filter = SanitizeFolder(string.IsNullOrEmpty(m.Exposure.Filter) ? "L" : m.Exposure.Filter);
        return Path.Combine(rig, "stacked", target, filter,
            sessionDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Map a local timestamp to its astronomical session date, the date
    /// the *evening* started. A capture at 02:30 local time still belongs
    /// to the previous evening's session, so the rollover is local noon.
    /// This matches how observers describe sessions ("the night of May
    /// 21st" runs from May 21 sunset through May 22 sunrise).
    /// </summary>
    public static DateTime SessionDateForLocal(DateTime local) =>
        (local.Hour < 12 ? local.AddDays(-1) : local).Date;

    private static string SanitizeFolder(string s) {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        // Also normalise spaces to underscore so paths stay shell-safe.
        return s.Replace(' ', '_');
    }

    private static string SubstitutePattern(string pattern, IImageData img, int seq) {
        var m = img.MetaData;
        var now = m.CreationTime.ToLocalTime();
        string Token(string key) => key switch {
            "target"    => string.IsNullOrEmpty(m.Target.Name) ? "Unknown" : m.Target.Name,
            "filter"    => string.IsNullOrEmpty(m.Exposure.Filter) ? "L" : m.Exposure.Filter,
            "exposure"  => m.Exposure.ExposureTime.ToString("0.##", CultureInfo.InvariantCulture),
            "gain"      => m.Camera.Gain.ToString(CultureInfo.InvariantCulture),
            "binning"   => $"{m.Camera.BinX}x{m.Camera.BinY}",
            "bitdepth"  => img.Properties.BitDepth.ToString(CultureInfo.InvariantCulture),
            "date"      => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "time"      => now.ToString("HH-mm-ss", CultureInfo.InvariantCulture),
            "datetime"  => now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture),
            "framenr"   => seq.ToString("0000"),
            "seq"       => seq.ToString("0000"),
            "camera"    => string.IsNullOrEmpty(m.Camera.Name) ? "cam" : m.Camera.Name,
            "temp"      => m.Camera.Temperature.ToString("0", CultureInfo.InvariantCulture),
            "imagetype" => m.Exposure.ImageType ?? "LIGHT",
            _           => "{" + key + "}"
        };

        return System.Text.RegularExpressions.Regex.Replace(pattern, @"\{(\w+)\}", match => Token(match.Groups[1].Value));
    }
}