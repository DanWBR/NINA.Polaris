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


using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>The offset every main-camera capture carries from the rig: set
/// means sent, zero or unset means the driver keeps its own.</summary>
[TestFixture]
public class RigCaptureDefaultsTests {

    private static ProfileService Profile(int? offset) {
        var p = new ProfileService(new ConfigurationBuilder().Build(), NullLogger<ProfileService>.Instance);
        p.ActiveEquipmentProfile!.DefaultOffset = offset;
        return p;
    }

    [Test]
    public void Offset_FollowsTheRig() {
        Assert.That(RigCaptureDefaults.Offset(Profile(50)), Is.EqualTo(50));
        Assert.That(RigCaptureDefaults.Offset(Profile(0)), Is.Null);
        Assert.That(RigCaptureDefaults.Offset(Profile(null)), Is.Null);
        Assert.That(RigCaptureDefaults.Offset(null), Is.Null);
    }
}
