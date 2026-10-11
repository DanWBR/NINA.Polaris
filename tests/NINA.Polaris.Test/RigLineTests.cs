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

using NINA.Polaris.Services.Broadcast;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The two lines at the top of a broadcast. "What scope is that?" is the
/// question asked in every astrophotography live chat, so the answer is on
/// screen from the first frame, and it has to hold together for a rig that was
/// only half filled in.
/// </summary>
[TestFixture]
public class RigLineTests {

    private static readonly RigFacts Full = new() {
        RigName = "Quintal",
        FocalLengthMm = 550, ApertureMm = 100,
        Camera = "ASI2600MC Pro", Mount = "AM5",
        FilterWheel = "EFW 7x36", Focuser = "EAF",
        GuideCamera = "ASI120MM Mini", Guider = "PHD2"
    };

    [Test]
    public void TheWholeRigReadsAsOneLine() {
        var line = RigLine.Equipment(Full);
        Assert.That(line, Is.EqualTo(
            "550 mm f/5.5  ·  ASI2600MC Pro  ·  AM5  ·  EFW 7x36  ·  EAF  ·  ASI120MM Mini + PHD2"));
    }

    [Test]
    public void OpticsComeFirstBecauseThatIsWhatPeopleAsk() {
        Assert.That(RigLine.Equipment(Full), Does.StartWith("550 mm f/5.5"));
    }

    [Test]
    public void AnApertureNobodyEnteredIsNotAnInfiniteFRatio() {
        // Very common: the focal length matters for plate solving so it gets
        // filled in, and the aperture never does.
        var noAperture = Full with { ApertureMm = 0 };
        Assert.That(RigLine.Equipment(noAperture), Does.StartWith("550 mm  ·"));
        Assert.That(RigLine.Equipment(noAperture), Does.Not.Contain("f/"));
    }

    [Test]
    public void AShortFocalLengthKeepsItsDecimal() {
        Assert.That(RigLine.Equipment(new RigFacts { FocalLengthMm = 85.5, ApertureMm = 61 }),
            Is.EqualTo("85.5 mm f/1.4"));
    }

    [Test]
    public void MissingDevicesLeaveNoGapsBehind() {
        // A rig with a camera and nothing else must not come out as a row of
        // separators with air between them.
        var sparse = new RigFacts { Camera = "ASI294MC", FocalLengthMm = 0 };
        Assert.That(RigLine.Equipment(sparse), Is.EqualTo("ASI294MC"));

        var nothing = new RigFacts();
        Assert.That(RigLine.Equipment(nothing), Is.Empty);
    }

    [Test]
    public void ADeselectedDriverIsNotAPieceOfEquipment() {
        // "None" left over from a device that was selected and then cleared
        // looks like a fault when it is drawn on a broadcast.
        var withNone = new RigFacts { Camera = "ASI2600MC Pro", FilterWheel = "None", Focuser = "  " };
        Assert.That(RigLine.Equipment(withNone), Is.EqualTo("ASI2600MC Pro"));
    }

    [Test]
    public void TheGuiderIsSoftwareAndRidesWithItsCamera() {
        Assert.That(RigLine.Equipment(new RigFacts { GuideCamera = "ASI120MM Mini", Guider = "PHD2" }),
            Is.EqualTo("ASI120MM Mini + PHD2"));
        Assert.That(RigLine.Equipment(new RigFacts { GuideCamera = "ASI120MM Mini" }),
            Is.EqualTo("ASI120MM Mini"));
        Assert.That(RigLine.Equipment(new RigFacts { Guider = "PHD2" }), Is.EqualTo("PHD2"));
    }

    [Test]
    public void ABroadcastHasANameWithoutAnyoneTypingOne() {
        Assert.That(RigLine.Title(null, null), Is.EqualTo("Polaris Live Stream"));
        Assert.That(RigLine.Title("", "  "), Is.EqualTo("Polaris Live Stream"));
    }

    [Test]
    public void TheRigNameFollowsTheTitleWhenItIsWorthSaying() {
        Assert.That(RigLine.Title("Céu do Sul ao vivo", "Quintal"),
            Is.EqualTo("Céu do Sul ao vivo  ·  Quintal"));
        // Every untouched install has a rig called Default, and putting that
        // on a broadcast tells a viewer nothing.
        Assert.That(RigLine.Title("Céu do Sul ao vivo", "Default"), Is.EqualTo("Céu do Sul ao vivo"));
        Assert.That(RigLine.Title(null, "Travel APO"), Is.EqualTo("Polaris Live Stream  ·  Travel APO"));
    }
}
