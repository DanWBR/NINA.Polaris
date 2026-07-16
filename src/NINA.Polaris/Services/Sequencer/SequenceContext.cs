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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NINA.Polaris.Services.Sequencer;

/// <summary>
/// Runtime context handed to every entity's <see cref="ISequenceEntity.ExecuteAsync"/>.
/// Carries the services entities need (equipment, PHD2, plate solving, …)
/// plus a couple of run-scoped counters and a snapshot of the active rig's
/// settings.
///
/// Lives for the duration of one sequence run; the engine builds it from
/// the DI container before starting and disposes it on completion.
/// </summary>
public class SequenceContext {
    public EquipmentManager Equipment { get; }
    public ImageRelayService Relay { get; }
    public LiveStackingService LiveStack { get; }
    public PHD2Client PHD2 { get; }
    public AutoFocusService AutoFocus { get; }
    public MeridianFlipService MeridianFlip { get; }
    public PlateSolveService PlateSolver { get; }
    public SlewCenterService SlewCenter { get; }
    public ImageWriterService ImageWriter { get; }
    public ProfileService Profiles { get; }
    public CaptureProgressService CaptureProgress { get; }

    /// <summary>Walks the cooler setpoint at a controlled °C/min. Shared with the
    /// UI's cooler buttons so cooldown and warm-up behave identically wherever
    /// they're triggered from.</summary>
    public CoolingRampService CoolingRamp { get; }

    /// <summary>The active rig's ramp rate (°C/min), used when a cooler
    /// instruction doesn't override it. 0 = ramping off (write the setpoint once).</summary>
    public double CoolerRampDegPerMinute =>
        Profiles.ActiveEquipmentProfile?.CoolerRampDegPerMinute ?? 2.0;

    public ILogger Logger { get; }

    /// <summary>
    /// Per-run scratch space. Triggers use this to remember their last fired
    /// timestamp, the dither trigger uses it to count frames, etc. Keys are
    /// up to the entity (suggest "EntityType:EntityId:field").
    ///
    /// Concurrent so a <c>ParallelContainer</c> running children on multiple
    /// threads can read/write it without corrupting the bucket layout.
    /// </summary>
    public ConcurrentDictionary<string, object> Scratch { get; } = new();

    /// <summary>Wall-clock start of this sequence run (UTC).</summary>
    public DateTime RunStartedAt { get; }

    private int _framesCompleted;

    /// <summary>
    /// Counter incremented by <c>TakeExposureInstruction</c> after every
    /// successful frame. Read by Dither / Auto-focus / Center-after-drift
    /// triggers that fire every N frames. Read with a volatile load so a
    /// trigger evaluated on another thread (parallel container) sees the
    /// latest value; bump it via <see cref="IncrementFramesCompleted"/>.
    /// </summary>
    public int FramesCompleted {
        get => Volatile.Read(ref _framesCompleted);
        set => Volatile.Write(ref _framesCompleted, value);
    }

    /// <summary>Atomically increment the completed-frame counter.</summary>
    public int IncrementFramesCompleted() => Interlocked.Increment(ref _framesCompleted);

    /// <summary>
    /// True when this run RESUMES a previously interrupted run instead of
    /// starting fresh. Sequential containers then skip children whose status
    /// is still <see cref="SequenceEntityStatus.Completed"/> from the earlier
    /// run (first pass only), and <c>TakeExposureInstruction</c> continues
    /// from its retained frame counter. Set by the engine, never by entities.
    /// </summary>
    public bool IsResume { get; set; }

    /// <summary>
    /// Set by the engine when a <c>SafetyTrigger</c> raises a fatal
    /// condition; honoured by containers to abort the rest of the tree
    /// before falling out of the run.
    /// </summary>
    public bool AbortRequested { get; set; }

    /// <summary>Reason recorded with the abort, surfaced to the UI.</summary>
    public string? AbortReason { get; set; }

    public SequenceContext(
        EquipmentManager equipment,
        ImageRelayService relay,
        LiveStackingService liveStack,
        PHD2Client phd2,
        AutoFocusService autoFocus,
        MeridianFlipService meridianFlip,
        PlateSolveService plateSolver,
        SlewCenterService slewCenter,
        ImageWriterService imageWriter,
        ProfileService profiles,
        CaptureProgressService captureProgress,
        CoolingRampService coolingRamp,
        ILogger logger) {
        Equipment = equipment;
        Relay = relay;
        LiveStack = liveStack;
        PHD2 = phd2;
        AutoFocus = autoFocus;
        MeridianFlip = meridianFlip;
        PlateSolver = plateSolver;
        SlewCenter = slewCenter;
        ImageWriter = imageWriter;
        Profiles = profiles;
        CaptureProgress = captureProgress;
        CoolingRamp = coolingRamp;
        Logger = logger;
        RunStartedAt = DateTime.UtcNow;
    }
}