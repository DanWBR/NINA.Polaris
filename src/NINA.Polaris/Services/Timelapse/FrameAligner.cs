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
    private PhaseCorrelationAligner? _pc;   // built lazily on frame 0 for stabilize

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
            if (_pc == null) {
                // Reference = the first frame; it defines "in place".
                _pc = new PhaseCorrelationAligner(lum, width, height);
                return (0, 0);
            }
            // Align returns the shift (dst = src + (dx,dy)) that lands this frame
            // back on the reference — same convention CenterOffset and the
            // planetary stacker use, so apply it directly.
            var (dx, dy) = _pc.Align(lum);
            dx = Math.Clamp(dx, -width, width);
            dy = Math.Clamp(dy, -height, height);
            return (dx, dy);
        }

        return (0, 0);
    }
}
