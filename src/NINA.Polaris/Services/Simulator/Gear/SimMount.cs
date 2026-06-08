// Part of the built-in gear simulator (ST4 maths in SimGearState are ported
// from PHD2 under BSD-3-Clause). This ITelescope adapter routes guide pulses
// into the shared SimGearState and offers a minimal-but-complete mount so the
// simulator can also drive slew/sync/track/park flows.

using NINA.Core.Enum;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services.Simulator.Gear;

/// <summary>Simulated GEM mount backed by the shared <see cref="SimGearState"/>.</summary>
public sealed class SimMount : ITelescope {
    private readonly SimGearService _gear;
    private volatile bool _connected;

    public SimMount(SimGearService gear) {
        _gear = gear;
    }

    public string DeviceName => "Simulator";
    public bool IsConnected => _connected;

    public double RightAscension => _gear.State.RightAscensionHours;
    public double Declination => _gear.State.DeclinationDeg;
    public double Altitude => double.NaN; // status broadcaster recomputes from RA/Dec + site
    public double Azimuth => double.NaN;

    public bool IsTracking => _gear.State.Tracking;
    public bool IsParked => _gear.State.Parked;
    public bool IsSlewing { get; private set; }
    public PierSide SideOfPier => _gear.State.PierSide;

    public MountCapabilities Capabilities =>
        MountCapabilities.GermanEquatorial with { SupportsPulseGuide = true };

    public bool IsPulseGuiding { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default) {
        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default) {
        _connected = false;
        return Task.CompletedTask;
    }

    public Task SlewAsync(double ra, double dec, CancellationToken ct = default) {
        _gear.State.RightAscensionHours = ra;
        _gear.State.DeclinationDeg = dec;
        _gear.State.Tracking = true;
        _gear.State.Parked = false;
        return Task.CompletedTask;
    }

    public Task SyncAsync(double ra, double dec, CancellationToken ct = default) {
        _gear.State.RightAscensionHours = ra;
        _gear.State.DeclinationDeg = dec;
        return Task.CompletedTask;
    }

    public Task ParkAsync(CancellationToken ct = default) {
        _gear.State.Parked = true;
        _gear.State.Tracking = false;
        return Task.CompletedTask;
    }

    public Task UnparkAsync(CancellationToken ct = default) {
        _gear.State.Parked = false;
        return Task.CompletedTask;
    }

    public Task SetTrackingAsync(bool enabled, CancellationToken ct = default) {
        _gear.State.Tracking = enabled;
        return Task.CompletedTask;
    }

    public Task AbortSlewAsync(CancellationToken ct = default) {
        IsSlewing = false;
        return Task.CompletedTask;
    }

    public Task MoveNorthAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveSouthAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveEastAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveWestAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopMotionAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Set the simulated pier side, e.g. to rehearse a meridian flip.
    /// The native guider's pier-side handling reacts to <see cref="SideOfPier"/>
    /// changing between frames.</summary>
    public void SetPierSide(PierSide side) => _gear.State.PierSide = side;

    public async Task PulseGuideAsync(GuideDirections direction, int durationMs,
                                      CancellationToken ct = default) {
        IsPulseGuiding = true;
        try {
            _gear.State.St4Pulse(direction, durationMs, 1);
            // Mimic the pulse taking real time (PHD2 sleeps for the duration),
            // capped so tests with long pulses stay quick.
            if (durationMs > 0)
                await Task.Delay(Math.Min(durationMs, 5000), ct);
        } finally {
            IsPulseGuiding = false;
        }
    }
}
