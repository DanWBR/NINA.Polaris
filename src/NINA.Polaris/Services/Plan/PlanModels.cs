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

using System.Text.Json.Serialization;

namespace NINA.Polaris.Services.Plan;

/// <summary>
/// ASIAIR-style "PLAN" mode model: a saved, multi-target imaging plan. Each
/// plan queues several <see cref="PlanTarget"/>s, each with its own frame list
/// (autorun), executed in order with automatic slew + plate-solve-center. The
/// plan carries a scheduled start, an end condition, plan-wide automation
/// toggles, and end-of-session actions.
///
/// A plan is purely declarative data. At run time
/// <see cref="PlanCompilerService"/> lowers it into an Advanced-Sequencer
/// <c>SequenceDocument</c> that runs on the existing engine; the
/// <see cref="PlanRunnerService"/> schedules the start and enforces the end
/// condition. Plans live in the global user profile so they're a reusable
/// library, runnable with whatever rig is active.
/// </summary>
public class ImagingPlan {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New plan";

    // ---- Start / end scheduling --------------------------------------
    /// <summary>"Now" = begin when Start is pressed; "AtTime" = wait for <see cref="StartAtUtc"/>.</summary>
    public PlanStartMode StartMode { get; set; } = PlanStartMode.Now;
    /// <summary>UTC time-of-day "HH:mm" the plan begins when StartMode=AtTime (next occurrence).</summary>
    public string StartAtUtc { get; set; } = "21:00";

    /// <summary>"AllDone" = stop when every target finishes; "Dawn" = stop at astronomical
    /// dawn; "AtTime" = stop at <see cref="EndAtUtc"/>.</summary>
    public PlanEndMode EndMode { get; set; } = PlanEndMode.AllDone;
    /// <summary>UTC time-of-day "HH:mm" the plan stops when EndMode=AtTime (next occurrence).</summary>
    public string EndAtUtc { get; set; } = "05:00";

    // ---- Plan-wide automation toggles --------------------------------
    public bool AutoGuiding { get; set; } = true;
    public bool AutoMeridianFlip { get; set; } = true;
    public bool AutoCooling { get; set; } = false;
    public double CoolTargetC { get; set; } = -10;
    /// <summary>Run an auto-focus pass once at the start of the plan (needs a focuser).</summary>
    public bool AutoFocusOnStart { get; set; } = false;

    // ---- End-of-session actions --------------------------------------
    public bool EndWarmCoolerOff { get; set; } = false;
    public bool EndGoHome { get; set; } = false;
    public bool EndEafZero { get; set; } = false;
    /// <summary>Power the host off after the plan ends. Gated behind an explicit
    /// confirm in the UI; executed by the runner, never inside the sequence doc.</summary>
    public bool EndShutdownHost { get; set; } = false;

    public List<PlanTarget> Targets { get; set; } = new();
}

/// <summary>One imaging target inside a plan: a pointing + its frame list.</summary>
public class PlanTarget {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Target";

    /// <summary>J2000 right ascension in decimal hours.</summary>
    public double RaHours { get; set; }
    /// <summary>J2000 declination in decimal degrees.</summary>
    public double DecDeg { get; set; }
    /// <summary>Target rotation / position angle in degrees (record-keeping until a rotator move lands).</summary>
    public double Rotation { get; set; }

    /// <summary>Seconds to wait after centering before the first exposure (settle / let guiding lock).</summary>
    public int FirstDelaySec { get; set; } = 0;

    /// <summary>Disabled targets are kept in the list but skipped when the plan runs.</summary>
    public bool Enabled { get; set; } = true;

    public List<PlanFrame> Frames { get; set; } = new();
}

/// <summary>One row of a target's frame list — N exposures at a given exposure/gain/filter.</summary>
public class PlanFrame {
    public double ExposureSeconds { get; set; } = 60;
    public int Count { get; set; } = 10;
    public string? Filter { get; set; }
    public int? Gain { get; set; }
    public int Binning { get; set; } = 1;
    public string ImageType { get; set; } = "LIGHT";
}

// Serialize as strings ("Now", "AllDone", …) — the SPA sends/reads these as
// strings, and the API uses the default System.Text.Json options (which map
// enums to integers without this attribute, breaking model binding).
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanStartMode { Now, AtTime }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanEndMode { AllDone, Dawn, AtTime }
