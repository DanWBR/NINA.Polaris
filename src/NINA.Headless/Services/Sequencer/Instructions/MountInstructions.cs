namespace NINA.Headless.Services.Sequencer.Instructions;

/// <summary>Slew to absolute J2000 coordinates and wait for the slew to finish.</summary>
public class SlewToCoordinatesInstruction : SequenceInstruction {
    public override string Type => "SlewToCoordinates";
    /// <summary>Right ascension in decimal hours (J2000).</summary>
    public double RaHours { get; set; }
    /// <summary>Declination in decimal degrees (J2000).</summary>
    public double DecDeg { get; set; }

    public override IReadOnlyList<string> Validate() {
        var e = new List<string>();
        if (RaHours < 0 || RaHours >= 24) e.Add($"RA hours out of range: {RaHours}");
        if (DecDeg < -90 || DecDeg > 90) e.Add($"Dec out of range: {DecDeg}");
        return e;
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (ctx.Equipment.Telescope == null) throw new InvalidOperationException("No telescope connected");
        await ctx.Equipment.Telescope.SlewAsync(RaHours, DecDeg, ct);
        await MountUtil.WaitForSlewCompleteAsync(ctx, ct);
    }
}

/// <summary>Plate-solve based Slew &amp; Center on absolute coordinates.</summary>
public class CenterOnCoordinatesInstruction : SequenceInstruction {
    public override string Type => "CenterOnCoordinates";
    public double RaHours { get; set; }
    public double DecDeg { get; set; }
    public double ToleranceArcsec { get; set; } = 30;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        var job = ctx.SlewCenter.StartJob(RaHours, DecDeg, ToleranceArcsec);
        while (true) {
            ct.ThrowIfCancellationRequested();
            var status = ctx.SlewCenter.GetJob(job.Id);
            if (status == null) throw new InvalidOperationException("Slew & Center job vanished");
            if (status.State == SlewCenterState.Centered) return;
            if (status.State == SlewCenterState.Failed)
                throw new InvalidOperationException($"Center failed: {status.Error}");
            if (status.State == SlewCenterState.Cancelled)
                throw new OperationCanceledException("Center cancelled");
            await Task.Delay(500, ct);
        }
    }
}

public class ParkMountInstruction : SequenceInstruction {
    public override string Type => "ParkMount";
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (ctx.Equipment.Telescope == null) throw new InvalidOperationException("No telescope connected");
        await ctx.Equipment.Telescope.ParkAsync(ct);
    }
}

public class UnparkMountInstruction : SequenceInstruction {
    public override string Type => "UnparkMount";
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (ctx.Equipment.Telescope == null) throw new InvalidOperationException("No telescope connected");
        await ctx.Equipment.Telescope.UnparkAsync(ct);
    }
}

public class SetTrackingInstruction : SequenceInstruction {
    public override string Type => "SetTracking";
    public bool Enabled { get; set; } = true;
    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (ctx.Equipment.Telescope == null) throw new InvalidOperationException("No telescope connected");
        await ctx.Equipment.Telescope.SetTrackingAsync(Enabled, ct);
    }
}

/// <summary>Plate-solve the current pointing then Sync the mount to the solved coords.</summary>
public class SolveAndSyncInstruction : SequenceInstruction {
    public override string Type => "SolveAndSync";
    public double ExposureSeconds { get; set; } = 5;

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (ctx.Equipment.Telescope == null) throw new InvalidOperationException("No telescope connected");
        if (ctx.Equipment.Camera == null) throw new InvalidOperationException("No camera connected");

        var image = await ctx.Equipment.Camera.CaptureAsync(ExposureSeconds, ct);
        var tempFits = Path.Combine(Path.GetTempPath(), $"nina_solve_{Guid.NewGuid():N}.fits");
        NINA.Image.FileFormat.FITS.FITSWriter.Write(image, tempFits);
        try {
            var solve = await ctx.PlateSolver.SolveAsync(tempFits, new PlateSolveOptions(), ct);
            if (!solve.Success)
                throw new InvalidOperationException($"Plate solve failed: {solve.Error}");
            await ctx.Equipment.Telescope.SyncAsync(solve.RaHours, solve.DecDeg, ct);
        } finally {
            try { File.Delete(tempFits); } catch { }
        }
    }
}

internal static class MountUtil {
    /// <summary>Poll <c>IsSlewing</c> at 1 Hz for up to 5 minutes.</summary>
    public static async Task WaitForSlewCompleteAsync(SequenceContext ctx, CancellationToken ct) {
        if (ctx.Equipment.Telescope == null) return;
        for (int i = 0; i < 300; i++) {
            ct.ThrowIfCancellationRequested();
            if (!ctx.Equipment.Telescope.IsSlewing) return;
            await Task.Delay(1000, ct);
        }
        ctx.Logger.LogWarning("Slew did not complete within 5 minutes");
    }
}
