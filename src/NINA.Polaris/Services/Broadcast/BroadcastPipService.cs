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

using NINA.Polaris.Services.Studio;
using SkiaSharp;

namespace NINA.Polaris.Services.Broadcast;

/// <summary>Where the second picture comes from.</summary>
public static class PipSources {
    public const string Off = "off";
    /// <summary>The guide camera's last frame, taken by the guide loop.</summary>
    public const string Guide = "guide";
    /// <summary>The auxiliary camera's last frame, taken by its capture loop.</summary>
    public const string Aux = "aux";
    /// <summary>A snapshot URL: an all sky camera, an IP camera, anything that
    /// answers a GET with a JPEG or a PNG.</summary>
    public const string Url = "url";

    public static readonly string[] All = { Off, Guide, Aux, Url };

    public static bool IsValid(string? id) {
        foreach (var s in All) if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string Parse(string? id) {
        foreach (var s in All) if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return s;
        return Off;
    }
}

/// <summary>
/// The second picture on a broadcast: an all sky camera, a camera pointed at
/// the rig, or the guide frame.
///
/// <para>Three sources, one rule: <see cref="Current"/> returns whatever is
/// already in hand and never waits for anything. A broadcast draws a frame
/// twice a second and cannot stop to expose a camera or to finish an HTTP
/// request, and none of these pictures is worth a dropped frame.</para>
///
/// <para>So nothing here commands a camera. The guide and aux frames are the
/// ones those loops already took, read out of memory. Asking the aux camera
/// for an exposure of our own would fight its own archiving loop and the
/// operator's focus snaps over the same capture gate, and asking the guide
/// camera would fight the guiding itself.</para>
///
/// <para>The URL source is the one that answers the case people actually have,
/// because an all sky camera is usually an IP camera rather than an astronomy
/// one. It is polled on its own timer, well below the frame rate, and the last
/// picture that arrived is what gets drawn.</para>
/// </summary>
public sealed class BroadcastPipService : IDisposable {

    /// <summary>How often a snapshot URL is fetched. An all sky exposure runs
    /// tens of seconds and the sky does not move fast; polling harder would
    /// just be rude to a small camera's web server.</summary>
    public static readonly TimeSpan UrlPollInterval = TimeSpan.FromSeconds(5);

    private readonly ActiveGuiderProvider _guiders;
    private readonly AuxCaptureService _aux;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<BroadcastPipService> _logger;

    private readonly object _lock = new();
    private SKBitmap? _frame;
    private string? _frameSource;
    private long _frameStamp = -1;
    private DateTime _lastUrlAttemptUtc = DateTime.MinValue;
    private volatile bool _urlFetchInFlight;
    private volatile string? _lastError;

    public BroadcastPipService(ActiveGuiderProvider guiders, AuxCaptureService aux,
                               IHttpClientFactory http, ILogger<BroadcastPipService> logger) {
        _guiders = guiders;
        _aux = aux;
        _http = http;
        _logger = logger;
    }

    /// <summary>Why the second picture is missing, in the operator's words, or
    /// null when there is nothing to report.</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// The picture to draw right now, or null when there is none. Never waits.
    /// The returned bitmap belongs to this service and is replaced in place, so
    /// draw it and forget it.
    /// </summary>
    public SKBitmap? Current(string source, string? url) {
        switch (PipSources.Parse(source)) {
            case PipSources.Guide: return FromGuider();
            case PipSources.Aux: return FromAux();
            case PipSources.Url: return FromUrl(url);
            default:
                Clear();
                return null;
        }
    }

    /// <summary>The caption drawn over the corner of the picture.</summary>
    public static string LabelFor(string source, string? language) =>
        PipSources.Parse(source) switch {
            PipSources.Guide => BroadcastStrings.PipGuideLabel(language),
            PipSources.Aux => BroadcastStrings.PipAuxLabel(language),
            PipSources.Url => BroadcastStrings.PipCameraLabel(language),
            _ => ""
        };

    // --- Guide camera -----------------------------------------------

    private SKBitmap? FromGuider() {
        if (_guiders.Active is not NativeGuider ng) {
            // PHD2 draws its own window and hands us nothing. Saying so beats
            // an empty corner the operator cannot explain.
            _lastError = "The guide frame is only available with the built in guider, not with PHD2.";
            return Reuse(PipSources.Guide);
        }
        var stamp = ng.ViewFrameId;
        if (stamp <= 0) {
            _lastError = "No guide frame yet. Start the guide loop.";
            return Reuse(PipSources.Guide);
        }
        lock (_lock) {
            if (_frameSource == PipSources.Guide && _frameStamp == stamp && _frame != null) return _frame;
        }
        // The guide view is already stretched for display, which is what the
        // guider's own camera panel shows, so the corner matches what the
        // operator sees in the interface.
        var jpeg = ng.EncodeViewJpeg(maxDim: 480, quality: 78);
        return Store(PipSources.Guide, stamp, jpeg);
    }

    // --- Aux camera -------------------------------------------------

    private SKBitmap? FromAux() {
        var (image, stamp) = _aux.LastFrame();
        if (image == null) {
            _lastError = _aux.IsRunning
                ? "Waiting for the first frame from the auxiliary camera."
                : "The auxiliary camera is not capturing.";
            return Reuse(PipSources.Aux);
        }
        lock (_lock) {
            if (_frameSource == PipSources.Aux && _frameStamp == stamp && _frame != null) return _frame;
        }
        try {
            var jpeg = FitsThumbnailer.RenderJpegFromImageData(image, maxDim: 480, quality: 78);
            return Store(PipSources.Aux, stamp, jpeg);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not render the auxiliary frame for the broadcast");
            _lastError = "The auxiliary frame could not be rendered.";
            return Reuse(PipSources.Aux);
        }
    }

    // --- Snapshot URL -----------------------------------------------

    private SKBitmap? FromUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url)) {
            _lastError = "No snapshot URL for the second picture.";
            return Reuse(PipSources.Url);
        }
        // Detached, and only one in flight. A camera that has stopped
        // answering must slow the polling down, never the broadcast.
        if (!_urlFetchInFlight && DateTime.UtcNow - _lastUrlAttemptUtc >= UrlPollInterval) {
            _lastUrlAttemptUtc = DateTime.UtcNow;
            _urlFetchInFlight = true;
            _ = Task.Run(async () => {
                try {
                    var bytes = await FetchAsync(url!).ConfigureAwait(false);
                    if (bytes != null) {
                        Store(PipSources.Url, DateTime.UtcNow.Ticks, bytes);
                        _lastError = null;
                    }
                } catch (Exception ex) {
                    _lastError = "The snapshot URL did not answer: " + ex.Message;
                    _logger.LogDebug(ex, "Snapshot fetch failed for {Url}", url);
                } finally {
                    _urlFetchInFlight = false;
                }
            });
        }
        return Reuse(PipSources.Url);
    }

    private async Task<byte[]?> FetchAsync(string url) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var resp = await _http.CreateClient().GetAsync(url, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) {
            _lastError = $"The snapshot URL answered {(int)resp.StatusCode}.";
            return null;
        }
        var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        // A camera that answers with an HTML login page instead of a picture
        // is the commonest way this goes wrong.
        if (bytes.Length < 64) { _lastError = "The snapshot URL returned nothing usable."; return null; }
        return bytes;
    }

    // --- Shared -----------------------------------------------------

    private SKBitmap? Store(string source, long stamp, byte[]? encoded) {
        if (encoded == null) return Reuse(source);
        SKBitmap? decoded;
        try { decoded = SKBitmap.Decode(encoded); } catch { decoded = null; }
        if (decoded == null) {
            _lastError = "The second picture could not be decoded.";
            return Reuse(source);
        }
        lock (_lock) {
            _frame?.Dispose();
            _frame = decoded;
            _frameSource = source;
            _frameStamp = stamp;
            return _frame;
        }
    }

    /// <summary>The last picture from this source, if it is still the source.
    /// Holding the previous frame is deliberate: a guide loop that pauses or a
    /// camera that misses one poll should not blank the corner.</summary>
    private SKBitmap? Reuse(string source) {
        lock (_lock) return _frameSource == source ? _frame : null;
    }

    private void Clear() {
        lock (_lock) {
            _frame?.Dispose();
            _frame = null;
            _frameSource = null;
            _frameStamp = -1;
        }
        _lastError = null;
    }

    public void Dispose() => Clear();
}
