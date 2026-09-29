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

using SkiaSharp;

namespace NINA.Polaris.Services.Broadcast;

/// <summary>
/// Draws one broadcast frame: the picture, the object card, the banner.
///
/// <para>The host composes the whole frame and hands ffmpeg raw pixels, rather
/// than sending the bare picture and having ffmpeg's <c>overlay</c> and
/// <c>drawtext</c> put the furniture on. Three reasons, in order of how much
/// they cost when ignored. Wrapping: the description has to fit a panel of
/// known width in a proportional font, and drawtext cannot wrap, so the text
/// would have to be broken by counting characters and a wide string would
/// overflow the card. Changing: an image input is read once, so a new target
/// would mean restarting ffmpeg and dropping the stream. And filter_complex is
/// a language with two levels of escaping that a target name with a colon in
/// it can break.</para>
///
/// <para>What ffmpeg gets instead is a plain rawvideo stream and no filters at
/// all, which also means the overlay no longer depends on drawtext and
/// overlay being compiled into the host's build.</para>
/// </summary>
public sealed class FrameComposer : IDisposable {

    private static readonly SKColor Ink = new(0xFF, 0xFF, 0xFF);
    private static readonly SKColor Dim = new(0xB4, 0xBE, 0xCD);
    private static readonly SKColor Accent = new(0x6E, 0xA8, 0xFF);
    private static readonly SKColor PanelFill = new(0x0B, 0x0F, 0x18, 0xC8);
    private static readonly SKColor PanelEdge = new(0xFF, 0xFF, 0xFF, 0x22);

    private readonly BroadcastLayout _l;
    private readonly BroadcastFonts _fonts;
    private readonly SKFont _title, _subtitle, _chip, _body, _banner, _credit;
    private readonly SKFont _headerTitle, _headerRig;
    private readonly SKBitmap _canvasBitmap;
    private readonly byte[] _rgb;

    public FrameComposer(BroadcastLayout layout, BroadcastFonts fonts) {
        _l = layout;
        _fonts = fonts;
        _title = new SKFont(fonts.Bold, layout.TitleSize);
        _subtitle = new SKFont(fonts.Regular, layout.SubtitleSize);
        _chip = new SKFont(fonts.Regular, layout.ChipSize);
        _body = new SKFont(fonts.Regular, layout.BodySize);
        _banner = new SKFont(fonts.Bold, layout.BannerSize);
        _credit = new SKFont(fonts.Regular, layout.CreditSize);
        _headerTitle = new SKFont(fonts.Bold, layout.HeaderTitleSize);
        _headerRig = new SKFont(fonts.Regular, layout.HeaderRigSize);
        foreach (var f in new[] { _title, _subtitle, _chip, _body, _banner, _credit,
                                  _headerTitle, _headerRig }) {
            f.Edging = SKFontEdging.SubpixelAntialias;
            f.Subpixel = true;
        }
        _canvasBitmap = new SKBitmap(new SKImageInfo(layout.Width, layout.Height,
                                                     SKColorType.Rgba8888, SKAlphaType.Premul));
        _rgb = new byte[layout.Width * layout.Height * 3];
    }

    /// <summary>
    /// Compose a frame and return it as packed RGB24, the format ffmpeg is
    /// reading on stdin. The buffer is reused between frames: copy it if you
    /// intend to keep it.
    /// </summary>
    /// <param name="headerTitle">What this broadcast is, on the top strip.</param>
    /// <param name="headerRig">The equipment, under the title. Static for the
    /// whole session, which is why it sits at the top and not in the banner
    /// with the numbers that change.</param>
    /// <param name="waitingText">Shown in the middle when there is no picture
    /// yet. Null leaves the frame bare.</param>
    public byte[] Compose(SKBitmap? picture, ObjectCard? card, string? bannerText,
                          string? headerTitle = null, string? headerRig = null,
                          string? waitingText = null) {
        using var surface = new SKCanvas(_canvasBitmap);
        surface.Clear(new SKColor(0x05, 0x07, 0x0C));

        if (picture != null) DrawPicture(surface, picture);
        else if (!string.IsNullOrWhiteSpace(waitingText)) DrawWaiting(surface, waitingText!);
        if (!_l.Header.IsEmpty) DrawHeader(surface, headerTitle, headerRig);
        if (card != null) DrawCard(surface, card);
        if (!string.IsNullOrWhiteSpace(bannerText)) DrawBanner(surface, bannerText);
        surface.Flush();

        PackRgb24();
        return _rgb;
    }

    private void DrawPicture(SKCanvas c, SKBitmap picture) {
        var r = BroadcastLayout.Letterbox(picture.Width, picture.Height, _l.Picture);
        using var paint = new SKPaint { IsAntialias = true };
        using var image = SKImage.FromBitmap(picture);
        c.DrawImage(image, new SKRect(r.X, r.Y, r.Right, r.Bottom),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
    }

    // --- The card ---------------------------------------------------

    private void DrawCard(SKCanvas c, ObjectCard card) {
        var copy = card.Copy;
        var inset = _l.CardInset;
        var textW = _l.CardTextWidth;
        var x = _l.Card.X + inset;

        // Lay the text out first: the panel is only as tall as its contents,
        // and a fixed-height panel with a short description is a big empty box
        // sitting over the picture.
        var titleLines = Wrap(copy.Title, _title, textW, 2);
        var subtitleLines = string.IsNullOrWhiteSpace(copy.Subtitle)
            ? Array.Empty<string>() : Wrap(copy.Subtitle, _subtitle, textW, 1);
        var chipLines = copy.Facts.Count == 0
            ? Array.Empty<string>() : Wrap(string.Join("  ·  ", copy.Facts), _chip, textW, 2);
        var bodyLines = string.IsNullOrWhiteSpace(copy.Description)
            ? Array.Empty<string>() : Wrap(copy.Description, _body, textW, 5);
        var creditLines = string.IsNullOrWhiteSpace(copy.Credit)
            ? Array.Empty<string>() : Wrap(copy.Credit!, _credit, textW, 1);

        var thumb = LoadThumb(card.ThumbnailPath);
        var gap = Math.Max(3, inset / 2);

        float height = inset;
        if (thumb != null) height += _l.CardThumb.Height + inset;
        height += Height(titleLines, _title);
        if (subtitleLines.Length > 0) height += gap / 2 + Height(subtitleLines, _subtitle);
        if (chipLines.Length > 0) height += gap + Height(chipLines, _chip);
        if (bodyLines.Length > 0) height += gap + Height(bodyLines, _body);
        if (creditLines.Length > 0) height += gap / 2 + Height(creditLines, _credit);
        height += inset;
        height = Math.Min(height, (float)_l.Card.Height);

        var panel = new SKRect(_l.Card.X, _l.Card.Y, _l.Card.Right, _l.Card.Y + height);
        var radius = Math.Max(4, inset * 0.8f);
        using (var fill = new SKPaint { Color = PanelFill, IsAntialias = true })
            c.DrawRoundRect(panel, radius, radius, fill);
        using (var edge = new SKPaint {
            Color = PanelEdge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1
        }) c.DrawRoundRect(panel, radius, radius, edge);

        var y = (float)(_l.Card.Y + inset);
        if (thumb != null) {
            var dst = new SKRect(x, y, x + _l.CardThumb.Width, y + _l.CardThumb.Height);
            using (var clip = new SKPaint { IsAntialias = true }) {
                c.Save();
                c.ClipRoundRect(new SKRoundRect(dst, radius / 2), antialias: true);
                // Filled, not fitted: a letterbox here would put bars inside
                // the panel. The cutouts are centred on the object, so what
                // the crop loses is empty sky.
                var fitted = BroadcastLayout.Cover(thumb.Width, thumb.Height,
                    new BroadcastRect((int)dst.Left, (int)dst.Top, (int)dst.Width, (int)dst.Height));
                using var thumbImage = SKImage.FromBitmap(thumb);
                c.DrawImage(thumbImage, new SKRect(fitted.X, fitted.Y, fitted.Right, fitted.Bottom),
                            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), clip);
                c.Restore();
            }
            thumb.Dispose();
            y += _l.CardThumb.Height + inset;
        }

        y = DrawLines(c, titleLines, _title, x, y, Ink);
        if (subtitleLines.Length > 0) { y += gap / 2f; y = DrawLines(c, subtitleLines, _subtitle, x, y, Dim); }
        if (chipLines.Length > 0) { y += gap; y = DrawLines(c, chipLines, _chip, x, y, Accent); }
        if (bodyLines.Length > 0) { y += gap; y = DrawLines(c, bodyLines, _body, x, y, Ink); }
        if (creditLines.Length > 0) { y += gap / 2f; DrawLines(c, creditLines, _credit, x, y, Dim); }
    }

    private SKBitmap? LoadThumb(string? path) {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try {
            using var fs = File.OpenRead(path);
            return SKBitmap.Decode(fs);
        } catch { return null; }
    }

    /// <summary>The line shown while there is nothing to show. Centred on the
    /// picture area, not the frame, so the header and banner do not push it
    /// off centre.</summary>
    private void DrawWaiting(SKCanvas c, string text) {
        var line = Fit(text, _subtitle, _l.Width * 0.6f);
        var width = _subtitle.MeasureText(line);
        var m = _subtitle.Metrics;
        using var paint = new SKPaint { Color = Dim, IsAntialias = true };
        c.DrawText(line, (_l.Width - width) / 2f, _l.Height / 2f - m.Ascent / 2, _subtitle, paint);
    }

    // --- The header -------------------------------------------------

    /// <summary>
    /// The top strip: the name of the broadcast, and the equipment under it.
    ///
    /// <para>The equipment belongs here and not in the bottom banner because
    /// it does not change. The banner is where the numbers that move live, and
    /// mixing a fixed string of gear names into it makes the part that is
    /// actually updating harder to find. Someone arriving mid stream reads the
    /// top once and the bottom continuously.</para>
    /// </summary>
    private void DrawHeader(SKCanvas c, string? title, string? rig) {
        var r = _l.Header;
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(rig)) return;
        Panel(c, r, Math.Max(3, r.Height * 0.16f));

        var padX = r.Height * 0.30f;
        var padY = r.Height * 0.20f;
        var y = r.Y + padY;
        var maxW = r.Width - padX * 2;

        if (!string.IsNullOrWhiteSpace(title)) {
            var m = _headerTitle.Metrics;
            using var paint = new SKPaint { Color = Ink, IsAntialias = true };
            c.DrawText(Fit(title!, _headerTitle, maxW), r.X + padX, y - m.Ascent, _headerTitle, paint);
            y += m.Descent - m.Ascent + m.Leading;
        }
        if (!string.IsNullOrWhiteSpace(rig)) {
            var m = _headerRig.Metrics;
            using var paint = new SKPaint { Color = Dim, IsAntialias = true };
            c.DrawText(Fit(rig!, _headerRig, maxW), r.X + padX, y - m.Ascent, _headerRig, paint);
        }
    }

    private static void Panel(SKCanvas c, BroadcastRect r, float radius) {
        var rect = new SKRect(r.X, r.Y, r.Right, r.Bottom);
        using (var fill = new SKPaint { Color = PanelFill, IsAntialias = true })
            c.DrawRoundRect(rect, radius, radius, fill);
        using (var edge = new SKPaint {
            Color = PanelEdge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1
        }) c.DrawRoundRect(rect, radius, radius, edge);
    }

    // --- The banner -------------------------------------------------

    private void DrawBanner(SKCanvas c, string text) {
        var r = _l.Banner;
        Panel(c, r, Math.Max(3, r.Height * 0.22f));

        var padX = r.Height * 0.45f;
        var line = Fit(text, _banner, r.Width - padX * 2);
        var metrics = _banner.Metrics;
        var baseline = r.Y + (r.Height - (metrics.Descent - metrics.Ascent)) / 2 - metrics.Ascent;
        using var paint = new SKPaint { Color = Ink, IsAntialias = true };
        c.DrawText(line, r.X + padX, baseline, _banner, paint);
    }

    // --- Text -------------------------------------------------------

    private float Height(string[] lines, SKFont font) {
        var m = font.Metrics;
        return lines.Length * (m.Descent - m.Ascent + m.Leading);
    }

    private float DrawLines(SKCanvas c, string[] lines, SKFont font, float x, float y, SKColor colour) {
        var m = font.Metrics;
        var step = m.Descent - m.Ascent + m.Leading;
        using var paint = new SKPaint { Color = colour, IsAntialias = true };
        foreach (var line in lines) {
            c.DrawText(line, x, y - m.Ascent, font, paint);
            y += step;
        }
        return y;
    }

    /// <summary>Break text to the available width, measuring the real glyphs.
    /// Beyond <paramref name="maxLines"/> the last line is cut and ellipsised,
    /// so a long Wikipedia paragraph cannot push the card off the frame.</summary>
    internal static string[] Wrap(string text, SKFont font, float maxWidth, int maxLines) {
        if (string.IsNullOrWhiteSpace(text) || maxWidth <= 0 || maxLines <= 0) return Array.Empty<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = "";
        foreach (var word in words) {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate) <= maxWidth) { current = candidate; continue; }
            if (current.Length == 0) {
                // A single word wider than the card, which a long designation
                // or a URL can be. Cut it rather than let it run off the edge.
                lines.Add(Fit(word, font, maxWidth));
                current = "";
            } else {
                lines.Add(current);
                current = word;
            }
            if (lines.Count == maxLines) break;
        }
        if (lines.Count < maxLines && current.Length > 0) lines.Add(current);
        if (lines.Count == maxLines && current.Length > 0 && !lines[^1].EndsWith(current, StringComparison.Ordinal))
            lines[^1] = Ellipsise(lines[^1], font, maxWidth);
        return lines.ToArray();
    }

    /// <summary>One line, cut to fit with an ellipsis if it has to be.</summary>
    internal static string Fit(string text, SKFont font, float maxWidth) {
        if (maxWidth <= 0) return "";
        if (font.MeasureText(text) <= maxWidth) return text;
        return Ellipsise(text, font, maxWidth);
    }

    private static string Ellipsise(string text, SKFont font, float maxWidth) {
        const string tail = "...";
        var n = text.Length;
        while (n > 0 && font.MeasureText(text[..n] + tail) > maxWidth) n--;
        return n <= 0 ? "" : text[..n].TrimEnd(' ', ',', ';') + tail;
    }

    // --- Output -----------------------------------------------------

    /// <summary>RGBA to packed RGB24. Skia has no 24 bit surface, and sending
    /// the alpha would add a third again to what goes down the pipe every
    /// frame, which on an SBC is worth one pass over the buffer.</summary>
    private void PackRgb24() {
        var src = _canvasBitmap.GetPixelSpan();
        var dst = _rgb.AsSpan();
        int n = _l.Width * _l.Height;
        for (int i = 0, s = 0, d = 0; i < n; i++, s += 4, d += 3) {
            dst[d] = src[s];
            dst[d + 1] = src[s + 1];
            dst[d + 2] = src[s + 2];
        }
    }

    /// <summary>The composed frame as a PNG. Used by the preview endpoint and
    /// by the tests, which check the picture with an independent decoder
    /// rather than trusting the renderer's own account of itself.</summary>
    public byte[] ToPng() {
        using var image = SKImage.FromBitmap(_canvasBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    public void Dispose() {
        _title.Dispose(); _subtitle.Dispose(); _chip.Dispose();
        _body.Dispose(); _banner.Dispose(); _credit.Dispose();
        _canvasBitmap.Dispose();
    }
}
