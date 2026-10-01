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

namespace NINA.Polaris.Services.Broadcast;

/// <summary>How the picture is placed in the frame.</summary>
public static class PictureFits {
    /// <summary>Whole sensor visible, bars where the shapes differ.</summary>
    public const string Fit = "fit";
    /// <summary>Fill the frame and let the overflow be cropped.</summary>
    public const string Fill = "fill";

    public static readonly string[] All = { Fit, Fill };

    public static bool IsValid(string? id) {
        foreach (var f in All) if (string.Equals(f, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string Parse(string? id) {
        foreach (var f in All) if (string.Equals(f, id, StringComparison.OrdinalIgnoreCase)) return f;
        return Fit;
    }
}

/// <summary>An integer rectangle. Deliberately not a Skia type: the geometry
/// is arithmetic and is tested without a graphics library.</summary>
public readonly record struct BroadcastRect(int X, int Y, int Width, int Height) {
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Where everything sits on a broadcast frame, for a given canvas size.
///
/// <para>Every number is derived from the canvas rather than fixed, because
/// the same layout has to read at 480p on a phone over a hotspot and at 1080p
/// full screen. Proportions were chosen for the phone: that is the hard case,
/// and a card sized for a desktop viewer is unreadable there.</para>
///
/// <para>The picture fills the frame and the card and banner float over it,
/// rather than the picture being squeezed into a column beside the card. The
/// image is what people came to watch.</para>
/// </summary>
public sealed record BroadcastLayout {
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Margin from the frame edge. Players overscan, and text against
    /// the edge is the first thing to get clipped.</summary>
    public required int Pad { get; init; }

    /// <summary>The whole frame: the picture is letterboxed into this.</summary>
    public BroadcastRect Picture => new(0, 0, Width, Height);

    /// <summary>The object card, top right.</summary>
    public required BroadcastRect Card { get; init; }
    /// <summary>Inside the card, after its own padding.</summary>
    public required int CardInset { get; init; }
    /// <summary>The cutout at the top of the card. Square, because the DSS
    /// cutouts are.</summary>
    public required BroadcastRect CardThumb { get; init; }

    public required float TitleSize { get; init; }
    public required float SubtitleSize { get; init; }
    public required float ChipSize { get; init; }
    public required float BodySize { get; init; }
    public required float BannerSize { get; init; }
    public required float CreditSize { get; init; }

    /// <summary>The strip along the bottom holding the session numbers.</summary>
    public required BroadcastRect Banner { get; init; }

    /// <summary>
    /// The second picture, bottom left: an all sky camera, a camera pointed at
    /// the rig, or the guide frame.
    ///
    /// <para>Bottom left because the other three corners are taken. The card
    /// owns the right column down to the banner, and by 720p it already uses
    /// every pixel of that, so there is nothing to put underneath it.</para>
    /// </summary>
    public required BroadcastRect Pip { get; init; }
    public required float PipLabelSize { get; init; }

    /// <summary>The strip along the top: the name of the broadcast on one
    /// line, the equipment on the next. Empty when the broadcast has no
    /// header, and the card then starts at the top margin instead.</summary>
    public required BroadcastRect Header { get; init; }
    public required float HeaderTitleSize { get; init; }
    public required float HeaderRigSize { get; init; }

    /// <summary>The widest the card text may run before it has to wrap.</summary>
    public int CardTextWidth => Card.Width - CardInset * 2;

    /// <summary>Draw the card at the full height available to it instead of
    /// shrinking it to its contents. The description then gets as many lines
    /// as fit, which is the point: a taller panel with the same three lines
    /// in it would just be a bigger empty box.</summary>
    public bool CardFullHeight { get; init; }

    /// <param name="hasHeader">Whether to reserve the top strip. Decided once
    /// when the broadcast starts rather than per frame: a card that jumped
    /// down the screen the moment a rig name appeared would be worse than
    /// either arrangement on its own.</param>
    /// <param name="textScale">Percent, 100 being the size everything was
    /// designed at. The right size depends on how far away the viewer is and
    /// on the shape of the rig line, so it is a preference rather than
    /// something to derive: the first operator to run this in the field asked
    /// for smaller text on the same frame it was tuned on.</param>
    /// <param name="cardFullHeight">Let the object card use the whole column
    /// instead of shrinking to its text.</param>
    public static BroadcastLayout For(int width, int height, bool hasHeader = false,
                                      int textScale = 100, bool cardFullHeight = false) {
        var w = Math.Max(160, width);
        var h = Math.Max(120, height);
        var pad = Math.Max(6, w / 64);
        // Clamped here rather than trusted: it reaches this from a stored
        // configuration, and a zero would make every font size zero.
        var ts = Math.Clamp(textScale, 50, 200) / 100f;

        // A quarter of the width is enough for a name, a few facts and three
        // lines of description, and leaves three quarters of the frame as
        // picture. Clamped so it stays sane at both ends of the range.
        var cardW = Math.Clamp((int)(w * 0.26), 200, 460);
        var inset = Math.Max(6, cardW / 14);

        var bannerH = Math.Max(24, h / 11);
        var banner = new BroadcastRect(pad, h - pad - bannerH, w - pad * 2, bannerH);

        // Two lines: what the broadcast is, then what it is being made with.
        // Full width, mirroring the banner, so the frame reads as a picture
        // with a strip top and bottom rather than as furniture scattered over
        // it.
        var headerH = hasHeader ? Math.Max(30, (int)(h / 7.6)) : 0;
        var header = hasHeader
            ? new BroadcastRect(pad, pad, w - pad * 2, headerH)
            : new BroadcastRect(pad, pad, 0, 0);

        // A fixed 4:3 slot, and whatever arrives is fitted into it rather
        // than cropped: an all sky camera puts a circular fisheye in a square
        // frame, and cropping that cuts the horizon off all four sides.
        var pipW = Math.Clamp((int)(w * 0.22), 120, 380);
        var pipH = pipW * 3 / 4;
        var pip = new BroadcastRect(pad, banner.Y - pad - pipH, pipW, pipH);

        // The card's height is set by its content, which only the renderer
        // knows once the text is wrapped. This is the room it may use: below
        // the header, down to the banner.
        var cardTop = hasHeader ? header.Bottom + pad : pad;
        var cardMaxH = banner.Y - pad - cardTop;
        var card = new BroadcastRect(w - pad - cardW, cardTop, cardW, cardMaxH);

        // The cutout is a wide crop, not the full square the pack ships. A
        // square at card width takes half the panel, and with a header above
        // there is no longer half a panel to spare. The middle of a DSS cutout
        // is the part worth showing anyway.
        var thumbW = cardW - inset * 2;
        var thumb = new BroadcastRect(card.X + inset, card.Y + inset, thumbW, (int)(thumbW * 0.62));

        return new BroadcastLayout {
            Width = w,
            Height = h,
            Pad = pad,
            Card = card,
            Banner = banner,
            Pip = pip,
            PipLabelSize = Math.Max(8f, pipW * 0.075f) * ts,
            Header = header,
            // The header strip is a fixed band, so its text is clamped to what
            // still fits in it: scaling the type up without the band would
            // push the rig line out of its own strip.
            HeaderTitleSize = Math.Min(headerH * 0.42f, Math.Max(12f, headerH * 0.34f) * ts),
            HeaderRigSize = Math.Min(headerH * 0.28f, Math.Max(9f, headerH * 0.23f) * ts),
            CardInset = inset,
            CardThumb = thumb,
            CardFullHeight = cardFullHeight,
            TitleSize = Math.Max(11f, cardW / 11f) * ts,
            SubtitleSize = Math.Max(9f, cardW / 21f) * ts,
            ChipSize = Math.Max(9f, cardW / 22f) * ts,
            BodySize = Math.Max(9f, cardW / 20f) * ts,
            CreditSize = Math.Max(8f, cardW / 26f) * ts,
            BannerSize = Math.Min(bannerH * 0.55f, Math.Max(11f, bannerH * 0.42f) * ts)
        };
    }

    /// <summary>
    /// Fit a picture of <paramref name="srcW"/> by <paramref name="srcH"/> into
    /// <paramref name="into"/>, keeping its shape and centring it.
    ///
    /// <para>Letterboxed, never cropped. A broadcast frame is 16:9 and a
    /// sensor almost never is; cropping to fill would cut the top and bottom
    /// off every image, which on a tall galaxy is the galaxy.</para>
    /// </summary>
    /// <summary>
    /// The opposite of <see cref="Letterbox"/>: scale a picture until it
    /// covers <paramref name="into"/> entirely and centre it, letting the
    /// overflow fall outside. The caller has to clip.
    ///
    /// <para>For the cutout on the card, where a letterbox would leave bars
    /// inside the panel. Cropping is safe there and not on the main picture:
    /// the cutouts are centred on the object by construction, so the part that
    /// goes over the edge is empty sky.</para>
    /// </summary>
    public static BroadcastRect Cover(int srcW, int srcH, BroadcastRect into) {
        if (srcW <= 0 || srcH <= 0 || into.IsEmpty) return new BroadcastRect(into.X, into.Y, 0, 0);
        var scale = Math.Max((double)into.Width / srcW, (double)into.Height / srcH);
        var w = Math.Max(1, (int)Math.Ceiling(srcW * scale));
        var h = Math.Max(1, (int)Math.Ceiling(srcH * scale));
        return new BroadcastRect(into.X + (into.Width - w) / 2, into.Y + (into.Height - h) / 2, w, h);
    }

    public static BroadcastRect Letterbox(int srcW, int srcH, BroadcastRect into) {
        if (srcW <= 0 || srcH <= 0 || into.IsEmpty) return new BroadcastRect(into.X, into.Y, 0, 0);
        var scale = Math.Min((double)into.Width / srcW, (double)into.Height / srcH);
        var w = Math.Max(1, (int)Math.Round(srcW * scale));
        var h = Math.Max(1, (int)Math.Round(srcH * scale));
        return new BroadcastRect(into.X + (into.Width - w) / 2, into.Y + (into.Height - h) / 2, w, h);
    }
}
