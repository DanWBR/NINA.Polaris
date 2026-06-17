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

// Data models / DTOs extracted from SequenceEngine.cs for readability.
// Plain serialisable types owned by SequenceEngine; no behaviour here.

namespace NINA.Polaris.Services;

public enum SequenceState { Idle, Running, Paused }

public class SequenceItem {
    public string Name { get; set; } = "";
    public double Exposure { get; set; } = 1.0;
    public int Gain { get; set; } = 100;
    public int Binning { get; set; } = 1;
    public int Count { get; set; } = 1;
    public string? Filter { get; set; }
    public double? Ra { get; set; }
    public double? Dec { get; set; }

    /// <summary>
    /// When false the item is kept in the schedule but skipped at run
    /// time (and excluded from the total-frame count / estimates). Lets
    /// the operator park an item without deleting it. Defaults true so
    /// older saved sequences (no flag) keep running.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Frame classification: LIGHT (default), DARK, BIAS, FLAT, DARKFLAT.
    /// ImageWriterService.BuildSubDir already routes each type to its
    /// own folder; the engine uses it to (a) tag the saved file, (b)
    /// skip slew/dither/meridian-flip for calibration items, and (c)
    /// force exposure=0 for BIAS regardless of what the UI sent.
    /// </summary>
    public string ImageType { get; set; } = "LIGHT";

    /// <summary>
    /// When true and <see cref="ImageType"/> is FLAT, the engine asks
    /// <see cref="FlatWizardService.AutoFindExposureAsync"/> to
    /// binary-search the right exposure for this (filter, binning)
    /// before capturing <see cref="Count"/> frames. Trained-exposure
    /// cache short-circuits the search on subsequent runs.
    ///
    /// Lets users without a filter wheel (the Flat Wizard sub-tab is
    /// filter-wheel-gated) still benefit from auto-exposure flats by
    /// dropping a single FLAT item in AUTORUN with Auto = on.
    ///
    /// Ignored for every other ImageType; <see cref="Exposure"/> wins
    /// when this flag is false.
    /// </summary>
    public bool AutoExposure { get; set; }
}

/// <summary>
/// Per-run actions executed once the sequence finishes (or is stopped,
/// if <see cref="RunOnStop"/> is true). All actions are best-effort:
/// a failure on one does not skip the rest, we log and move on.
/// </summary>
public class SequenceEndActions {
    public bool ParkMount { get; set; }
    public bool StopTracking { get; set; }
    public bool WarmCamera { get; set; }
    public bool DisconnectGuider { get; set; }
    /// <summary>If true, end-actions also fire when the user hits Stop. Default false.</summary>
    public bool RunOnStop { get; set; }

    /// <summary>
    /// Per-frame hook (not strictly an end-action, lives here so it
    /// shares the Autorun panel UI). When true and GraXpert is
    /// installed, every saved LIGHT frame is shipped to GraXpert for
    /// background-extraction in a fire-and-forget Task. Calibration
    /// frames are skipped. The next exposure does not wait on the
    /// ~10 s BGE pass, explicit performance > purity trade-off.
    /// </summary>
    public bool AutoGraXpert { get; set; }
}

public class SequenceStatus {
    public string State { get; set; } = "idle";
    public List<SequenceItemStatus> Items { get; set; } = [];
    public int CurrentItemIndex { get; set; }
    public int CurrentFrameInItem { get; set; }
    public int TotalFrames { get; set; }
    public int TotalFramesCompleted { get; set; }
    public double ElapsedSeconds { get; set; }
    public double EstimatedRemainingSeconds { get; set; }
    public string? LastError { get; set; }
    public int DithersIssued { get; set; }
    public int FramesSinceDither { get; set; }
    public DitherSettings? Dither { get; set; }
    public SequenceEndActions? EndActions { get; set; }
}

public class SequenceItemStatus {
    public string Name { get; set; } = "";
    public double Exposure { get; set; }
    public int Count { get; set; }
    public int Completed { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// Dithering configuration for a sequence run. The engine asks PHD2 to dither
/// after every <see cref="EveryNFrames"/> successfully-captured frames, and
/// waits for SettleDone before continuing.
/// </summary>
public class DitherSettings {
    public bool Enabled { get; set; }
    /// <summary>Random pixel offset (passed to PHD2 'dither' as amount).</summary>
    public double Pixels { get; set; } = 5.0;
    /// <summary>Trigger a dither after every N successfully-captured frames.</summary>
    public int EveryNFrames { get; set; } = 1;
    /// <summary>Only dither in RA (useful for mounts with sloppy Dec backlash).</summary>
    public bool RaOnly { get; set; }
    /// <summary>Settle distance tolerance in pixels.</summary>
    public double SettlePixels { get; set; } = 1.5;
    /// <summary>Minimum settled time in seconds.</summary>
    public int SettleTime { get; set; } = 10;
    /// <summary>Hard timeout for settling, in seconds.</summary>
    public int SettleTimeout { get; set; } = 40;
}