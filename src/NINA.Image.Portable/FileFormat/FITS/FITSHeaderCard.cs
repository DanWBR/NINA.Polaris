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

namespace NINA.Image.FileFormat.FITS;

public record FITSHeaderCard(string Keyword, string Value, string? Comment = null) {
    public const int CARD_LENGTH = 80;
    public const int KEYWORD_LENGTH = 8;

    public static FITSHeaderCard? Parse(ReadOnlySpan<byte> card) {
        if (card.Length < CARD_LENGTH) return null;

        var keyword = System.Text.Encoding.ASCII.GetString(card[..KEYWORD_LENGTH]).TrimEnd();
        if (keyword == "END") return new FITSHeaderCard("END", "");

        if (card[8] != '=' || card[9] != ' ') {
            return new FITSHeaderCard(keyword, "", System.Text.Encoding.ASCII.GetString(card[8..]).Trim());
        }

        var valueComment = System.Text.Encoding.ASCII.GetString(card[10..]).TrimEnd();
        string value;
        string? comment = null;

        if (valueComment.StartsWith('\'')) {
            int endQuote = valueComment.IndexOf('\'', 1);
            if (endQuote > 0) {
                value = valueComment[1..endQuote].TrimEnd();
                int slashPos = valueComment.IndexOf('/', endQuote);
                if (slashPos >= 0) comment = valueComment[(slashPos + 1)..].Trim();
            } else {
                value = valueComment[1..].TrimEnd();
            }
        } else {
            int slashPos = valueComment.IndexOf('/');
            if (slashPos >= 0) {
                value = valueComment[..slashPos].Trim();
                comment = valueComment[(slashPos + 1)..].Trim();
            } else {
                value = valueComment.Trim();
            }
        }

        return new FITSHeaderCard(keyword, value, comment);
    }
}