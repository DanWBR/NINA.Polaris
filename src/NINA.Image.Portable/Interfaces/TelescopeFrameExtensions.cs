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

using NINA.Core.Utility;

namespace NINA.Image.Interfaces;

public static class TelescopeFrameExtensions {
    /// <summary>The mount's pointing of date (JNow), for an hour angle, an
    /// altitude or a meridian decision, all of which are measured against
    /// the sky of date. <see cref="ITelescope.RightAscension"/> and
    /// <see cref="ITelescope.Declination"/> are J2000; anything that slews,
    /// syncs, solves or draws on SKY uses those.</summary>
    public static (double RaHours, double DecDeg) PointingOfDate(this ITelescope telescope)
        => EquatorialEpoch.J2000ToJNow(telescope.RightAscension, telescope.Declination, DateTime.UtcNow);
}
