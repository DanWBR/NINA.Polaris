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
/// The typefaces the broadcast draws with.
///
/// <para>Polaris vendors its interface fonts as woff2, which the browser wants
/// and which neither Skia nor freetype can open, so the same family is also
/// shipped as TrueType for the host to render with. Without that the overlay
/// would depend on whatever fonts the operating system happens to have, and a
/// minimal Debian image for a headless board frequently has none at all: the
/// card would come out blank on exactly the installs this feature is for.</para>
///
/// <para>System fonts are still tried, so a host that has had the bundled
/// files stripped out degrades to something readable instead of nothing.</para>
/// </summary>
public sealed class BroadcastFonts : IDisposable {

    private static readonly string[] LinuxCandidates = {
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf"
    };

    private static readonly string[] WindowsCandidates = {
        @"C:\Windows\Fonts\segoeui.ttf", @"C:\Windows\Fonts\arial.ttf"
    };

    private static readonly string[] MacCandidates = {
        "/System/Library/Fonts/Helvetica.ttc", "/Library/Fonts/Arial.ttf"
    };

    public SKTypeface Regular { get; }
    public SKTypeface Bold { get; }
    /// <summary>Where the regular face came from, for the status block and for
    /// answering "why is the card blank".</summary>
    public string Source { get; }

    public BroadcastFonts(string? webRoot) {
        var bundled = webRoot == null ? null : Path.Combine(webRoot, "fonts", "plex-sans");
        Regular = Load(bundled, "IBMPlexSans-Regular.ttf", out var from)
                  ?? SKTypeface.Default;
        Bold = Load(bundled, "IBMPlexSans-Bold.ttf", out _) ?? Regular;
        Source = from ?? "the default typeface";
    }

    private static SKTypeface? Load(string? bundledDir, string fileName, out string? source) {
        source = null;
        if (bundledDir != null) {
            var p = Path.Combine(bundledDir, fileName);
            if (File.Exists(p)) {
                var tf = SKTypeface.FromFile(p);
                if (tf != null) { source = p; return tf; }
            }
        }
        foreach (var c in SystemCandidates()) {
            if (!File.Exists(c)) continue;
            var tf = SKTypeface.FromFile(c);
            if (tf != null) { source = c; return tf; }
        }
        return null;
    }

    private static string[] SystemCandidates() {
        if (OperatingSystem.IsWindows()) return WindowsCandidates;
        if (OperatingSystem.IsMacOS()) return MacCandidates;
        return LinuxCandidates;
    }

    public void Dispose() {
        Regular.Dispose();
        if (!ReferenceEquals(Bold, Regular)) Bold.Dispose();
    }
}
