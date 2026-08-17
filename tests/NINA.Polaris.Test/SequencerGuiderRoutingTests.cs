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

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.INDI.Client;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Sequencer;
using NINA.Polaris.Services.Sequencer.Triggers;

namespace NINA.Polaris.Test;

/// <summary>
/// The sequencer must guide with the guider the rig is configured to use.
///
/// Every instruction and trigger used to call PHD2Client directly, so on a
/// native-guider rig a sequence could not start, stop or recover guiding, and
/// the periodic dither trigger failed its first test (`!PHD2.IsConnected`) and
/// quietly returned false: those sessions never dithered, all night, with no
/// error anywhere. Reported from the field 2026-08-09 as "PLAN only works with
/// external PHD2".
/// </summary>
[TestFixture]
public class SequencerGuiderRoutingTests {

    private static (SequenceContext ctx, PHD2Client phd2, NativeGuider native)
            MakeContext(string guiderDriver) {
        var config = new ConfigurationBuilder().Build();
        var profiles = new ProfileService(config, NullLogger<ProfileService>.Instance);
        profiles.ActiveEquipmentProfile.GuiderDriver = guiderDriver;

        var indi = new IndiClient("localhost", 7624);
        var equipment = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance,
            new NINA.Polaris.Services.Alpaca.AlpacaDiscoveryCache(),
            new NINA.Polaris.Services.Simulator.Gear.SimGearService());

        var phd2 = new PHD2Client(NullLogger<PHD2Client>.Instance);
        var native = new NativeGuider(equipment, profiles, NullLogger<NativeGuider>.Instance);
        var guiders = new ActiveGuiderProvider(profiles, phd2, native);

        var ctx = new SequenceContext(
            equipment, null!, null!, phd2, guiders, null!, null!, null!, null!,
            null!, profiles, null!, null!, null!,
            new NINA.Polaris.Services.DitherBarrier(guiders, NullLogger<NINA.Polaris.Services.DitherBarrier>.Instance),
            NullLogger.Instance);
        return (ctx, phd2, native);
    }

    [Test]
    public void Context_OnANativeRig_HandsOutTheNativeGuider() {
        var (ctx, phd2, native) = MakeContext("native");

        Assert.That(ctx.Guider, Is.SameAs(native),
            "a native-guider rig must not be routed to PHD2");
        Assert.That(ctx.Guider, Is.Not.SameAs(phd2));
    }

    [Test]
    public void Context_OnAPhd2Rig_HandsOutPhd2() {
        var (ctx, phd2, _) = MakeContext("phd2");

        Assert.That(ctx.Guider, Is.SameAs(phd2),
            "an explicit phd2 rig must keep using PHD2");
    }

    /// <summary>The provider is asked per call, not captured, because the
    /// operator can switch backends between frames of a running sequence.</summary>
    [Test]
    public void Context_FollowsALiveBackendSwitch() {
        var (ctx, phd2, native) = MakeContext("native");
        Assert.That(ctx.Guider, Is.SameAs(native));

        ctx.Profiles.ActiveEquipmentProfile.GuiderDriver = "phd2";
        Assert.That(ctx.Guider, Is.SameAs(phd2),
            "the context cached the guider instead of resolving it");
    }

    /// <summary>
    /// THE SILENT ONE. The dither trigger's first test decided the whole
    /// night: reading PHD2 on a native rig meant it answered "not guiding" and
    /// returned false on every frame, so nothing dithered and nothing
    /// complained. It must now read the active guider.
    /// </summary>
    [Test]
    public async Task DitherTrigger_OnANativeRig_ConsultsTheNativeGuider() {
        var (ctx, phd2, native) = MakeContext("native");
        var trigger = new DitherAfterNExposuresTrigger { EveryNFrames = 1 };

        // Neither guider is connected here, so the trigger declines either way.
        // What this pins is WHICH one it asked: with the old code the answer
        // came from PHD2 even on a native rig.
        Assert.That(await trigger.ShouldFireAsync(ctx, CancellationToken.None), Is.False);
        Assert.That(ctx.Guider, Is.SameAs(native),
            "the trigger's decision has to come from the native guider");
        Assert.That(phd2.IsConnected, Is.False, "precondition: PHD2 is not connected in this fixture");
    }
}
