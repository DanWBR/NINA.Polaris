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

using System.Text.RegularExpressions;

namespace NINA.Polaris.Services;

/// <summary>
/// THERM: samples SoC temperature and CPU clock from Linux sysfs while a
/// benchmark's sustained CPU workload runs, so the resulting score can be read
/// against whether the board throttled.
///
/// Both a weak power supply and poor cooling depress a fixed-workload score the
/// same way — by pulling the CPU clock below its rated ceiling — so the raw
/// number alone can't tell them apart. This trace captures the two signals that
/// do: the hottest thermal zone (heat soak → cooling) and the sustained clock
/// vs the advertised max (a clock held down while the SoC stays cool is the
/// undervoltage / current-limit signature of a marginal PSU or cable).
///
/// Linux-only (reads <c>/sys/class/thermal</c> and the cpufreq sysfs). Off a
/// Linux host, or where those files aren't exposed, <see cref="Supported"/> is
/// false and <see cref="Stop"/> returns <c>Ran=false</c>.
/// </summary>
internal sealed class ThermalSampler {
    private readonly List<double> _temps = new();
    private readonly List<int> _clocksMhz = new();
    private readonly int _ratedMaxMhz;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private const int SampleIntervalMs = 250;

    // A sustained clock this far below the advertised ceiling means the governor
    // pulled it down under load — i.e. the board throttled.
    private const double ThrottleRatio = 0.90;
    // Above this the SoC is hot enough that the throttle is heat-driven; a
    // throttle while comfortably below it points at power/undervoltage instead.
    private const double HotTempC = 80.0;

    public bool Supported { get; }

    public ThermalSampler() {
        Supported = OperatingSystem.IsLinux() && Directory.Exists("/sys/class/thermal");
        _ratedMaxMhz = Supported ? ReadRatedMaxMhz() : 0;
    }

    /// <summary>Begin sampling on a background task. No-op when unsupported.</summary>
    public void Start() {
        if (!Supported) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () => {
            while (!ct.IsCancellationRequested) {
                var t = ReadMaxTempC();
                if (t is double tc) lock (_temps) _temps.Add(tc);
                var f = ReadMaxClockMhz();
                if (f is int fm) lock (_clocksMhz) _clocksMhz.Add(fm);
                try { await Task.Delay(SampleIntervalMs, ct); } catch { break; }
            }
        }, ct);
    }

    /// <summary>Stop sampling and fold the trace into a result.</summary>
    public ThermalResult Stop() {
        if (!Supported)
            return new ThermalResult(false, 0, 0, 0, 0, 0, 0, 0, false, null, 0,
                OperatingSystem.IsLinux()
                    ? "No thermal sensors exposed on this host."
                    : "Thermal trace is Linux-only.");

        try { _cts?.Cancel(); _loop?.Wait(1000); } catch { }
        finally { _cts?.Dispose(); _cts = null; }

        double[] temps;
        int[] clocks;
        lock (_temps) temps = _temps.ToArray();
        lock (_clocksMhz) clocks = _clocksMhz.ToArray();

        if (temps.Length == 0 && clocks.Length == 0)
            return new ThermalResult(false, 0, 0, 0, _ratedMaxMhz, 0, 0, 0, false, null, 0,
                "No thermal / clock samples were captured.");

        double startT = temps.Length > 0 ? temps[0] : 0;
        double maxT = temps.Length > 0 ? temps.Max() : 0;
        double endT = temps.Length > 0 ? temps[^1] : 0;

        int clkMin = clocks.Length > 0 ? clocks.Min() : 0;
        int clkMax = clocks.Length > 0 ? clocks.Max() : 0;
        int clkAvg = clocks.Length > 0 ? (int)Math.Round(clocks.Average()) : 0;

        bool throttled = _ratedMaxMhz > 0 && clkAvg > 0 && clkAvg < ThrottleRatio * _ratedMaxMhz;
        // Cause is only a hint: hot + throttled → cooling-bound; cool + throttled
        // → the undervoltage signature (a better PSU/cable is the fix).
        string? cause = null;
        if (throttled) cause = maxT >= HotTempC ? "thermal" : (maxT > 0 && maxT < HotTempC - 15 ? "power" : null);

        return new ThermalResult(
            Ran: true,
            StartTempC: Math.Round(startT, 1),
            MaxTempC: Math.Round(maxT, 1),
            EndTempC: Math.Round(endT, 1),
            RatedMaxMhz: _ratedMaxMhz,
            ClockMinMhz: clkMin,
            ClockAvgMhz: clkAvg,
            ClockMaxMhz: clkMax,
            Throttled: throttled,
            Cause: cause,
            Samples: Math.Max(temps.Length, clocks.Length),
            Error: null);
    }

    /// <summary>Hottest thermal zone in °C, or null when none are readable.
    /// Ignores obviously bogus readings (≤0 or &gt;150°C) some boards report on
    /// unused zones.</summary>
    private static double? ReadMaxTempC() {
        double best = double.NaN;
        try {
            foreach (var zone in Directory.EnumerateDirectories("/sys/class/thermal", "thermal_zone*")) {
                var milli = ReadInt(Path.Combine(zone, "temp"));
                if (milli is not int mv) continue;
                double c = mv / 1000.0;
                if (c <= 0 || c > 150) continue;
                if (double.IsNaN(best) || c > best) best = c;
            }
        } catch { /* sysfs not readable */ }
        return double.IsNaN(best) ? null : best;
    }

    /// <summary>Fastest running core's current clock in MHz (the ceiling the SoC
    /// is willing to sustain under load), or null when cpufreq isn't exposed.</summary>
    private static int? ReadMaxClockMhz() {
        int best = 0;
        try {
            foreach (var cpu in EnumerateCpuDirs()) {
                var kHz = ReadInt(Path.Combine(cpu, "cpufreq", "scaling_cur_freq"));
                if (kHz is int k && k > 0) best = Math.Max(best, k / 1000);
            }
        } catch { /* sysfs not readable */ }
        return best > 0 ? best : null;
    }

    /// <summary>Advertised max clock in MHz = the highest
    /// <c>cpuinfo_max_freq</c> across all cores (the big cluster's ceiling on a
    /// big.LITTLE SoC). 0 when unavailable.</summary>
    private static int ReadRatedMaxMhz() {
        int best = 0;
        try {
            foreach (var cpu in EnumerateCpuDirs()) {
                var kHz = ReadInt(Path.Combine(cpu, "cpufreq", "cpuinfo_max_freq"));
                if (kHz is int k && k > 0) best = Math.Max(best, k / 1000);
            }
        } catch { /* sysfs not readable */ }
        return best;
    }

    private static IEnumerable<string> EnumerateCpuDirs() {
        // Match cpu0, cpu1, ... but not cpufreq / cpuidle.
        foreach (var d in Directory.EnumerateDirectories("/sys/devices/system/cpu", "cpu*")) {
            var name = Path.GetFileName(d);
            if (Regex.IsMatch(name, @"^cpu\d+$")) yield return d;
        }
    }

    private static int? ReadInt(string path) {
        try {
            if (!File.Exists(path)) return null;
            var s = File.ReadAllText(path).Trim();
            return int.TryParse(s, out var v) ? v : null;
        } catch { return null; }
    }
}
