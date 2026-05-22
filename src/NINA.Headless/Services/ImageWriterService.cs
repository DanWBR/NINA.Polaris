using System.Globalization;
using NINA.Image.FileFormat.FITS;
using NINA.Image.FileFormat.XISF;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Headless.Services;

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
///     lights/{target}/{filter}/{yyyy-MM-dd}/light_*.fits
///     calibration/
///       dark/{exposure}s_g{gain}/dark_*.fits
///       bias/g{gain}/bias_*.fits
///       flat/{filter}_g{gain}/flat_*.fits
///       masters/master_*.fits            (written by STUDIO ST-3)
///     calibrated/{target}/{filter}/...   (written by STUDIO ST-4)
///     integrated/{target}/{filter}/...   (written by STUDIO ST-5)
///     processed/{target}/...             (written by STUDIO ST-7 — TIFF/PNG/JPEG)
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

    public ImageWriterService(EquipmentManager equip, ProfileService profile, ILogger<ImageWriterService> logger) {
        _equip = equip;
        _profile = profile;
        _logger = logger;
    }

    public void ResetSessionCounter() => _sessionFrameNumber = 0;

    /// <summary>Save the image to disk and return the resulting path, or null
    /// if disabled / output dir missing.</summary>
    public string? SaveImage(IImageData imageData,
        string? targetName = null,
        string imageType = "LIGHT",
        int gain = 0) {

        var profile = _profile.Active;
        var dir = profile.ImageOutputDir;
        if (string.IsNullOrWhiteSpace(dir)) {
            _logger.LogDebug("ImageWriter: no output dir configured, skipping disk save");
            return null;
        }

        try {
            Directory.CreateDirectory(dir);

            EnrichMetadata(imageData, profile, targetName, imageType, gain);
            _sessionFrameNumber++;

            var format = (profile.ImageFormat ?? "fits").Trim().ToLowerInvariant();
            var extension = format switch {
                "xisf" => ".xisf",
                _      => ".fits"
            };

            var pattern = string.IsNullOrWhiteSpace(profile.ImageNamePattern)
                ? "{target}_{filter}_{exposure}s_{date}_{seq}"
                : profile.ImageNamePattern;
            var fileName = SubstitutePattern(pattern, imageData, _sessionFrameNumber) + extension;
            // Sanitise illegal filename characters
            foreach (var c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');

            // Standard subdirectory layout — keeps lights / calibration /
            // STUDIO outputs separated so the post-processing pipeline can
            // find matching darks by exposure+gain (and flats by filter+gain)
            // without scanning every header.
            var subDir = BuildSubDir(imageType, imageData, profile);
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

            if (format == "xisf") {
                XISFWriter.Write(imageData, fullPath, rotator: rotMeta);
                _logger.LogInformation("Saved XISF: {Path}", fullPath);
            } else {
                FITSWriter.Write(imageData, fullPath, rotator: rotMeta);
                _logger.LogInformation("Saved FITS: {Path}", fullPath);
            }
            _lastWrittenPath = fullPath;
            return fullPath;
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to save FITS to {Dir}", dir);
            return null;
        }
    }

    private void EnrichMetadata(IImageData imageData, UserProfile profile,
        string? targetName, string imageType, int gain) {
        var m = imageData.MetaData;

        // Exposure
        if (string.IsNullOrEmpty(m.Exposure.ImageType)) m.Exposure.ImageType = imageType;
        m.Exposure.ExposureNumber = _sessionFrameNumber + 1;

        // Camera (some fields are populated by IndiCamera, fill gaps)
        if (m.Camera.Gain == 0 && gain > 0) m.Camera.Gain = gain;

        // Telescope — focal length comes from the *active rig* (a per-rig
        // optic property), falling back to the legacy profile value only if
        // no rigs have been set up.
        if (_equip.Telescope != null && _equip.Telescope.IsConnected) {
            var rigFocalLen = _profile.ActiveEquipmentProfile.FocalLengthMm;
            var focalLength = rigFocalLen > 0 ? rigFocalLen : profile.FocalLengthMm;
            m.Telescope.Name = _equip.Telescope.DeviceName;
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

    /// <summary>
    /// Pick the structured subdirectory under ImageOutputDir for a frame
    /// based on its IMAGETYP. Lights live under
    /// lights/{target}/{filter}/{date}; calibration frames are grouped by
    /// the keys that matter for matching them to lights later (exposure +
    /// gain for darks, gain for bias, filter + gain for flats).
    /// </summary>
    public static string BuildSubDir(string imageType, IImageData img, UserProfile profile) {
        var m = img.MetaData;
        var typeUpper = (imageType ?? "LIGHT").Trim().ToUpperInvariant();
        var filter   = SanitizeFolder(string.IsNullOrEmpty(m.Exposure.Filter) ? "L" : m.Exposure.Filter);
        var gain     = m.Camera.Gain;
        var exposure = m.Exposure.ExposureTime;

        return typeUpper switch {
            "DARK"      => Path.Combine("calibration", "dark",
                            FormattableString.Invariant($"{exposure:0.##}s_g{gain}")),
            "BIAS"      => Path.Combine("calibration", "bias",
                            FormattableString.Invariant($"g{gain}")),
            "DARKFLAT"  => Path.Combine("calibration", "darkflat",
                            FormattableString.Invariant($"{exposure:0.##}s_g{gain}")),
            "FLAT"      => Path.Combine("calibration", "flat",
                            FormattableString.Invariant($"{filter}_g{gain}")),
            _           => Path.Combine("lights",
                            SanitizeFolder(string.IsNullOrEmpty(m.Target.Name) ? "Unknown" : m.Target.Name),
                            filter,
                            m.CreationTime.ToLocalTime().ToString("yyyy-MM-dd",
                                System.Globalization.CultureInfo.InvariantCulture))
        };
    }

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
