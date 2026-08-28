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

using NINA.Polaris.Services.Planetary;
using SkiaSharp;

namespace NINA.Polaris.Services.Timelapse;

/// <summary>
/// Per-job frame registration for the time-lapse builder. Stateful (the
/// stabilize mode pins a reference to frame 0), so the encoder builds ONE
/// aligner per job and calls <see cref="Process"/> for every rendered frame in
/// order. The Skia glue (decode/shift/re-encode) lives here; the alignment
/// decision is the pure planetary/timelapse helpers, so it stays testable.
///
/// Two strategies, mirroring the planetary stacker's split:
/// - <b>center</b>: move the bright bounded disc to the image centre
///   (<see cref="TimelapseAlign.CenterOffset"/>, limb fit -> centroid fallback).
///   Right for a whole Sun/Moon on sky, an eclipse crescent included.
/// - <b>stabilize</b>: register each frame back onto frame 0 by phase
///   correlation (<see cref="PhaseCorrelationAligner"/>). Right for a
///   frame-filling lunar/solar SURFACE close-up, where there is no clean limb.
/// - <b>auto</b>: resolve to stabilize when the first frame fills the frame
///   (<see cref="CentroidAligner.FillFraction"/> &gt;= 0.6), else center.
/// </summary>
public sealed class FrameAligner {
    public enum Mode { Off, Auto, Center, Stabilize }

    private Mode _mode;
    // Stabilize: sequential registration. Each frame is correlated against the
    // last CONFIDENT frame and the step is accumulated into a running offset back
    // to frame 0. Anchoring every frame directly to frame 0 breaks down over a
    // long session (the Moon/Sun drifts and its terminator/illumination change,
    // so late frames diverge too far from frame 0 and phase correlation locks the
    // wrong peak). When the correlation peak is weak — a low-contrast frame with
    // no detail to lock onto, e.g. a Moon near eclipse totality — the shift can't
    // be trusted, so we COAST: hold the last confident position and keep that
    // frame as the reference, re-locking when the subject brightens again.
    private ushort[]? _refLum;              // last confident reference frame
    private int _refDx, _refDy;             // its cumulative shift onto frame 0

    // Minimum peak-to-sidelobe ratio to trust a correlation. Below this the frame
    // is too diffuse to register (near-eclipse dimming); coast instead.
    private const double ConfidenceThreshold = 5.0;

    public FrameAligner(string? mode) => _mode = Parse(mode);

    public static Mode Parse(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch {
        "auto" => Mode.Auto,
        "center" or "centre" => Mode.Center,
        "stabilize" or "stabilise" => Mode.Stabilize,
        // Legacy: the old boolean "center the disc" option.
        "true" or "1" => Mode.Center,
        _ => Mode.Off,
    };

    public bool Enabled => _mode != Mode.Off;

    /// <summary>The resolved mode (after auto has been decided on frame 0).
    /// Exposed for tests/telemetry.</summary>
    public Mode Resolved => _mode;

    /// <summary>Register the rendered JPEG for frame <paramref name="index"/>.
    /// Returns a re-encoded JPEG shifted into place, or the input unchanged when
    /// there's nothing to move. Any failure returns the input untouched.</summary>
    public byte[] Process(byte[] jpeg, int index) {
        if (_mode == Mode.Off) return jpeg;
        try {
            using var bmp = SKBitmap.Decode(jpeg);
            if (bmp == null) return jpeg;
            int w = bmp.Width, h = bmp.Height;
            var px = bmp.Pixels;                 // SKColor[]
            var lum = new ushort[w * h];
            for (int i = 0; i < px.Length; i++) {
                // Rec.601 luminance, promoted to the 16-bit domain the aligners use.
                int y = (299 * px[i].Red + 587 * px[i].Green + 114 * px[i].Blue) / 1000;
                lum[i] = (ushort)(y << 8);
            }

            var (dx, dy) = Offset(lum, w, h, index);
            if (dx == 0 && dy == 0) return jpeg;

            using var shifted = new SKBitmap(w, h, bmp.ColorType, bmp.AlphaType);
            using (var canvas = new SKCanvas(shifted)) {
                canvas.Clear(SKColors.Black);
                canvas.DrawBitmap(bmp, dx, dy);
            }
            using var img = SKImage.FromBitmap(shifted);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
            return data.ToArray();
        } catch { return jpeg; }
    }

    /// <summary>The integer shift (dst = src + (dx, dy)) for this frame. Pure,
    /// so it can be unit-tested against synthetic luminance buffers.</summary>
    public (int dx, int dy) Offset(ushort[] lum, int width, int height, int index) {
        if (_mode == Mode.Auto) {
            // Resolve once, on the first frame we see, by how much it fills.
            _mode = CentroidAligner.FillFraction(lum, width, height) >= 0.6
                ? Mode.Stabilize : Mode.Center;
        }

        if (_mode == Mode.Center)
            return TimelapseAlign.CenterOffset(lum, width, height);

        if (_mode == Mode.Stabilize) {
            if (_refLum == null || _refLum.Length != lum.Length) {
                // First frame defines "in place"; nothing accumulated yet.
                _refLum = lum;
                _refDx = _refDy = 0;
                return (0, 0);
            }
            // Correlate against the last confident frame. Align returns the shift
            // (dst = src + (dx,dy)) landing THIS frame on the reference, plus a
            // PSR confidence; the reference's own offset composes it back to frame 0.
            var pc = new PhaseCorrelationAligner(_refLum, width, height);
            var (sdx, sdy, conf) = pc.AlignWithConfidence(lum);
            int maxStep = Math.Max(4, Math.Min(width, height) / 4);
            bool trustworthy = conf >= ConfidenceThreshold
                && Math.Abs(sdx) <= maxStep && Math.Abs(sdy) <= maxStep;
            if (!trustworthy)
                // Too diffuse (or an implausible jump) to measure: hold the last
                // confident position and keep its reference for the next frame.
                return (_refDx, _refDy);

            int dx = Math.Clamp(_refDx + sdx, -width, width);
            int dy = Math.Clamp(_refDy + sdy, -height, height);
            // Promote this frame to the reference for the next one.
            _refLum = lum; _refDx = dx; _refDy = dy;
            return (dx, dy);
        }

        return (0, 0);
    }
}
