using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Planetary;
using NUnit.Framework;

namespace NINA.Polaris.Test.Planetary;

/// <summary>
/// Field harness, not a unit test: runs the planetary stacker on a real SER
/// named by POLARIS_SER and writes the stack next to it (or to
/// POLARIS_STACK_OUT). Explicit so the normal suite never touches the disk.
///   POLARIS_SER=E:\clip.ser POLARIS_KEEP=45 dotnet test --filter Name~PlanetaryStackerFieldRun
/// </summary>
[TestFixture]
public class PlanetaryStackerFieldRunTests {
    [Test, Explicit("needs POLARIS_SER pointing at a real recording")]
    public async Task StackTheClipNamedByEnvironment() {
        var ser = Environment.GetEnvironmentVariable("POLARIS_SER");
        Assume.That(ser, Is.Not.Null.And.Not.Empty, "POLARIS_SER not set");
        Assume.That(File.Exists(ser!), $"not found: {ser}");
        var outDir = Environment.GetEnvironmentVariable("POLARIS_STACK_OUT") ?? Path.GetDirectoryName(ser)!;
        double keep = double.TryParse(Environment.GetEnvironmentVariable("POLARIS_KEEP"), out var k) ? k : 45;
        var name = Environment.GetEnvironmentVariable("POLARIS_STACK_NAME")
                   ?? Path.GetFileNameWithoutExtension(ser) + "_polaris";

        var profiles = new ProfileService(new ConfigurationBuilder().Build(), NullLogger<ProfileService>.Instance);
        var svc = new PlanetaryStackerService(profiles, NullLogger<PlanetaryStackerService>.Instance);
        var sw = Stopwatch.StartNew();
        var job = svc.StartJob(new StackConfig(ser!, outDir, keep, name));
        await job.Task!;
        sw.Stop();

        TestContext.Out.WriteLine($"phase={job.Phase} error={job.Error}");
        TestContext.Out.WriteLine($"frames total={job.TotalFrames} picked={job.FramesPicked} aligned={job.FramesAligned} stacked={job.FramesStacked}");
        TestContext.Out.WriteLine($"output={job.OutputPath}");
        TestContext.Out.WriteLine($"elapsed={sw.Elapsed.TotalSeconds:0}s");
        Assert.That(job.Phase, Is.EqualTo(StackPhase.Ok), job.Error);
        Assert.That(File.Exists(job.OutputPath!));
    }
}
