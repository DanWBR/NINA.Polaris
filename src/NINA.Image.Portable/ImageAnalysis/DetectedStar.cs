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

namespace NINA.Image.ImageAnalysis;

public class DetectedStar {
    public double X { get; set; }
    public double Y { get; set; }
    public double HFR { get; set; }
    public double Peak { get; set; }
    public double Flux { get; set; }
    public int PixelCount { get; set; }

    /// <summary>Shape elongation from the flux-weighted second moments:
    /// 0 = perfectly round, →1 = highly elongated
    /// (sqrt(1 - minorAxis²/majorAxis²)). Used by the STUDIO aberration
    /// analyzer to tell coma / astigmatism from a round-but-soft field.</summary>
    public double Eccentricity { get; set; }

    /// <summary>Major-axis angle of the star ellipse in radians
    /// (0.5·atan2(2·Mxy, Mxx-Myy)), image coordinates. Only meaningful
    /// when <see cref="Eccentricity"/> is non-trivial.</summary>
    public double OrientationRad { get; set; }

    public double DistanceTo(DetectedStar other) {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}