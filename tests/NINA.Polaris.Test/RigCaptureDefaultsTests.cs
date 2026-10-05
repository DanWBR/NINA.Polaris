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

/// <summary>
/// The offset a capture carries from the rig. Set means sent to the driver;
/// zero or unset means the driver keeps its own value and the frame's header
/// records that instead (issue #26).
///
/// There used to be four of these, one per capturing panel, on the theory that
/// a capture should obey the panel it came from. What that produced in the
/// field was an operator typing a pedestal into one panel and getting a
/// different one out of a frame started from another. One rig, one offset.
/// </summary>
[TestFixture]
public class RigCaptureDefaultsTests {

    private static ProfileService Profile(int? offset) {
        var p = new ProfileService(new ConfigurationBuilder().Build(),
                                   NullLogger<ProfileService>.Instance);
        p.ActiveEquipmentProfile!.DefaultOffset = offset;
        return p;
    }

    [Test]
    public void AnOffsetIsSentWhenItIsSet() {
        Assert.That(RigCaptureDefaults.Offset(Profile(50)), Is.EqualTo(50));
    }

    /// <summary>Zero is not "offset zero", it is "do not write one". The
    /// distinction is the whole of issue #26: a camera whose driver is set to
    /// 125 must keep 125, not be pushed to 0.</summary>
    [Test]
    public void ZeroMeansWriteNothing() {
        Assert.That(RigCaptureDefaults.Offset(Profile(0)), Is.Null);
    }

    [Test]
    public void UnsetMeansWriteNothing() {
        Assert.That(RigCaptureDefaults.Offset(Profile(null)), Is.Null);
    }

    /// <summary>The test doubles construct without DI, and a capture must not
    /// fall over because nothing told it about a rig.</summary>
    [Test]
    public void NoProfileAtAllIsNotAnError() {
        Assert.That(RigCaptureDefaults.Offset(null), Is.Null);
    }
}
