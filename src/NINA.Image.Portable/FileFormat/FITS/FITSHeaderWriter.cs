// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

using System.Text;

namespace NINA.Image.FileFormat.FITS;

/// <summary>
/// In-place editor for FITS header keywords. Rewrites individual 80-byte
/// cards inside an existing file without touching the pixel data block.
///
/// Supported operations:
///   - Update the value of an existing keyword (the 80-byte card is
///     rewritten in place, no size change).
///   - Insert a new keyword before the END card. When the current header
///     block has no free slots the block is extended by one 2880-byte
///     block (the pixel data and any trailing extensions shift forward).
///
/// The FITS standard requires header blocks to be a multiple of 2880
/// bytes (36 cards of 80 bytes each). This writer maintains that
/// invariant at all times.
/// </summary>
public static class FITSHeaderWriter {
    private const int BLOCK_SIZE = 2880;
    private const int CARD_SIZE = 80;

    /// <summary>
    /// Open an existing FITS file and update (or insert) the given
    /// header keywords. Pixel data stays untouched.
    /// </summary>
    /// <param name="filePath">Absolute path to the FITS file.</param>
    /// <param name="updates">
    /// Keyword/value pairs to write. Keywords that already exist in
    /// the header are updated in place. Keywords that do not exist
    /// are inserted immediately before the END card (growing the
    /// header block if necessary).
    /// </param>
    public static void UpdateHeaders(string filePath,
                                     IReadOnlyList<(string Keyword, string Value)> updates) {
        if (updates == null || updates.Count == 0) return;

        // Read the entire header region into memory (typically 1-3
        // blocks, 2880-8640 bytes). We need random access to
        // individual cards, so a byte[] is simplest.
        byte[] headerBytes;
        long headerLength;
        long pixelDataStart;

        using (var fs = new FileStream(filePath, FileMode.Open,
                                        FileAccess.Read, FileShare.Read)) {
            (headerBytes, headerLength) = ReadHeaderBlocks(fs);
            pixelDataStart = headerLength;
        }

        // Build a mutable list of 80-byte card images.
        var cards = new List<byte[]>();
        for (int i = 0; i < headerBytes.Length; i += CARD_SIZE) {
            var card = new byte[CARD_SIZE];
            Array.Copy(headerBytes, i, card, 0, CARD_SIZE);
            cards.Add(card);
        }

        // Apply each update: overwrite existing card or queue for
        // insertion before END.
        var toInsert = new List<(string Keyword, string Value)>();

        foreach (var (keyword, value) in updates) {
            int idx = FindCard(cards, keyword);
            if (idx >= 0) {
                // Overwrite existing card.
                cards[idx] = FormatCard(keyword, value);
            } else {
                toInsert.Add((keyword, value));
            }
        }

        // Insert new keywords before END.
        if (toInsert.Count > 0) {
            int endIdx = FindEndCard(cards);
            if (endIdx < 0) {
                // Degenerate: no END found. Append one.
                endIdx = cards.Count;
                cards.Add(FormatEndCard());
            }
            foreach (var (keyword, value) in toInsert) {
                cards.Insert(endIdx, FormatCard(keyword, value));
                endIdx++; // END shifted one slot forward
            }
        }

        // Re-pad to a 2880-byte block boundary.
        while (cards.Count % 36 != 0) {
            // Blank padding card (spaces).
            cards.Add(Encoding.ASCII.GetBytes(new string(' ', CARD_SIZE)));
        }

        // Flatten back to a byte[].
        var newHeader = new byte[cards.Count * CARD_SIZE];
        for (int i = 0; i < cards.Count; i++) {
            Array.Copy(cards[i], 0, newHeader, i * CARD_SIZE, CARD_SIZE);
        }

        long newHeaderLength = newHeader.Length;

        if (newHeaderLength == headerLength) {
            // Header size unchanged -- write header in place, pixel
            // data doesn't move.
            using var fs = new FileStream(filePath, FileMode.Open,
                                           FileAccess.Write, FileShare.None);
            fs.Seek(0, SeekOrigin.Begin);
            fs.Write(newHeader, 0, newHeader.Length);
            // File length stays the same.
        } else {
            // Header grew (new keywords inserted). We need to shift
            // the pixel data forward. Safest approach: read the tail,
            // then rewrite from the front.
            byte[] tail;
            using (var fs = new FileStream(filePath, FileMode.Open,
                                            FileAccess.Read, FileShare.Read)) {
                fs.Seek(pixelDataStart, SeekOrigin.Begin);
                tail = new byte[fs.Length - pixelDataStart];
                int read = 0;
                while (read < tail.Length) {
                    int n = fs.Read(tail, read, tail.Length - read);
                    if (n == 0) break;
                    read += n;
                }
            }

            using var ws = new FileStream(filePath, FileMode.Open,
                                           FileAccess.Write, FileShare.None);
            ws.SetLength(newHeaderLength + tail.Length);
            ws.Seek(0, SeekOrigin.Begin);
            ws.Write(newHeader, 0, newHeader.Length);
            ws.Write(tail, 0, tail.Length);
        }
    }

    // ---- internals -------------------------------------------------------

    /// <summary>
    /// Read all 2880-byte header blocks from the stream until END is
    /// found (or the stream ends). Returns the raw bytes and the total
    /// header region length.
    /// </summary>
    private static (byte[] bytes, long length) ReadHeaderBlocks(Stream stream) {
        var blocks = new List<byte[]>();
        var block = new byte[BLOCK_SIZE];
        bool endFound = false;
        while (!endFound) {
            int bytesRead = stream.Read(block, 0, BLOCK_SIZE);
            if (bytesRead < BLOCK_SIZE) {
                // Pad incomplete final block with spaces.
                for (int i = bytesRead; i < BLOCK_SIZE; i++) block[i] = (byte)' ';
            }
            var copy = new byte[BLOCK_SIZE];
            Array.Copy(block, copy, BLOCK_SIZE);
            blocks.Add(copy);
            // Scan for END keyword.
            for (int i = 0; i < BLOCK_SIZE; i += CARD_SIZE) {
                var keyword = Encoding.ASCII.GetString(copy, i, 8).TrimEnd();
                if (keyword == "END") { endFound = true; break; }
            }
            if (bytesRead < BLOCK_SIZE) break;
        }
        long totalLen = (long)blocks.Count * BLOCK_SIZE;
        var result = new byte[totalLen];
        for (int i = 0; i < blocks.Count; i++) {
            Array.Copy(blocks[i], 0, result, i * BLOCK_SIZE, BLOCK_SIZE);
        }
        return (result, totalLen);
    }

    /// <summary>
    /// Find the index of the card with the given keyword (case-insensitive).
    /// Returns -1 if not found.
    /// </summary>
    private static int FindCard(List<byte[]> cards, string keyword) {
        var upper = keyword.ToUpperInvariant().PadRight(8);
        for (int i = 0; i < cards.Count; i++) {
            var kw = Encoding.ASCII.GetString(cards[i], 0, 8).TrimEnd().ToUpperInvariant();
            if (kw == keyword.ToUpperInvariant()) return i;
        }
        return -1;
    }

    /// <summary>
    /// Find the index of the END card.
    /// </summary>
    private static int FindEndCard(List<byte[]> cards) {
        for (int i = 0; i < cards.Count; i++) {
            var kw = Encoding.ASCII.GetString(cards[i], 0, 8).TrimEnd();
            if (kw == "END") return i;
        }
        return -1;
    }

    /// <summary>
    /// Format a single 80-byte FITS header card. String values are
    /// single-quoted; numeric/boolean values are right-justified in
    /// columns 11-30 (0-indexed).
    /// </summary>
    private static byte[] FormatCard(string keyword, string value) {
        // Determine whether the value looks like a string or a
        // number/boolean. FITS convention: T/F booleans and numbers
        // are unquoted, everything else is single-quoted.
        string cardStr;
        var trimmed = value.Trim();
        if (IsNumericOrBool(trimmed)) {
            cardStr = $"{keyword.ToUpperInvariant(),-8}= {trimmed,20}";
        } else {
            // String value: quote it.
            var escaped = trimmed.Replace("'", "''");
            if (escaped.Length > 68) escaped = escaped.Substring(0, 68);
            cardStr = $"{keyword.ToUpperInvariant(),-8}= '{escaped}'";
            // Pad the value area to at least column 30 for readability.
            if (cardStr.Length < 30) cardStr = cardStr.PadRight(30);
        }
        cardStr = cardStr.Length > 80 ? cardStr.Substring(0, 80) : cardStr.PadRight(80);
        return Encoding.ASCII.GetBytes(cardStr);
    }

    private static byte[] FormatEndCard() {
        return Encoding.ASCII.GetBytes("END".PadRight(80));
    }

    private static bool IsNumericOrBool(string s) {
        if (s is "T" or "F") return true;
        // Try parsing as a number (integers and floats).
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out _);
    }
}