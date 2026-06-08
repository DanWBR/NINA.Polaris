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

namespace NINA.Core.Model.Equipment;

[JsonObject(MemberSerialization.OptIn)]
public class FilterInfo {
    [JsonProperty] public string Name { get; set; } = string.Empty;
    [JsonProperty] public int Position { get; set; }
    [JsonProperty] public double FocusOffset { get; set; }
    [JsonProperty] public short AutoFocusExposureTime { get; set; } = 10;
    [JsonProperty] public int? FlatWizardFilterSettingsKey { get; set; }

    public FilterInfo() { }

    public FilterInfo(string name, int position, double focusOffset = 0) {
        Name = name;
        Position = position;
        FocusOffset = focusOffset;
    }

    public override string ToString() => $"{Name} (Pos: {Position})";
}