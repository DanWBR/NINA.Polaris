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

using NINA.Image.ImageAnalysis;

namespace NINA.Polaris.Services.Sequencer.Instructions;

/// <summary>
/// Capture one or more exposures with the active camera, persist via
/// <see cref="ImageWriterService"/>, and feed the result into the live-stack
/// pipeline or the relay (mirrors the Simple Sequencer's frame loop).
/// Increments <see cref="SequenceContext.FramesCompleted"/> per successful frame.
/// </summary>
public class TakeExposureInstruction : SequenceInstruction {
    public override string Type => "TakeExposure";

    /// <summary>Exposure time in seconds.</summary>
    public double ExposureSeconds { get; set; } = 1.0;

    /// <summary>How many frames to capture in this instruction.</summary>
    public int Count { get; set; } = 1;

    public int? Gain { get; set; }
    public int? Offset { get; set; }
    public int Binning { get; set; } = 1;

    /// <summary>Filter to capture through. Written to the FITS header AND used
    /// to switch the wheel + apply the per-filter focuser offset (delta from the
    /// previous filter) before capturing — when a wheel is connected. No-ops if
    /// already on this filter or no wheel is present.</summary>
    public string? Filter { get; set; }

    /// <summary>Target name written to OBJECT keyword + image filename pattern.</summary>
    public string? TargetName { get; set; }

    /// <summary>FITS IMAGETYP, LIGHT / DARK / FLAT / BIAS.</summary>
    public string ImageType { get; set; } = "LIGHT";

    public override IReadOnlyList<string> Validate() {
        var e = new List<string>();
        if (ExposureSeconds <= 0) e.Add("Exposure must be positive");
        if (Count <= 0) e.Add("Count must be positive");
        if (Binning <= 0) e.Add("Binning must be positive");
        return e;
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (ctx.Equipment.Camera == null) throw new InvalidOperationException("No camera connected");

        if (Binning != 1) await ctx.Equipment.Camera.SetBinningAsync(Binning, Binning, ct);

        // Switch the filter wheel + apply its focuser offset (delta from the
        // previous filter) before capturing. The per-run FilterState lives in
        // Scratch so the delta is computed across instructions in this run.
        if (!string.IsNullOrWhiteSpace(Filter)) {
            var fs = (FilterState)ctx.Scratch.GetOrAdd("Filter:State", _ => new FilterState());
            await FilterSwitcher.ApplyAsync(
                ctx.Equipment.FilterWheel, ctx.Equipment.Focuser,
                ctx.Profiles.ActiveEquipmentProfile?.FilterOffsets,
                Filter, fs, ctx.Logger, ct);
        }

        // Build the per-exposure options once (mirrors the AUTORUN path) so the
        // driver actually receives gain / offset / binning / frame-type / filter
        // — previously the tree sequencer only set binning and captured with
        // defaults, so gain and the CCD_FRAME_TYPE tag were never applied.
        // Offset falls back to the rig's DefaultOffset (bias pedestal) when the
        // instruction doesn't pin one.
        var rigOffset = ctx.Profiles.ActiveEquipmentProfile?.DefaultOffset ?? 0;
        var capOpts = new NINA.Image.Interfaces.CaptureOptions(
            Gain: Gain,
            Offset: Offset ?? (rigOffset > 0 ? rigOffset : (int?)null),
            BinX: Binning > 0 ? Binning : (int?)null,
            BinY: Binning > 0 ? Binning : (int?)null,
            ImageType: string.IsNullOrWhiteSpace(ImageType) ? "LIGHT" : ImageType,
            Filter: string.IsNullOrEmpty(Filter) ? null : Filter,
            TargetName: string.IsNullOrEmpty(TargetName) ? null : TargetName);

        for (int i = 0; i < Count; i++) {
            ct.ThrowIfCancellationRequested();
            NINA.Image.Interfaces.IImageData image;
            using (ctx.CaptureProgress.Begin("sequencer", ExposureSeconds))
                image = await NINA.Polaris.Services.CameraCaptureGate.RunAsync(
                    () => ctx.Equipment.Camera.CaptureAsync(ExposureSeconds, capOpts, ct), ct);

            image.MetaData.Exposure.ExposureTime = ExposureSeconds;
            if (!string.IsNullOrEmpty(Filter)) image.MetaData.Exposure.Filter = Filter;
            if (!string.IsNullOrEmpty(TargetName)) image.MetaData.Target.Name = TargetName;

            ctx.ImageWriter.SaveImage(image, targetName: TargetName, imageType: ImageType, gain: Gain ?? 0);

            if (ctx.LiveStack.IsRunning) {
                await ctx.LiveStack.AddFrameAsync(image, ct);
            } else {
                // Sequence capture → AUTORUN preview canvas only, never LIVE.
                await ctx.Relay.RelayImageAsync(image, NINA.Polaris.Services.FrameKind.Autorun, ct);
            }

            // Measure HFR + star count and stash in Scratch so the
            // AutoFocusOnHfrIncrease trigger has something to compare against.
            // Failures are non-fatal, a bad frame shouldn't kill the run.
            try {
                var stars = new StarDetector().Detect(image.Data,
                    image.Properties.Width, image.Properties.Height);
                if (stars.Count > 0) {
                    var hfrs = stars.Select(s => s.HFR).OrderBy(h => h).ToArray();
                    var median = hfrs[hfrs.Length / 2];
                    ctx.Scratch["Frame:LastHfr"] = median;
                    ctx.Scratch["Frame:StarCount"] = stars.Count;
                    ctx.Logger.LogDebug("Frame HFR={Hfr:0.00} ({Count} stars)", median, stars.Count);
                }
            } catch (Exception ex) {
                ctx.Logger.LogDebug(ex, "Star detection failed on captured frame (continuing)");
            }

            ctx.IncrementFramesCompleted();
        }
    }
}

/// <summary>
/// Set the camera cooler setpoint and wait until the sensor is within
/// <see cref="ToleranceDegC"/> of <see cref="TargetTempC"/> or
/// <see cref="TimeoutSeconds"/> elapses.
/// </summary>
public class CoolCameraInstruction : SequenceInstruction {
    public override string Type => "CoolCamera";
    public double TargetTempC { get; set; } = -10;
    public double ToleranceDegC { get; set; } = 1.0;
    public int TimeoutSeconds { get; set; } = 600;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var cam = ctx.Equipment.Camera ?? throw new InvalidOperationException("No camera connected");
        await cam.SetCoolerAsync(true, ct);
        await cam.SetTemperatureAsync(TargetTempC, ct);

        var deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);
        while (DateTime.UtcNow < deadline) {
            ct.ThrowIfCancellationRequested();
            if (Math.Abs(cam.Temperature - TargetTempC) <= ToleranceDegC) {
                ctx.Logger.LogInformation("Cooler reached {Target}°C (now {Now:0.0}°C)", TargetTempC, cam.Temperature);
                return;
            }
            await Task.Delay(2000, ct);
        }
        throw new TimeoutException($"Cooler did not reach {TargetTempC}°C ±{ToleranceDegC} within {TimeoutSeconds}s (last reading {cam.Temperature:0.0}°C)");
    }
}

/// <summary>
/// Gradually ramp the cooler back to ambient, then power it off. Default
/// ramp is 2°C/min to protect the sensor from thermal shock.
/// </summary>
public class WarmCameraInstruction : SequenceInstruction {
    public override string Type => "WarmCamera";
    public double TargetTempC { get; set; } = 20;
    public double RateDegPerMinute { get; set; } = 2.0;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var cam = ctx.Equipment.Camera ?? throw new InvalidOperationException("No camera connected");
        var start = cam.Temperature;
        var stepC = Math.Max(0.5, RateDegPerMinute / 6); // 10-second steps
        var stepDelay = TimeSpan.FromSeconds(10);

        while (cam.Temperature < TargetTempC - 0.5) {
            ct.ThrowIfCancellationRequested();
            var next = Math.Min(TargetTempC, cam.Temperature + stepC);
            await cam.SetTemperatureAsync(next, ct);
            await Task.Delay(stepDelay, ct);
        }
        await cam.SetCoolerAsync(false, ct);
        ctx.Logger.LogInformation("Cooler ramped from {Start:0.0}°C to {Target}°C and powered off", start, TargetTempC);
    }
}

/// <summary>
/// Cool the AUX camera to a setpoint — same wait/tolerance loop as
/// <see cref="CoolCameraInstruction"/> but on <c>ctx.Equipment.AuxCamera</c>.
/// </summary>
public class CoolAuxCameraInstruction : SequenceInstruction {
    public override string Type => "CoolAuxCamera";
    public double TargetTempC { get; set; } = -10;
    public double ToleranceDegC { get; set; } = 1.0;
    public int TimeoutSeconds { get; set; } = 600;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var cam = ctx.Equipment.AuxCamera ?? throw new InvalidOperationException("No aux camera connected");
        await cam.SetCoolerAsync(true, ct);
        await cam.SetTemperatureAsync(TargetTempC, ct);

        var deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);
        while (DateTime.UtcNow < deadline) {
            ct.ThrowIfCancellationRequested();
            if (Math.Abs(cam.Temperature - TargetTempC) <= ToleranceDegC) {
                ctx.Logger.LogInformation("Aux cooler reached {Target}°C (now {Now:0.0}°C)", TargetTempC, cam.Temperature);
                return;
            }
            await Task.Delay(2000, ct);
        }
        throw new TimeoutException($"Aux cooler did not reach {TargetTempC}°C ±{ToleranceDegC} within {TimeoutSeconds}s (last reading {cam.Temperature:0.0}°C)");
    }
}

/// <summary>
/// Warm the AUX camera back to ambient and power off the cooler — same ramp as
/// <see cref="WarmCameraInstruction"/> but on <c>ctx.Equipment.AuxCamera</c>.
/// </summary>
public class WarmAuxCameraInstruction : SequenceInstruction {
    public override string Type => "WarmAuxCamera";
    public double TargetTempC { get; set; } = 20;
    public double RateDegPerMinute { get; set; } = 2.0;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var cam = ctx.Equipment.AuxCamera ?? throw new InvalidOperationException("No aux camera connected");
        var start = cam.Temperature;
        var stepC = Math.Max(0.5, RateDegPerMinute / 6); // 10-second steps

        while (cam.Temperature < TargetTempC - 0.5) {
            ct.ThrowIfCancellationRequested();
            var next = Math.Min(TargetTempC, cam.Temperature + stepC);
            await cam.SetTemperatureAsync(next, ct);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        await cam.SetCoolerAsync(false, ct);
        ctx.Logger.LogInformation("Aux cooler ramped from {Start:0.0}°C to {Target}°C and powered off", start, TargetTempC);
    }
}

/// <summary>
/// Capture one or more frames with the AUX camera and save them to the
/// <c>aux/</c> subtree, mirroring <see cref="AuxCaptureService"/>. Routes through
/// <see cref="AuxCameraCaptureGate"/> so it never collides with the background
/// aux loop, and applies the rig's aux focal-length override on the saved FITS.
/// Defaults (exposure / gain / binning) fall back to the active rig's aux
/// settings when the instruction leaves them unset.
/// </summary>
public class TakeAuxExposureInstruction : SequenceInstruction {
    public override string Type => "TakeAuxExposure";

    /// <summary>Exposure time in seconds. 0 ⇒ use the rig's aux exposure.</summary>
    public double ExposureSeconds { get; set; }
    /// <summary>How many aux frames to capture.</summary>
    public int Count { get; set; } = 1;
    /// <summary>Gain in native units; null ⇒ use the rig's aux gain.</summary>
    public int? Gain { get; set; }
    /// <summary>Binning; 0 ⇒ use the rig's aux binning.</summary>
    public int Binning { get; set; }

    public override IReadOnlyList<string> Validate() {
        var e = new List<string>();
        if (ExposureSeconds < 0) e.Add("Exposure must be >= 0");
        if (Count <= 0) e.Add("Count must be positive");
        if (Binning < 0) e.Add("Binning must be >= 0");
        return e;
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var cam = ctx.Equipment.AuxCamera ?? throw new InvalidOperationException("No aux camera connected");
        var rig = ctx.Profiles.ActiveEquipmentProfile;

        var expSec = ExposureSeconds > 0
            ? ExposureSeconds
            : Math.Max(0.05, (rig?.AuxExposureMs ?? 5000) / 1000.0);
        var bin = Math.Clamp(Binning > 0 ? Binning : (rig?.AuxBinning ?? 1), 1, 4);
        int? gain = Gain ?? (rig?.AuxGain is int g && g > 0 ? g : null);

        try { await cam.SetBinningAsync(bin, bin, ct); } catch { /* best effort */ }

        var opts = new NINA.Image.Interfaces.CaptureOptions(
            Gain: gain, BinX: bin, BinY: bin, ImageType: "LIGHT");

        for (int i = 0; i < Count; i++) {
            ct.ThrowIfCancellationRequested();
            NINA.Image.Interfaces.IImageData image;
            using (ctx.CaptureProgress.Begin("sequencer-aux", expSec))
                image = await AuxCameraCaptureGate.RunAsync(
                    () => cam.CaptureAsync(expSec, opts, ct), ct,
                    acquireTimeout: TimeSpan.FromSeconds(expSec + 60));

            image.MetaData.Exposure.ExposureTime = expSec;
            ctx.ImageWriter.SaveImage(image, imageType: "AUX",
                gain: gain ?? 0, focalLengthMmOverride: rig?.AuxFocalLengthMm);
            ctx.Logger.LogInformation("Aux frame {N}/{Count} captured ({Exp:0.##}s, gain {Gain}, bin {Bin})",
                i + 1, Count, expSec, gain?.ToString() ?? "default", bin);
        }
    }
}