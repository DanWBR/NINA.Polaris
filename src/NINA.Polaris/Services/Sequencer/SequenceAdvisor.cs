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

using NINA.Polaris.Services.Sequencer.Conditions;
using NINA.Polaris.Services.Sequencer.Containers;
using NINA.Polaris.Services.Sequencer.Instructions;
using NINA.Polaris.Services.Sequencer.Triggers;

namespace NINA.Polaris.Services.Sequencer;

/// <summary>
/// Reviews a sequence for the things people forget, as opposed to the things
/// that are invalid. <see cref="ISequenceEntity.Validate"/> rejects a tree the
/// engine cannot run (a negative exposure) and blocks Start; this finds trees
/// that run fine and still lose the night: a flip trigger timed off another
/// target's coordinates, a target block with nothing to end it, a dither
/// cadence with guiding never started, a camera left cold, a mount left
/// tracking at dawn. It never blocks a run; the editor shows these as warnings
/// and asks once before starting.
///
/// Asked for on Discord (2026-11-07): "to get through the night on several
/// targets I'm afraid of forgetting an element of the palette or of setting it
/// up incorrectly."
///
/// The walk follows execution order (items depth-first, the way the engine
/// runs them), so "before" and "after" in the rules mean what they mean at
/// run time. Triggers apply to everything under their container, so a target
/// block sees its own triggers plus every ancestor's.
/// </summary>
public static class SequenceAdvisor {
    // Coordinates closer than this are the same target: about 1.5 arcmin in
    // RA at the equator, 0.1° in Dec. Copies typed by hand differ by rounding.
    private const double RaToleranceHours = 0.0015;
    private const double DecToleranceDeg = 0.1;

    private sealed class Walk {
        public readonly List<string> Warnings = new();
        public bool GuidingStarted;
        public bool Cold;              // a CoolCamera ran and no WarmCamera after it
        public bool Pointed;           // something slewed or centred
        public bool MountMoved;        // pointed since the last park / home
        public bool WarnedGuiding;
        public bool WarnedPointing;

        public void Add(string w) { if (!Warnings.Contains(w)) Warnings.Add(w); }
    }

    public static IReadOnlyList<string> Review(ISequenceEntity root) {
        var walk = new Walk();
        Visit(root, new List<SequenceContainer>(), walk);

        if (walk.Cold)
            walk.Add("The camera is cooled but never warmed up afterwards. Add Warm Camera at the end, "
                   + "so the cooler ramps down instead of staying on after the run.");
        if (walk.MountMoved)
            walk.Add("The sequence ends without parking: the mount keeps tracking after the last target. "
                   + "Add Park Mount (or Home Mount) at the end.");
        return walk.Warnings;
    }

    private static void Visit(ISequenceEntity e, List<SequenceContainer> ancestors, Walk walk) {
        switch (e) {
            case SequenceContainer c:
                VisitContainer(c, ancestors, walk);
                return;
            case StartGuidingInstruction:
                walk.GuidingStarted = true;
                return;
            case CoolCameraInstruction:
                walk.Cold = true;
                return;
            case WarmCameraInstruction:
                walk.Cold = false;
                return;
            case SlewToCoordinatesInstruction or CenterOnCoordinatesInstruction:
                walk.Pointed = walk.MountMoved = true;
                return;
            case ParkMountInstruction or HomeMountInstruction:
                walk.MountMoved = false;
                return;
            case DitherInstruction:
                if (!walk.GuidingStarted && !walk.WarnedGuiding) {
                    walk.WarnedGuiding = true;
                    walk.Add($"[{Label(e)}] Dither runs before any Start Guiding: a dither needs guiding "
                           + "running. Add Start Guiding near the top of the sequence.");
                }
                return;
            case TakeExposureInstruction x:
                VisitExposure(x, ancestors, walk);
                return;
        }
    }

    private static void VisitContainer(SequenceContainer c, List<SequenceContainer> ancestors, Walk walk) {
        if (c.IsLoop && c.Conditions.Count == 0)
            walk.Add($"[{Label(c)}] \"Repeat this block\" is on with no loop condition: it repeats until "
                   + "you stop the run, and nothing after it ever runs. Add a condition such as Loop Until "
                   + "Altitude or Loop Until Time.");
        if (!c.IsLoop && c.Conditions.Count > 0)
            walk.Add($"[{Label(c)}] Has a loop condition but \"Repeat this block\" is off, so the condition "
                   + "is ignored and the block runs once. Turn \"Repeat this block\" on.");

        if (c is DeepSkyObjectContainer dso) {
            CheckTarget(dso, ancestors, walk);
            if (dso.CenterOnStart) walk.Pointed = walk.MountMoved = true;
        }
        CheckStrayCoordinates(c, walk);

        ancestors.Add(c);
        foreach (var item in c.Items) Visit(item, ancestors, walk);
        ancestors.RemoveAt(ancestors.Count - 1);
    }

    private static void CheckTarget(DeepSkyObjectContainer dso, List<SequenceContainer> ancestors, Walk walk) {
        var name = Label(dso);
        if (dso.RaHours == 0 && dso.DecDeg == 0) {
            walk.Add($"[{name}] The target's coordinates are not set (RA 0h, Dec 0°). "
                   + "Fill RaHours and DecDeg with the target's J2000 position.");
            return;
        }

        bool Same(double ra, double dec) =>
            Math.Abs(WrapHours(ra - dso.RaHours)) <= RaToleranceHours && Math.Abs(dec - dso.DecDeg) <= DecToleranceDeg;

        // The flip and the re-centre time themselves off their own copy of the
        // coordinates. On this block, a differing copy is a target edited in one
        // place only; on an ancestor, it is one trigger shared by several targets.
        foreach (var t in dso.Triggers) {
            if (t is MeridianFlipTrigger f && !Same(f.RaHours, f.DecDeg))
                walk.Add($"[{name}] The Meridian Flip trigger uses RA {f.RaHours:0.###}h, Dec {f.DecDeg:0.##}°, "
                       + $"not the target's ({dso.RaHours:0.###}h, {dso.DecDeg:0.##}°). It would flip at the wrong "
                       + "time. Copy the target's coordinates into the trigger.");
            if (t is CenterAfterDriftTrigger r && !Same(r.RaHours, r.DecDeg))
                walk.Add($"[{name}] The Center After Drift trigger uses other coordinates than the target, "
                       + "so it would re-centre somewhere else. Copy the target's coordinates into it.");
        }
        foreach (var cond in dso.Conditions) {
            if (cond is LoopUntilAltitudeCondition a && !Same(a.RaHours, a.DecDeg))
                walk.Add($"[{name}] The Loop Until Altitude condition watches RA {a.RaHours:0.###}h, "
                       + $"Dec {a.DecDeg:0.##}°, not the target, so the target ends when that other point sets. "
                       + "Copy the target's coordinates into the condition.");
        }
        foreach (var anc in ancestors) {
            foreach (var f in anc.Triggers.OfType<MeridianFlipTrigger>()) {
                if (!Same(f.RaHours, f.DecDeg))
                    walk.Add($"[{name}] The Meridian Flip trigger on \"{Label(anc)}\" applies to this target too, "
                           + "but it is timed off other coordinates. Put one Meridian Flip trigger on each "
                           + "target block instead, with that target's coordinates.");
            }
        }

        // Lights under this target with no flip anywhere above them.
        bool hasFlip = dso.Triggers.OfType<MeridianFlipTrigger>().Any()
                    || ancestors.Any(a => a.Triggers.OfType<MeridianFlipTrigger>().Any());
        if (!hasFlip && HasLights(dso))
            walk.Add($"[{name}] No Meridian Flip trigger covers this target. On a German equatorial mount the "
                   + "run stops at the meridian. Add one to the target block (not needed on fork or alt-az mounts).");
    }

    // A flip, re-centre or altitude limit outside any target block with its
    // coordinates left at zero is an item dropped from the palette and never filled.
    private static void CheckStrayCoordinates(SequenceContainer c, Walk walk) {
        if (c is DeepSkyObjectContainer) return;
        foreach (var t in c.Triggers) {
            if (t is MeridianFlipTrigger { RaHours: 0, DecDeg: 0 }
                || t is CenterAfterDriftTrigger { RaHours: 0, DecDeg: 0 })
                walk.Add($"[{Label(c)}/{Label(t)}] Its coordinates are not set (RA 0h, Dec 0°). "
                       + "Move it onto a target block and copy the target's coordinates into it.");
        }
        foreach (var cond in c.Conditions) {
            if (cond is LoopUntilAltitudeCondition { RaHours: 0, DecDeg: 0 })
                walk.Add($"[{Label(c)}/{Label(cond)}] Its coordinates are not set (RA 0h, Dec 0°), so it "
                       + "watches a point that may never set or never rise.");
        }
    }

    private static void VisitExposure(TakeExposureInstruction x, List<SequenceContainer> ancestors, Walk walk) {
        bool light = string.IsNullOrWhiteSpace(x.ImageType)
                  || string.Equals(x.ImageType.Trim(), "LIGHT", StringComparison.OrdinalIgnoreCase);
        if (!light) return;

        if (!walk.Pointed && !walk.WarnedPointing) {
            walk.WarnedPointing = true;
            walk.Add($"[{Label(x)}] Light frames are taken before anything points the telescope. "
                   + "Put them inside a target block (DeepSkyObject), which slews and centres first.");
        }

        if (!walk.GuidingStarted && !walk.WarnedGuiding) {
            var needsGuiding = ancestors.SelectMany(a => a.Triggers)
                .FirstOrDefault(t => t is DitherAfterNExposuresTrigger or RestoreGuidingTrigger);
            if (needsGuiding != null) {
                walk.WarnedGuiding = true;
                walk.Add($"[{Label(needsGuiding)}] This trigger needs guiding running, but no Start Guiding "
                       + "comes before the exposures it watches. Add Start Guiding near the top of the sequence.");
            }
        }
    }

    private static bool HasLights(SequenceContainer c) => c.Items.Any(i => i switch {
        TakeExposureInstruction x => string.IsNullOrWhiteSpace(x.ImageType)
                                     || string.Equals(x.ImageType.Trim(), "LIGHT", StringComparison.OrdinalIgnoreCase),
        SequenceContainer inner => HasLights(inner),
        _ => false
    });

    private static double WrapHours(double d) {
        while (d > 12) d -= 24;
        while (d < -12) d += 24;
        return d;
    }

    private static string Label(ISequenceEntity e) =>
        string.IsNullOrWhiteSpace(e.Name) ? e.GetType().Name : e.Name;
}
