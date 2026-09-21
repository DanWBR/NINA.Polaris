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

/// <summary>The offset a capture carries from the rig. One field per
/// capturing panel, because a capture obeys the panel it came from; set means
/// sent, zero or unset means the driver keeps its own value and the frame's
/// header records that instead (issue #26).</summary>
[TestFixture]
public class RigCaptureDefaultsTests {

    private static ProfileService Profile(int? live = null, int? preview = null,
            int? autorun = null, int? adv = null) {
        var p = new ProfileService(new ConfigurationBuilder().Build(), NullLogger<ProfileService>.Instance);
        p.ActiveEquipmentProfile!.DefaultOffset = live;
        p.ActiveEquipmentProfile!.PreviewOffset = preview;
        p.ActiveEquipmentProfile!.AutorunOffset = autorun;
        p.ActiveEquipmentProfile!.AdvOffset = adv;
        return p;
    }

    [Test]
    public void Offset_FollowsTheLivePanel() {
        Assert.That(RigCaptureDefaults.Offset(Profile(live: 50)), Is.EqualTo(50));
        Assert.That(RigCaptureDefaults.Offset(Profile(live: 0)), Is.Null);
        Assert.That(RigCaptureDefaults.Offset(Profile(live: null)), Is.Null);
        Assert.That(RigCaptureDefaults.Offset(null), Is.Null);
    }

    [Test]
    public void PreviewOffset_FollowsThePreviewPanel() {
        Assert.That(RigCaptureDefaults.PreviewOffset(Profile(preview: 30)), Is.EqualTo(30));
        Assert.That(RigCaptureDefaults.PreviewOffset(Profile(preview: 0)), Is.Null);
        Assert.That(RigCaptureDefaults.PreviewOffset(Profile(preview: null)), Is.Null);
        Assert.That(RigCaptureDefaults.PreviewOffset(null), Is.Null);
    }

    [Test]
    public void AutorunOffset_FollowsTheAutorunPanel() {
        Assert.That(RigCaptureDefaults.AutorunOffset(Profile(autorun: 64)), Is.EqualTo(64));
        Assert.That(RigCaptureDefaults.AutorunOffset(Profile(autorun: 0)), Is.Null);
        Assert.That(RigCaptureDefaults.AutorunOffset(Profile(autorun: null)), Is.Null);
        Assert.That(RigCaptureDefaults.AutorunOffset(null), Is.Null);
    }

    [Test]
    public void AdvOffset_FollowsTheAdvPanel() {
        Assert.That(RigCaptureDefaults.AdvOffset(Profile(adv: 12)), Is.EqualTo(12));
        Assert.That(RigCaptureDefaults.AdvOffset(Profile(adv: 0)), Is.Null);
        Assert.That(RigCaptureDefaults.AdvOffset(Profile(adv: null)), Is.Null);
        Assert.That(RigCaptureDefaults.AdvOffset(null), Is.Null);
    }

    /// <summary>The four are independent. A shared value was the bug: typing
    /// one number in PREVIEW changed what a live stack or a whole sequence
    /// would use, silently.</summary>
    [Test]
    public void EachPanelReadsOnlyItsOwnField() {
        var p = Profile(live: 10, preview: 20, autorun: 30, adv: 40);
        Assert.That(RigCaptureDefaults.Offset(p), Is.EqualTo(10));
        Assert.That(RigCaptureDefaults.PreviewOffset(p), Is.EqualTo(20));
        Assert.That(RigCaptureDefaults.AutorunOffset(p), Is.EqualTo(30));
        Assert.That(RigCaptureDefaults.AdvOffset(p), Is.EqualTo(40));

        // And one panel at zero does not drag the others down with it.
        var mixed = Profile(live: 0, preview: 20, autorun: 0, adv: 40);
        Assert.That(RigCaptureDefaults.Offset(mixed), Is.Null);
        Assert.That(RigCaptureDefaults.PreviewOffset(mixed), Is.EqualTo(20));
        Assert.That(RigCaptureDefaults.AutorunOffset(mixed), Is.Null);
        Assert.That(RigCaptureDefaults.AdvOffset(mixed), Is.EqualTo(40));
    }
}
