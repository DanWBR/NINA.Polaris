using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using NINA.Image.Interfaces;

namespace NINA.Headless.Services;

public class SlewCenterService {
    private readonly EquipmentManager _equip;
    private readonly PlateSolveService _solver;
    private readonly ILogger<SlewCenterService> _logger;

    private readonly ConcurrentDictionary<string, SlewCenterJob> _jobs = new();

    public SlewCenterService(EquipmentManager equip, PlateSolveService solver,
        ILogger<SlewCenterService> logger) {
        _equip = equip;
        _solver = solver;
        _logger = logger;
    }

    public SlewCenterJob StartJob(double ra, double dec, double toleranceArcsec = 30) {
        var job = new SlewCenterJob {
            Id = Guid.NewGuid().ToString("N"),
            TargetRa = ra,
            TargetDec = dec,
            ToleranceArcsec = toleranceArcsec,
            State = SlewCenterState.Pending,
            CreatedAt = DateTime.UtcNow
        };

        _jobs[job.Id] = job;

        job.Cts = new CancellationTokenSource();
        job.Task = Task.Run(() => RunJobAsync(job, job.Cts.Token));

        return job;
    }

    public SlewCenterJob? GetJob(string jobId) {
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public void CancelJob(string jobId) {
        if (_jobs.TryGetValue(jobId, out var job)) {
            job.Cts?.Cancel();
            job.State = SlewCenterState.Cancelled;
        }
    }

    private async Task RunJobAsync(SlewCenterJob job, CancellationToken ct) {
        const int maxIterations = 5;
        const double solveExposure = 5.0;

        try {
            if (_equip.Telescope == null) {
                job.Error = "No telescope connected";
                job.State = SlewCenterState.Failed;
                return;
            }

            if (_equip.Camera == null) {
                job.Error = "No camera connected for plate solving";
                job.State = SlewCenterState.Failed;
                return;
            }

            if (!_solver.IsAvailable) {
                job.Error = "Plate solver (ASTAP) not available";
                job.State = SlewCenterState.Failed;
                return;
            }

            for (int i = 0; i < maxIterations; i++) {
                ct.ThrowIfCancellationRequested();
                job.Iteration = i + 1;

                // Step 1: Slew
                job.State = SlewCenterState.Slewing;
                _logger.LogInformation("Slew-and-center iteration {I}: slewing to RA={Ra:F4} Dec={Dec:F4}",
                    i + 1, job.TargetRa, job.TargetDec);

                await _equip.Telescope.SlewAsync(job.TargetRa, job.TargetDec, ct);
                await WaitForSlewComplete(ct);

                ct.ThrowIfCancellationRequested();

                // Step 2: Capture short exposure for plate solving
                job.State = SlewCenterState.Capturing;
                _logger.LogInformation("Capturing {Exp}s solve frame", solveExposure);

                var imageData = await _equip.Camera.CaptureAsync(solveExposure, ct);

                var tempFits = Path.Combine(Path.GetTempPath(),
                    $"nina_solve_{job.Id}_{i}.fits");

                WriteMinimalFits(imageData, tempFits);

                ct.ThrowIfCancellationRequested();

                // Step 3: Plate solve
                job.State = SlewCenterState.Solving;
                _logger.LogInformation("Plate solving...");

                var solveResult = await _solver.SolveAsync(tempFits, new PlateSolveOptions {
                    HintRa = job.TargetRa,
                    HintDec = job.TargetDec,
                    SearchRadiusDeg = 10
                }, ct);

                try { File.Delete(tempFits); } catch { }

                if (!solveResult.Success) {
                    _logger.LogWarning("Plate solve failed on iteration {I}: {Error}",
                        i + 1, solveResult.Error);
                    job.Error = "Solve failed: " + solveResult.Error;

                    if (i == maxIterations - 1) {
                        job.State = SlewCenterState.Failed;
                        return;
                    }
                    continue;
                }

                // Step 4: Calculate error
                var errorArcsec = AngularSeparationArcsec(
                    job.TargetRa, job.TargetDec,
                    solveResult.RaHours, solveResult.DecDeg);

                job.ActualRa = solveResult.RaHours;
                job.ActualDec = solveResult.DecDeg;
                job.ErrorArcsec = errorArcsec;
                job.Rotation = solveResult.RotationDeg;
                job.Scale = solveResult.ScaleArcsecPerPixel;

                _logger.LogInformation(
                    "Solve result: RA={Ra:F4}h Dec={Dec:F4}°, error={Err:F1}\" (tolerance={Tol:F0}\")",
                    solveResult.RaHours, solveResult.DecDeg, errorArcsec, job.ToleranceArcsec);

                // Step 5: Check convergence
                if (errorArcsec <= job.ToleranceArcsec) {
                    job.State = SlewCenterState.Centered;
                    _logger.LogInformation("Centered! Error {Err:F1}\" within tolerance {Tol:F0}\"",
                        errorArcsec, job.ToleranceArcsec);
                    return;
                }

                // Step 6: Sync mount and prepare for next iteration
                job.State = SlewCenterState.Syncing;
                _logger.LogInformation("Syncing mount at RA={Ra:F4} Dec={Dec:F4}",
                    solveResult.RaHours, solveResult.DecDeg);

                await _equip.Telescope.SyncAsync(solveResult.RaHours, solveResult.DecDeg, ct);
            }

            job.State = SlewCenterState.Failed;
            job.Error = $"Did not converge after {maxIterations} iterations (last error: {job.ErrorArcsec:F1}\")";

        } catch (OperationCanceledException) {
            job.State = SlewCenterState.Cancelled;
            _logger.LogInformation("Slew-and-center cancelled");
        } catch (Exception ex) {
            job.State = SlewCenterState.Failed;
            job.Error = ex.Message;
            _logger.LogError(ex, "Slew-and-center failed");
        }
    }

    private async Task WaitForSlewComplete(CancellationToken ct) {
        if (_equip.Telescope == null) return;
        for (int i = 0; i < 300; i++) {
            ct.ThrowIfCancellationRequested();
            if (!_equip.Telescope.IsSlewing) return;
            await Task.Delay(1000, ct);
        }
        _logger.LogWarning("Slew did not complete within 5 minutes");
    }

    private static void WriteMinimalFits(IImageData imageData, string path) {
        var w = imageData.Properties.Width;
        var h = imageData.Properties.Height;
        var pixels = imageData.Data;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);

        var headers = new List<string> {
            FitsCard("SIMPLE", "T"),
            FitsCard("BITPIX", "16"),
            FitsCard("NAXIS", "2"),
            FitsCard("NAXIS1", w.ToString()),
            FitsCard("NAXIS2", h.ToString()),
            FitsCard("BZERO", "32768"),
            FitsCard("BSCALE", "1"),
            "END" + new string(' ', 77)
        };

        while (headers.Count % 36 != 0)
            headers.Add(new string(' ', 80));

        var headerBytes = Encoding.ASCII.GetBytes(string.Concat(headers));
        fs.Write(headerBytes);

        var buf = new byte[2];
        foreach (var px in pixels) {
            short signed = (short)(px - 32768);
            BinaryPrimitives.WriteInt16BigEndian(buf, signed);
            fs.Write(buf);
        }

        var dataLen = pixels.Length * 2;
        var pad = (2880 - (dataLen % 2880)) % 2880;
        if (pad > 0) fs.Write(new byte[pad]);
    }

    private static string FitsCard(string key, string value) {
        var card = $"{key,-8}= {value,20}";
        return card.PadRight(80);
    }

    private static double AngularSeparationArcsec(double ra1Hours, double dec1Deg,
        double ra2Hours, double dec2Deg) {
        var ra1Rad = ra1Hours * 15.0 * Math.PI / 180.0;
        var dec1Rad = dec1Deg * Math.PI / 180.0;
        var ra2Rad = ra2Hours * 15.0 * Math.PI / 180.0;
        var dec2Rad = dec2Deg * Math.PI / 180.0;

        var cosSep = Math.Sin(dec1Rad) * Math.Sin(dec2Rad) +
                     Math.Cos(dec1Rad) * Math.Cos(dec2Rad) * Math.Cos(ra1Rad - ra2Rad);

        cosSep = Math.Clamp(cosSep, -1.0, 1.0);
        var sepRad = Math.Acos(cosSep);

        return sepRad * 180.0 / Math.PI * 3600.0;
    }
}

public class SlewCenterJob {
    public string Id { get; set; } = "";
    public double TargetRa { get; set; }
    public double TargetDec { get; set; }
    public double ToleranceArcsec { get; set; }
    public SlewCenterState State { get; set; }
    public int Iteration { get; set; }
    public double? ActualRa { get; set; }
    public double? ActualDec { get; set; }
    public double? ErrorArcsec { get; set; }
    public double? Rotation { get; set; }
    public double? Scale { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }

    internal CancellationTokenSource? Cts { get; set; }
    internal Task? Task { get; set; }
}

public enum SlewCenterState {
    Pending,
    Slewing,
    Capturing,
    Solving,
    Syncing,
    Centered,
    Failed,
    Cancelled
}
