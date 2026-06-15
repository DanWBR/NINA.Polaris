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

using NINA.Polaris.Services.Sequencer;
using NINA.Polaris.Services.Sequencer.Conditions;
using NINA.Polaris.Services.Sequencer.Containers;
using NINA.Polaris.Services.Sequencer.Instructions;
using NINA.Polaris.Services.Sequencer.Triggers;

namespace NINA.Polaris.Services.Plan;

/// <summary>
/// Lowers a declarative <see cref="ImagingPlan"/> into an Advanced-Sequencer
/// <see cref="SequenceDocument"/> the existing <c>AdvancedSequenceEngine</c>
/// runs unchanged. The shape is:
///
/// <code>
///   SequentialContainer (plan name)
///     [WaitUntilTime]        when StartMode = AtTime
///     [CoolCamera]           when AutoCooling
///     [StartGuiding]         when AutoGuiding
///     [AutoFocus]            when AutoFocusOnStart
///     foreach enabled target:
///       DeepSkyObjectContainer (slew + plate-solve-center)
///         [WaitForTime]      when FirstDelaySec > 0
///         TakeExposure × frame-rows
///         (trigger) MeridianFlip   when AutoMeridianFlip
/// </code>
///
/// The end-of-session actions (stop guiding, warm + cooler off, park, focuser →
/// 0) are compiled into a SEPARATE document by <see cref="CompileEndActions"/>.
/// <see cref="PlanRunnerService"/> runs them after the main run ends — including
/// when the plan was stopped at dawn / a set time, where a hard engine stop
/// would otherwise skip an in-document epilogue. Host shutdown
/// (<see cref="ImagingPlan.EndShutdownHost"/>) is performed by the runner after
/// the end-actions document completes.
/// </summary>
public class PlanCompilerService {
    // Rough per-target overhead used only for the session-time estimate.
    private const int SlewOverheadSeconds = 30;
    private const int PlateSolveSeconds = 20;
    private const int PerFrameOverheadSeconds = 5;

    public SequenceDocument Compile(ImagingPlan plan) {
        var root = new SequentialContainer { Name = string.IsNullOrWhiteSpace(plan.Name) ? "Plan" : plan.Name };

        // ---- Prologue ----
        if (plan.StartMode == PlanStartMode.AtTime) {
            root.Items.Add(new WaitUntilTimeInstruction {
                Name = "Wait for start time",
                TimeOfDayUtc = plan.StartAtUtc
            });
        }
        if (plan.AutoCooling) {
            root.Items.Add(new CoolCameraInstruction {
                Name = "Cool camera",
                TargetTempC = plan.CoolTargetC
            });
        }
        if (plan.AutoGuiding) {
            root.Items.Add(new StartGuidingInstruction { Name = "Start guiding" });
        }
        if (plan.AutoFocusOnStart) {
            root.Items.Add(new AutoFocusInstruction { Name = "Auto-focus" });
        }

        // ---- Targets ----
        foreach (var t in plan.Targets) {
            if (!t.Enabled) continue;
            bool timed = t.ScheduleMode == PlanScheduleMode.TimeWindow;

            // Per-target start gate (time-window mode): wait until the start
            // time, but go now if it's already passed. Runs once, at root level,
            // before the container's slew so it doesn't repeat per loop pass.
            if (timed && !string.IsNullOrWhiteSpace(t.StartAtUtc)) {
                root.Items.Add(new WaitUntilTimeInstruction {
                    Name = $"Wait for {t.Name}", TimeOfDayUtc = t.StartAtUtc, SkipIfPast = true
                });
            }
            // Per-target auto-focus: run once before the target starts.
            if (plan.AutoFocusEachTarget) {
                root.Items.Add(new AutoFocusInstruction { Name = $"Auto-focus ({t.Name})" });
            }

            var dso = new DeepSkyObjectContainer {
                Name = t.Name,
                Target = t.Name,
                RaHours = t.RaHours,
                DecDeg = t.DecDeg,
                Rotation = t.Rotation,
                CenterOnStart = true
            };
            if (t.FirstDelaySec > 0) {
                dso.Items.Add(new WaitForTimeInstruction {
                    Name = "First delay", Seconds = t.FirstDelaySec
                });
            }
            foreach (var f in t.Frames) {
                dso.Items.Add(new TakeExposureInstruction {
                    Name = $"{f.ImageType} × {f.Count}",
                    ExposureSeconds = f.ExposureSeconds,
                    Count = f.Count,
                    Filter = f.Filter,
                    Gain = f.Gain,
                    Binning = f.Binning <= 0 ? 1 : f.Binning,
                    TargetName = t.Name,
                    ImageType = string.IsNullOrWhiteSpace(f.ImageType) ? "LIGHT" : f.ImageType
                });
            }
            if (plan.AutoMeridianFlip) {
                dso.Triggers.Add(new MeridianFlipTrigger {
                    Name = "Meridian flip", RaHours = t.RaHours, DecDeg = t.DecDeg
                });
            }
            // Per-target periodic maintenance (0 = off).
            if (t.RecenterEveryNFrames > 0) {
                dso.Triggers.Add(new CenterAfterDriftTrigger {
                    Name = $"Re-center every {t.RecenterEveryNFrames}",
                    RaHours = t.RaHours, DecDeg = t.DecDeg,
                    CheckEveryNFrames = t.RecenterEveryNFrames
                });
            }
            if (t.RefocusEveryNFrames > 0) {
                dso.Triggers.Add(new AutoFocusEveryNFramesTrigger {
                    Name = $"Re-focus every {t.RefocusEveryNFrames}",
                    EveryNFrames = t.RefocusEveryNFrames
                });
            }
            if (t.DitherEveryNFrames > 0) {
                dso.Triggers.Add(new DitherAfterNExposuresTrigger {
                    Name = $"Dither every {t.DitherEveryNFrames}",
                    EveryNFrames = t.DitherEveryNFrames
                });
            }
            // Time-window mode: loop the frame block (slew/center happens once in
            // the container preamble) until the target's end time is reached.
            if (timed && !string.IsNullOrWhiteSpace(t.EndAtUtc)) {
                dso.IsLoop = true;
                dso.Conditions.Add(new LoopUntilTimeCondition {
                    Name = $"Until {t.EndAtUtc}", TimeOfDayUtc = t.EndAtUtc
                });
            }
            root.Items.Add(dso);
        }

        return new SequenceDocument {
            Name = root.Name,
            Description = $"Plan '{root.Name}': {plan.Targets.Count(t => t.Enabled)} target(s)",
            Root = root
        };
    }

    /// <summary>
    /// Build the end-of-session actions as a standalone document, run by the
    /// runner after the main run ends. Returns null when the plan selected no
    /// end actions (and isn't guiding), so the runner can skip straight to
    /// shutdown / cleanup. Host shutdown is NOT included here — the runner does
    /// it after this document completes.
    /// </summary>
    public SequenceDocument? CompileEndActions(ImagingPlan plan) {
        var root = new SequentialContainer { Name = "End of session" };
        if (plan.AutoGuiding) root.Items.Add(new StopGuidingInstruction { Name = "Stop guiding" });
        if (plan.EndWarmCoolerOff) root.Items.Add(new WarmCameraInstruction { Name = "Warm camera + cooler off" });
        if (plan.EndGoHome) root.Items.Add(new ParkMountInstruction { Name = "Park mount" });
        if (plan.EndEafZero) root.Items.Add(new MoveFocuserInstruction { Name = "Focuser → 0", Position = 0 });

        if (root.Items.Count == 0) return null;
        return new SequenceDocument {
            Name = root.Name,
            Description = $"End-of-session actions for plan '{plan.Name}'",
            Root = root
        };
    }

    /// <summary>
    /// Rough session-time estimate in seconds (sum of per-target slew + solve +
    /// first-delay + exposures × (exposure + readout overhead)). Optimistic; it
    /// ignores meridian-flip / auto-focus interruptions.
    /// </summary>
    public double EstimateSeconds(ImagingPlan plan) {
        double total = 0;
        foreach (var t in plan.Targets) {
            if (!t.Enabled) continue;
            total += SlewOverheadSeconds + PlateSolveSeconds + Math.Max(0, t.FirstDelaySec);
            if (t.ScheduleMode == PlanScheduleMode.TimeWindow) {
                // Time-bounded: the contribution is the window length itself.
                total += WindowSeconds(t.StartAtUtc, t.EndAtUtc);
            } else {
                foreach (var f in t.Frames) {
                    total += Math.Max(0, f.Count) * (Math.Max(0, f.ExposureSeconds) + PerFrameOverheadSeconds);
                }
            }
        }
        return total;
    }

    /// <summary>Length of a "HH:mm"→"HH:mm" UTC window in seconds, wrapping past
    /// midnight (end before start = next day). Returns 0 on bad/empty input.</summary>
    private static double WindowSeconds(string start, string end) {
        if (!TimeSpan.TryParse(start, out var s) || !TimeSpan.TryParse(end, out var e)) return 0;
        var d = (e - s).TotalSeconds;
        if (d <= 0) d += 24 * 3600;
        return d;
    }
}
