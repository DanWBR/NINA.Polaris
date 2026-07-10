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

using NUnit.Framework;
using NINA.Image.ImageAnalysis.AutoFocus;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

[TestFixture]
public class AutoFocusOptionsTests {

    [Test]
    public void Resolve_NullRequest_UsesProfileValues() {
        var profile = new AutoFocusSettings {
            StepSize = 80, OffsetSteps = 5, ExposureSeconds = 3.5, FramesPerPoint = 2,
            Method = "HYPERBOLIC", RSquaredThreshold = 0.8, Attempts = 3, MaxHfrRatio = 1.3,
            InnerCropRatio = 0.6, UseBrightestStars = 12,
            BacklashIn = 40, BacklashOut = 10, BacklashModel = "ABSOLUTE", MinStars = 8
        };

        var o = AutoFocusRunOptions.Resolve(null, profile);

        Assert.That(o.StepSize, Is.EqualTo(80));
        Assert.That(o.OffsetSteps, Is.EqualTo(5));
        Assert.That(o.ExposureSeconds, Is.EqualTo(3.5));
        Assert.That(o.FramesPerPoint, Is.EqualTo(2));
        Assert.That(o.Method, Is.EqualTo(AFCurveFittingMethod.Hyperbolic));
        Assert.That(o.RSquaredThreshold, Is.EqualTo(0.8));
        Assert.That(o.Attempts, Is.EqualTo(3));
        Assert.That(o.MaxHfrRatio, Is.EqualTo(1.3));
        Assert.That(o.InnerCropRatio, Is.EqualTo(0.6));
        Assert.That(o.UseBrightestStars, Is.EqualTo(12));
        Assert.That(o.BacklashIn, Is.EqualTo(40));
        Assert.That(o.BacklashOut, Is.EqualTo(10));
        Assert.That(o.OvershootBacklash, Is.False);
        Assert.That(o.MinStars, Is.EqualTo(8));
        Assert.That(o.FocuserSource, Is.EqualTo("main"));
    }

    [Test]
    public void Resolve_NullProfile_UsesDesktopDefaults() {
        var o = AutoFocusRunOptions.Resolve(null, null);

        Assert.That(o.StepSize, Is.EqualTo(50));
        Assert.That(o.OffsetSteps, Is.EqualTo(4));
        Assert.That(o.ExposureSeconds, Is.EqualTo(2.0));
        Assert.That(o.FramesPerPoint, Is.EqualTo(1));
        Assert.That(o.Method, Is.EqualTo(AFCurveFittingMethod.TrendHyperbolic));
        Assert.That(o.RSquaredThreshold, Is.EqualTo(0.7));
        Assert.That(o.Attempts, Is.EqualTo(2));
        Assert.That(o.MaxHfrRatio, Is.EqualTo(1.15));
        Assert.That(o.InnerCropRatio, Is.EqualTo(1.0));
        Assert.That(o.BacklashIn, Is.EqualTo(0));
        Assert.That(o.OvershootBacklash, Is.True);
        Assert.That(o.MinStars, Is.EqualTo(5));
    }

    [Test]
    public void Resolve_ExplicitFields_OverrideProfile() {
        var profile = new AutoFocusSettings { StepSize = 80, OffsetSteps = 5 };
        var req = new AutoFocusRequest { StepSize = 25, Method = "PARABOLIC" };

        var o = AutoFocusRunOptions.Resolve(req, profile);

        Assert.That(o.StepSize, Is.EqualTo(25));
        Assert.That(o.OffsetSteps, Is.EqualTo(5), "unset request field falls back to profile");
        Assert.That(o.Method, Is.EqualTo(AFCurveFittingMethod.Parabolic));
    }

    [Test]
    public void Resolve_LegacySteps_MapsToOffsetSteps() {
        // Old grid clients sent Steps=9 → offsetSteps 4 (same sweep span).
        var o = AutoFocusRunOptions.Resolve(new AutoFocusRequest { Steps = 9 }, null);
        Assert.That(o.OffsetSteps, Is.EqualTo(4));
    }

    [Test]
    public void Resolve_LegacyPointsPerSide_WinsOverSteps() {
        var o = AutoFocusRunOptions.Resolve(
            new AutoFocusRequest { Steps = 9, PointsPerSide = 3 }, null);
        Assert.That(o.OffsetSteps, Is.EqualTo(3));
    }

    [Test]
    public void Resolve_LegacyBacklashSteps_MapsToBacklashIn() {
        var o = AutoFocusRunOptions.Resolve(new AutoFocusRequest { BacklashSteps = 60 }, null);
        Assert.That(o.BacklashIn, Is.EqualTo(60));
        Assert.That(o.OvershootBacklash, Is.True);
    }

    [Test]
    public void Resolve_ExplicitBacklashIn_WinsOverLegacy() {
        var o = AutoFocusRunOptions.Resolve(
            new AutoFocusRequest { BacklashSteps = 60, BacklashIn = 30 }, null);
        Assert.That(o.BacklashIn, Is.EqualTo(30));
    }

    [Test]
    public void Resolve_ClampsOutOfRangeValues() {
        var o = AutoFocusRunOptions.Resolve(new AutoFocusRequest {
            OffsetSteps = 99, Attempts = 99, RSquaredThreshold = 5, InnerCropRatio = 0.01
        }, null);

        Assert.That(o.OffsetSteps, Is.EqualTo(10));
        Assert.That(o.Attempts, Is.EqualTo(5));
        Assert.That(o.RSquaredThreshold, Is.EqualTo(1));
        Assert.That(o.InnerCropRatio, Is.EqualTo(0.1));
    }

    [Test]
    public void Resolve_FocuserSource_Normalized() {
        var o = AutoFocusRunOptions.Resolve(new AutoFocusRequest { FocuserSource = " Guide " }, null);
        Assert.That(o.FocuserSource, Is.EqualTo("guide"));
    }
}
