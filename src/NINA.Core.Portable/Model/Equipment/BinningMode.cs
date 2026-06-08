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

using Newtonsoft.Json;
using System.Xml.Serialization;

namespace NINA.Core.Model.Equipment;

[JsonObject(MemberSerialization.OptIn)]
[Serializable]
[XmlRoot(ElementName = nameof(BinningMode))]
public class BinningMode {
    private const char SEPARATOR = 'x';

    private BinningMode() { }

    public BinningMode(short x, short y) {
        X = x;
        Y = y;
    }

    public string Name => string.Join(SEPARATOR.ToString(), X, Y);

    [XmlElement(nameof(X))]
    [JsonProperty(PropertyName = nameof(X))]
    public short X { get; set; }

    [XmlElement(nameof(Y))]
    [JsonProperty(PropertyName = nameof(Y))]
    public short Y { get; set; }

    public override string ToString() => Name;

    public override bool Equals(object? obj) {
        if (obj is not BinningMode other) return false;
        return X == other.X && Y == other.Y;
    }

    public override int GetHashCode() {
        const int primeNumber = 397;
        unchecked {
            return (X.GetHashCode() * primeNumber) ^ Y.GetHashCode();
        }
    }

    public static bool TryParse(string s, out BinningMode? mode) {
        mode = null;
        if (string.IsNullOrEmpty(s)) return false;
        var parts = s.Split(SEPARATOR);
        if (parts.Length != 2) return false;
        if (!short.TryParse(parts[0], out short x)) return false;
        if (!short.TryParse(parts[1], out short y)) return false;
        mode = new BinningMode(x, y);
        return true;
    }
}