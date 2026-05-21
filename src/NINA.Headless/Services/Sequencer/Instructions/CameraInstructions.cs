using NINA.Image.ImageAnalysis;

namespace NINA.Headless.Services.Sequencer.Instructions;

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

    /// <summary>Filter name written to the FITS header (UI hint; doesn't move the wheel).</summary>
    public string? Filter { get; set; }

    /// <summary>Target name written to OBJECT keyword + image filename pattern.</summary>
    public string? TargetName { get; set; }

    /// <summary>FITS IMAGETYP — LIGHT / DARK / FLAT / BIAS.</summary>
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

        for (int i = 0; i < Count; i++) {
            ct.ThrowIfCancellationRequested();
            var image = await ctx.Equipment.Camera.CaptureAsync(ExposureSeconds, ct);

            image.MetaData.Exposure.ExposureTime = ExposureSeconds;
            if (!string.IsNullOrEmpty(Filter)) image.MetaData.Exposure.Filter = Filter;
            if (!string.IsNullOrEmpty(TargetName)) image.MetaData.Target.Name = TargetName;

            ctx.ImageWriter.SaveImage(image, targetName: TargetName, imageType: ImageType, gain: Gain ?? 0);

            if (ctx.LiveStack.IsRunning) {
                await ctx.LiveStack.AddFrameAsync(image, ct);
            } else {
                await ctx.Relay.RelayImageAsync(image, ct);
            }

            // Measure HFR + star count and stash in Scratch so the
            // AutoFocusOnHfrIncrease trigger has something to compare against.
            // Failures are non-fatal — a bad frame shouldn't kill the run.
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

            ctx.FramesCompleted++;
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
