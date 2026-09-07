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

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using NINA.Image.ImageAnalysis;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// SKBitmap.SetPixels stores the bare pointer it is given and copies nothing,
/// so every read of those pixels has to happen while the array is still pinned.
///
/// Five call sites closed the fixed{} block first and read afterwards, and the
/// Orange Pi 4 Pro caught one on 2026-09-07 01:26:48: SIGSEGV with the PC in
/// libc, the return address in libSkiaSharp, and the faulting address inside
/// the reserved, decommitted tail of a .NET GC region. The 36 MB RGBA buffer of
/// a 3008x3008 colour stack lives on the large object heap, the service runs
/// with DOTNET_GCConserveMemory=5, which compacts that heap, and Skia was left
/// encoding from where the array used to be.
///
/// The race cannot be reproduced on demand, so this guards the shape instead:
/// no SetPixels may be the last statement of its pin.
/// </summary>
[TestFixture]
public class SkiaPinnedPixelsTests {

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    [Test]
    public void NoSetPixelsIsTheLastStatementOfItsPin() {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"),
                                                      "*.cs", SearchOption.AllDirectories)) {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
             || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) {
                continue;
            }
            var text = File.ReadAllText(file);
            int at = 0;
            while ((at = text.IndexOf("SetPixels(", at, StringComparison.Ordinal)) >= 0) {
                var rest = text[(at + "SetPixels(".Length)..];
                // A pointer handed over raw is the risky form; the
                // GetPixels/Marshal.Copy shape owns its storage and is fine.
                if (rest.StartsWith("(IntPtr)", StringComparison.Ordinal)
                    && !ReadsPixelsBeforeTheEndOfThePin(text, at)) {
                    var line = text[..at].Split('\n').Length;
                    offenders.Add($"{Path.GetFileName(file)}:{line}");
                }
                at += "SetPixels(".Length;
            }
        }

        Assert.That(offenders, Is.Empty,
            "SetPixels guarda o ponteiro cru e nao copia nada: a leitura dos pixels "
            + "(Copy, Encode, ...) tem de acontecer DENTRO do fixed{}, senao uma coleta "
            + "pode mover o array antes e o Skia le memoria ja devolvida. "
            + "Sitios fora do pin: " + string.Join(", ", offenders));
    }

    /// <summary>True when something that actually reads the pixels appears
    /// between the SetPixels call and the closing brace of its pin.</summary>
    private static bool ReadsPixelsBeforeTheEndOfThePin(string text, int setPixelsAt) {
        int depth = 0;
        for (int i = setPixelsAt; i < text.Length; i++) {
            char c = text[i];
            if (c == '{') { depth++; continue; }
            if (c == '}') {
                if (depth == 0) return false;      // pin closed, nothing read yet
                depth--;
                continue;
            }
            if (c != '.' && c != 'r') continue;
            foreach (var read in new[] { ".Copy(", ".Encode(", ".PeekPixels(", "return " }) {
                if (string.CompareOrdinal(text, i, read, 0, read.Length) == 0) return true;
            }
        }
        return false;
    }

    /// <summary>The path that crashed, exercised end to end at the size that
    /// crashed, so a refactor of the pinning cannot quietly break the encode.
    /// </summary>
    [Test]
    public void EncodeRgbStillProducesAReadableJpegAtStackSize() {
        const int w = 512, h = 512;          // same shape, small enough for CI
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < rgb.Length; i += 3) {
            rgb[i] = (byte)(i % 251);
            rgb[i + 1] = (byte)(i % 241);
            rgb[i + 2] = (byte)(i % 239);
        }

        var jpeg = JpegHelper.EncodeRgb(rgb, w, h, quality: 90);

        Assert.That(jpeg, Is.Not.Null.And.Length.GreaterThan(1024));
        Assert.That(jpeg[0], Is.EqualTo(0xFF), "assinatura JPEG");
        Assert.That(jpeg[1], Is.EqualTo(0xD8));
        using var decoded = SkiaSharp.SKBitmap.Decode(jpeg);
        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded.Width, Is.EqualTo(w));
        Assert.That(decoded.Height, Is.EqualTo(h));
    }
}
