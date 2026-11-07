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
/// Ready-made sequences the ADV editor offers under "New from example", so a
/// first sequence starts from a working tree instead of an empty root.
///
/// The question that prompted them (Discord, 2026-11-07) was not "what can the
/// sequencer do" but "in what ORDER do the pieces go": where guiding starts,
/// where the meridian flip trigger sits, what ends a target. So every block
/// carries a description saying why it is where it is, and the examples share
/// the structure the PLAN compiler generates, which is the order the engine was
/// built around: prologue at the root, one DeepSkyObject container per target
/// (it slews and centres on its own, pausing and restarting guiding around the
/// slew), the per-target triggers on that container, epilogue at the root.
///
/// Built from the entity classes rather than shipped as JSON files so a
/// renamed property breaks the build instead of silently loading an example
/// with a field the engine ignores.
/// </summary>
public static class SequenceExamples {
    public sealed record Example(string Id, string Title, string Summary);

    // Placeholder targets, all within reach of both hemispheres. The
    // descriptions tell the operator to swap them for their own.
    private const double M42Ra = 5.5881, M42Dec = -5.3911;
    private const double M104Ra = 12.6664, M104Dec = -11.6231;

    public static IReadOnlyList<Example> All { get; } = new[] {
        new Example("one-target", "One target all night",
            "Cool, wait for dark, guide and focus, then image one target until it gets low. "
            + "Meridian flip, dither and refocus run on their own. Park at the end."),
        new Example("two-targets", "Two targets in one night",
            "The first target until it gets low, then wait for the second one to rise and image it. "
            + "Each target slews, centres and flips on its own."),
        new Example("lrgb", "LRGB with refocus on filter change",
            "Cycles L, R, G and B on one target, refocusing after every filter change, "
            + "until the target gets low."),
        new Example("darks-biases", "Darks and biases",
            "Cools to the imaging temperature and takes a dark and a bias library. "
            + "Cover the telescope first."),
    };

    public static SequenceDocument? Build(string id) => id switch {
        "one-target" => OneTarget(),
        "two-targets" => TwoTargets(),
        "lrgb" => Lrgb(),
        "darks-biases" => DarksAndBiases(),
        _ => null
    };

    private static SequenceDocument Doc(string id, SequentialContainer root) {
        var ex = All.First(e => e.Id == id);
        // The editor shows the selected block's description, and the root is
        // what is selected right after loading: it carries the summary too.
        root.Description ??= ex.Summary;
        return new SequenceDocument { Name = ex.Title, Description = ex.Summary, Root = root };
    }

    // ---- Shared blocks ----

    private static IEnumerable<ISequenceEntity> Prologue() => new ISequenceEntity[] {
        new UnparkMountInstruction {
            Name = "Unpark mount",
            Description = "First, so nothing else tries to move a parked mount."
        },
        new CoolCameraInstruction {
            Name = "Cool camera", TargetTempC = -10,
            Description = "Starts early so the sensor is at temperature by dark. "
                        + "Set it to the temperature your darks were taken at. "
                        + "On a camera without a cooler it fails and the run carries on.",
            ErrorBehavior = InstructionErrorBehavior.ContinueOnError
        },
        new WaitForSunBelowHorizonInstruction {
            Name = "Wait for dark", SunAltitudeDeg = -12,
            Description = "Holds the run until the Sun is 12° below the horizon (nautical twilight). "
                        + "Use -18 for full astronomical darkness."
        },
        new StartGuidingInstruction {
            Name = "Start guiding",
            Description = "Once, here. Each target block pauses guiding for its own slew and "
                        + "restarts it after centring, so there is no Start guiding inside a target."
        },
        new AutoFocusInstruction {
            Name = "Auto-focus",
            Description = "One focus run before the first target. The triggers on each target "
                        + "refocus later in the night."
        },
    };

    private static IEnumerable<ISequenceEntity> Epilogue() => new ISequenceEntity[] {
        new StopGuidingInstruction { Name = "Stop guiding" },
        new WarmCameraInstruction {
            Name = "Warm camera",
            Description = "Ramps the cooler back to ambient before switching it off.",
            ErrorBehavior = InstructionErrorBehavior.ContinueOnError
        },
        new ParkMountInstruction {
            Name = "Park mount",
            Description = "Runs when the targets finish. Stopping the run by hand skips this block."
        },
    };

    /// <summary>One target block: slews and centres on start, repeats its
    /// exposures until the target drops below <paramref name="minAlt"/>, and
    /// carries the triggers that keep a long run healthy.</summary>
    private static DeepSkyObjectContainer Target(string name, double ra, double dec, double minAlt) {
        var dso = new DeepSkyObjectContainer {
            Name = name, Target = name, RaHours = ra, DecDeg = dec, CenterOnStart = true,
            IsLoop = true,
            Description = $"Slews to {name}, plate-solves and centres, then repeats the exposures below "
                        + $"until {name} drops under {minAlt:0}°. Replace the name and coordinates with your "
                        + "own target. Triggers attached here apply to every exposure inside the block."
        };
        dso.Conditions.Add(new LoopUntilAltitudeCondition {
            Name = $"Until {name} is below {minAlt:0}°", RaHours = ra, DecDeg = dec, MinAltitudeDeg = minAlt,
            Description = "Ends this target. The coordinates must match the target's."
        });
        dso.Triggers.Add(new MeridianFlipTrigger {
            Name = "Meridian flip", RaHours = ra, DecDeg = dec,
            Description = "Flips when the target crosses the meridian: pauses guiding, flips, "
                        + "re-centres and resumes. Needs the same coordinates as the target."
        });
        dso.Triggers.Add(new DitherAfterNExposuresTrigger {
            Name = "Dither every 3", EveryNFrames = 3,
            Description = "Moves the pointing a few pixels between frames so stacking rejects hot pixels."
        });
        dso.Triggers.Add(new AutoFocusOnTempChangeTrigger {
            Name = "Refocus on 1 °C change", DeltaC = 1.0,
            Description = "Focus drifts as the tube cools during the night."
        });
        dso.Triggers.Add(new RestoreGuidingTrigger {
            Name = "Restore guiding",
            Description = "Restarts guiding if it stops mid-target (clouds, lost star)."
        });
        return dso;
    }

    private static TakeExposureInstruction Light(string target, double seconds, int count, string? filter = null) => new() {
        Name = filter == null ? $"{seconds:0} s light" : $"{filter} {seconds:0} s × {count}",
        ExposureSeconds = seconds, Count = count, Filter = filter, TargetName = target, ImageType = "LIGHT",
        Description = filter == null
            ? "One frame per pass; the block repeats it until the target gets low."
            : "The filter name must match a slot of your filter wheel."
    };

    // ---- Examples ----

    private static SequenceDocument OneTarget() {
        var root = new SequentialContainer { Name = "One target all night" };
        root.Items.AddRange(Prologue());
        var m42 = Target("M42", M42Ra, M42Dec, 30);
        m42.Items.Add(Light("M42", 120, 1));
        root.Items.Add(m42);
        root.Items.AddRange(Epilogue());
        return Doc("one-target", root);
    }

    private static SequenceDocument TwoTargets() {
        var root = new SequentialContainer { Name = "Two targets in one night" };
        root.Items.AddRange(Prologue());

        var first = Target("M42", M42Ra, M42Dec, 30);
        first.Items.Add(Light("M42", 120, 1));
        root.Items.Add(first);

        root.Items.Add(new WaitUntilAltitudeInstruction {
            Name = "Wait for M104 to rise", RaHours = M104Ra, DecDeg = M104Dec, MinAltitudeDeg = 30,
            Description = "Between the two target blocks: if the second target is not high enough yet "
                        + "when the first one ends, the run waits here instead of imaging through the horizon."
        });

        var second = Target("M104", M104Ra, M104Dec, 30);
        second.Items.Add(Light("M104", 180, 1));
        root.Items.Add(second);

        root.Items.AddRange(Epilogue());
        return Doc("two-targets", root);
    }

    private static SequenceDocument Lrgb() {
        var root = new SequentialContainer { Name = "LRGB with refocus on filter change" };
        root.Items.AddRange(Prologue());
        var m104 = Target("M104", M104Ra, M104Dec, 30);
        m104.Items.Add(Light("M104", 60, 6, "L"));
        m104.Items.Add(Light("M104", 120, 3, "R"));
        m104.Items.Add(Light("M104", 120, 3, "G"));
        m104.Items.Add(Light("M104", 120, 3, "B"));
        // Filter-change refocus replaces the temperature one: with four
        // filter swaps per pass it already refocuses often enough.
        m104.Triggers.RemoveAll(t => t is AutoFocusOnTempChangeTrigger);
        m104.Triggers.Add(new AutoFocusOnFilterChangeTrigger {
            Name = "Refocus on filter change",
            Description = "Each filter focuses slightly differently. With filter offsets set in RIGS "
                        + "you can drop this trigger and refocus on temperature instead."
        });
        root.Items.Add(m104);
        root.Items.AddRange(Epilogue());
        return Doc("lrgb", root);
    }

    private static SequenceDocument DarksAndBiases() {
        var root = new SequentialContainer {
            Name = "Darks and biases",
            Description = "Cover the telescope or remove the camera before starting."
        };
        root.Items.Add(new CoolCameraInstruction {
            Name = "Cool camera", TargetTempC = -10,
            Description = "The same temperature as the lights these darks will calibrate."
        });
        root.Items.Add(new TakeExposureInstruction {
            Name = "Darks 120 s × 20", ExposureSeconds = 120, Count = 20, ImageType = "DARK",
            Description = "Same exposure, gain and offset as the lights."
        });
        root.Items.Add(new TakeExposureInstruction {
            Name = "Biases × 30", ExposureSeconds = 0, Count = 30, ImageType = "BIAS",
            Description = "The shortest exposure the camera takes, same gain and offset as the lights."
        });
        root.Items.Add(new WarmCameraInstruction {
            Name = "Warm camera", ErrorBehavior = InstructionErrorBehavior.ContinueOnError
        });
        return Doc("darks-biases", root);
    }
}
