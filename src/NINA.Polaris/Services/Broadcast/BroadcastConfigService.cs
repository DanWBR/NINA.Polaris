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

using System.Text.Json;
using System.Text.Json.Serialization;
using NINA.Polaris.Services;

namespace NINA.Polaris.Services.Broadcast;

/// <summary>A streaming platform and the RTMP endpoint it publishes to. The
/// selector only prefills the URL field; what is stored on the configuration is
/// what gets used, because Twitch has regional ingest servers and Instagram
/// hands out a fresh URL for every session.</summary>
public sealed record BroadcastDestination(string Id, string Label, string RtmpUrl);

public static class BroadcastDestinations {
    public static readonly BroadcastDestination[] All = {
        new("youtube", "YouTube Live", "rtmp://a.rtmp.youtube.com/live2"),
        new("twitch", "Twitch", "rtmp://live.twitch.tv/app"),
        // Facebook publishes over RTMPS on 443 and refuses plain RTMP.
        new("facebook", "Facebook Live", "rtmps://live-api-s.facebook.com:443/rtmp"),
        // Instagram goes through Live Producer, which issues the URL and the
        // key per session, so there is nothing useful to prefill.
        new("instagram", "Instagram Live Producer", ""),
        new("custom", "Custom RTMP server", "")
    };

    public static BroadcastDestination? Find(string? id) {
        foreach (var d in All) if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }
}

/// <summary>What is stored. The key is in here and must not leave the host.</summary>
public sealed record BroadcastConfig {
    public string Destination { get; init; } = "youtube";
    public string RtmpUrl { get; init; } = "";
    public string StreamKey { get; init; } = "";
    public string Quality { get; init; } = "medium";
    /// <summary>The first line of the header strip. Empty means the default,
    /// which the renderer supplies: the operator should not have to type
    /// anything to get a broadcast with a name on it.</summary>
    public string Title { get; init; } = "";
    /// <summary>The header strip: the title, and the equipment under it.</summary>
    public bool ShowHeader { get; init; } = true;
    public bool ShowObjectCard { get; init; } = true;
    /// <summary>Where the second picture in the bottom left corner comes from:
    /// off, guide, aux or url.</summary>
    public string PipSource { get; init; } = PipSources.Off;
    /// <summary>Snapshot URL for the url source: an all sky camera, an IP
    /// camera, anything that answers a GET with a JPEG.</summary>
    public string PipUrl { get; init; } = "";
    /// <summary>What to call the second picture on screen. Empty uses a
    /// default for the source, which for a snapshot URL can only be generic:
    /// Polaris has no way to know whether the camera is pointed at the sky,
    /// at the rig or at the dog.</summary>
    public string PipLabel { get; init; } = "";
    public bool ShowBanner { get; init; } = true;
    /// <summary>Look descriptions up online for objects Polaris ships no text
    /// for. On by default: it never blocks a frame, and with no network the
    /// card falls back without anyone noticing.</summary>
    public bool FetchDescriptions { get; init; } = true;
    public bool RecordToDisk { get; init; }

    /// <summary>A file or a folder on the host to play under the broadcast.
    /// Empty means the silent track.
    ///
    /// <para>The operator's own music, never ours. Polaris ships none: the
    /// usual free libraries license a track into your video and forbid
    /// redistributing the file, which is what putting one in the package would
    /// be. It also puts the licence where the account is, since the platform
    /// sends its claim to whoever is broadcasting.</para></summary>
    public string MusicPath { get; init; } = "";
    /// <summary>0 to 100.</summary>
    public int MusicVolume { get; init; } = 50;
    /// <summary>Only means anything for a folder.</summary>
    public bool MusicShuffle { get; init; } = true;

    public static readonly BroadcastConfig Default = new();

    public bool HasStreamKey => !string.IsNullOrEmpty(StreamKey);
    public bool CanPublish => !string.IsNullOrWhiteSpace(RtmpUrl) && HasStreamKey;
}

/// <summary>Patch semantics: null keeps the stored value, a value replaces it.
/// For the key, "" clears it, matching the relay token and the Canopus API key,
/// because the field is write-only in the interface and the browser has nothing
/// to send back when it was not touched.</summary>
public sealed record BroadcastConfigUpdate(
    string? Destination = null, string? RtmpUrl = null, string? StreamKey = null,
    string? Quality = null, bool? ShowObjectCard = null, bool? ShowBanner = null,
    bool? FetchDescriptions = null, bool? RecordToDisk = null,
    string? Title = null, bool? ShowHeader = null,
    string? PipSource = null, string? PipUrl = null, string? PipLabel = null,
    string? MusicPath = null, int? MusicVolume = null, bool? MusicShuffle = null);

/// <summary>
/// The broadcast configuration, in its own file under the data dir rather than
/// on the profile.
///
/// <para>The reason is <c>GET /api/system/profile</c>, which returns the active
/// profile verbatim to every client that asks. A stream key on the profile
/// would be handed to any browser on the network, and a stream key is enough to
/// broadcast to someone's channel as them. So it lives in
/// <c>broadcast/config.json</c>, 0600 in a 0700 directory, and
/// <see cref="GetPublic"/> reports only whether one is set.</para>
/// </summary>
public sealed class BroadcastConfigService {

    private static readonly JsonSerializerOptions _json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly ILogger<BroadcastConfigService> _logger;
    private readonly object _lock = new();
    private BroadcastConfig? _cached;

    public BroadcastConfigService(ProfileService profiles, ILogger<BroadcastConfigService> logger)
        : this(Path.Combine(profiles.DataDir, "broadcast", "config.json"), logger) { }

    /// <summary>Explicit path, for tests.</summary>
    public BroadcastConfigService(string path, ILogger<BroadcastConfigService> logger) {
        _path = path;
        _logger = logger;
    }

    public string FilePath => _path;

    public BroadcastConfig Get() {
        lock (_lock) {
            if (_cached != null) return _cached;
            try {
                if (File.Exists(_path)) {
                    var stored = JsonSerializer.Deserialize<StoredConfig>(File.ReadAllText(_path), _json);
                    if (stored != null) _cached = FromStored(stored);
                }
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Broadcast config unreadable at {Path}; using defaults", _path);
            }
            return _cached ??= BroadcastConfig.Default;
        }
    }

    public BroadcastConfig Update(BroadcastConfigUpdate req) {
        lock (_lock) {
            var cur = Get();

            var destination = cur.Destination;
            if (req.Destination != null) {
                destination = BroadcastDestinations.Find(req.Destination)?.Id
                    ?? throw new ArgumentException($"Unknown destination '{req.Destination}'.");
            }

            var quality = cur.Quality;
            if (req.Quality != null) {
                // The quality id ends up as ffmpeg arguments, so it is matched
                // against the allowlist here rather than trusted from the wire.
                if (!BroadcastQuality.IsValid(req.Quality))
                    throw new ArgumentException($"Unknown quality '{req.Quality}'. Use low, medium or high.");
                quality = BroadcastQuality.Parse(req.Quality).Id;
            }

            var pipSource = cur.PipSource;
            if (req.PipSource != null) {
                if (!PipSources.IsValid(req.PipSource))
                    throw new ArgumentException($"Unknown second picture source '{req.PipSource}'.");
                pipSource = PipSources.Parse(req.PipSource);
            }
            var pipUrl = req.PipUrl != null ? req.PipUrl.Trim() : cur.PipUrl;
            if (pipUrl.Length > 0 && !IsHttpUrl(pipUrl))
                throw new ArgumentException("The snapshot address must be an http:// or https:// URL.");

            var url = req.RtmpUrl != null ? req.RtmpUrl.Trim() : cur.RtmpUrl;
            if (url.Length > 0 && !IsRtmpUrl(url))
                throw new ArgumentException("The destination must be an rtmp:// or rtmps:// URL. "
                    + "The address of the studio page in the browser is not it.");

            var next = new BroadcastConfig {
                Destination = destination,
                RtmpUrl = url,
                // Trimmed, because a key copied out of a web page brings a
                // trailing newline with it often enough to be worth handling.
                StreamKey = req.StreamKey == null ? cur.StreamKey : req.StreamKey.Trim(),
                Quality = quality,
                // Trimmed and capped: it is drawn into a video frame, and a
                // title longer than the strip is ellipsised anyway.
                Title = req.Title == null ? cur.Title : Cap(req.Title.Trim(), 80),
                ShowHeader = req.ShowHeader ?? cur.ShowHeader,
                ShowObjectCard = req.ShowObjectCard ?? cur.ShowObjectCard,
                PipSource = pipSource,
                PipUrl = pipUrl,
                PipLabel = req.PipLabel == null ? cur.PipLabel : Cap(req.PipLabel.Trim(), 30),
                ShowBanner = req.ShowBanner ?? cur.ShowBanner,
                FetchDescriptions = req.FetchDescriptions ?? cur.FetchDescriptions,
                RecordToDisk = req.RecordToDisk ?? cur.RecordToDisk,
                MusicPath = req.MusicPath == null ? cur.MusicPath : req.MusicPath.Trim(),
                // Clamped rather than rejected: a slider that refuses to save
                // teaches nothing, and the value is only a gain.
                MusicVolume = Math.Clamp(req.MusicVolume ?? cur.MusicVolume, 0, 100),
                MusicShuffle = req.MusicShuffle ?? cur.MusicShuffle
            };
            Save(next);
            _cached = next;
            return next;
        }
    }

    /// <summary>What a browser is allowed to see: everything except the key,
    /// plus the fact that there is one.</summary>
    public object GetPublic() {
        var c = Get();
        return new {
            destination = c.Destination,
            rtmpUrl = c.RtmpUrl,
            hasStreamKey = c.HasStreamKey,
            quality = c.Quality,
            title = c.Title,
            showHeader = c.ShowHeader,
            showObjectCard = c.ShowObjectCard,
            pipSource = c.PipSource,
            pipUrl = c.PipUrl,
            pipLabel = c.PipLabel,
            pipSources = PipSources.All,
            showBanner = c.ShowBanner,
            fetchDescriptions = c.FetchDescriptions,
            recordToDisk = c.RecordToDisk,
            musicPath = c.MusicPath,
            musicVolume = c.MusicVolume,
            musicShuffle = c.MusicShuffle,
            musicExtensions = BroadcastMusic.Extensions,
            destinations = BroadcastDestinations.All.Select(d => new { id = d.Id, label = d.Label, rtmpUrl = d.RtmpUrl }),
            qualities = BroadcastQuality.All.Select(q => new {
                id = q.Id, label = q.Label, width = q.Width, height = q.Height, bitrateKbps = q.BitrateKbps
            })
        };
    }

    /// <summary>
    /// Null when a broadcast can start; otherwise the reason, in the words the
    /// operator sees. <paramref name="ffmpegAvailable"/> is passed in rather
    /// than probed so this stays a decision about the configuration.
    /// </summary>
    public string? Validate(bool ffmpegAvailable) {
        var c = Get();
        if (!ffmpegAvailable)
            return "ffmpeg is not installed on this host, and the broadcast is encoded with it.";
        // Recording alone is a complete broadcast: compose the picture and keep
        // the file, which is what a bad uplink or no uplink leaves you.
        if (c.RecordToDisk && !c.CanPublish) return null;
        if (string.IsNullOrWhiteSpace(c.RtmpUrl))
            return "Choose a destination, or turn on recording to keep the video on the host.";
        if (!c.HasStreamKey) return "Enter the stream key from the platform.";
        return null;
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();

    private static bool IsHttpUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool IsRtmpUrl(string url) =>
        url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase);

    private static BroadcastConfig FromStored(StoredConfig s) => new() {
        Destination = BroadcastDestinations.Find(s.Destination)?.Id ?? "youtube",
        RtmpUrl = s.RtmpUrl?.Trim() ?? "",
        StreamKey = s.StreamKey ?? "",
        Quality = BroadcastQuality.Parse(s.Quality).Id,
        Title = s.Title?.Trim() ?? "",
        ShowHeader = s.ShowHeader ?? true,
        ShowObjectCard = s.ShowObjectCard ?? true,
        PipSource = PipSources.Parse(s.PipSource),
        PipUrl = s.PipUrl?.Trim() ?? "",
        PipLabel = s.PipLabel?.Trim() ?? "",
        ShowBanner = s.ShowBanner ?? true,
        FetchDescriptions = s.FetchDescriptions ?? true,
        RecordToDisk = s.RecordToDisk ?? false,
        MusicPath = s.MusicPath?.Trim() ?? "",
        MusicVolume = Math.Clamp(s.MusicVolume ?? 50, 0, 100),
        MusicShuffle = s.MusicShuffle ?? true
    };

    private void Save(BroadcastConfig c) {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) {
            // The file is 0600, but a world-readable directory around it still
            // tells anyone with an account on the board that there is a stream
            // key here and what it is called.
            try {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            } catch (Exception ex) { _logger.LogDebug(ex, "chmod 700 failed for {Path}", dir); }
        }
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new StoredConfig {
            Destination = c.Destination, RtmpUrl = c.RtmpUrl, StreamKey = c.StreamKey, Quality = c.Quality,
            Title = c.Title, ShowHeader = c.ShowHeader,
            ShowObjectCard = c.ShowObjectCard, ShowBanner = c.ShowBanner,
            PipSource = c.PipSource, PipUrl = c.PipUrl, PipLabel = c.PipLabel,
            MusicPath = c.MusicPath, MusicVolume = c.MusicVolume, MusicShuffle = c.MusicShuffle,
            FetchDescriptions = c.FetchDescriptions, RecordToDisk = c.RecordToDisk
        }, _json));
        if (!OperatingSystem.IsWindows()) {
            // Set on the temporary file, before it takes the real name: a
            // moment of 0644 is a moment in which the key can be read.
            try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception ex) { _logger.LogDebug(ex, "chmod 600 failed for {Path}", tmp); }
        }
        File.Move(tmp, _path, overwrite: true);
    }

    private sealed class StoredConfig {
        public string? Destination { get; set; }
        public string? RtmpUrl { get; set; }
        [JsonPropertyName("streamKey")] public string? StreamKey { get; set; }
        public string? Quality { get; set; }
        public string? Title { get; set; }
        public bool? ShowHeader { get; set; }
        public bool? ShowObjectCard { get; set; }
        public string? PipSource { get; set; }
        public string? PipUrl { get; set; }
        public string? PipLabel { get; set; }
        public bool? ShowBanner { get; set; }
        public string? MusicPath { get; set; }
        public int? MusicVolume { get; set; }
        public bool? MusicShuffle { get; set; }
        public bool? FetchDescriptions { get; set; }
        public bool? RecordToDisk { get; set; }
    }
}
