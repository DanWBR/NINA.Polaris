using NINA.INDI.Client;

namespace NINA.INDI.Devices;

public class IndiFocuser : NINA.Image.Interfaces.IFocuser {
    private readonly IndiClient _client;

    public string DeviceName { get; }
    /// <summary>
    /// True only when the INDI client is up AND the device's per-device
    /// CONNECTION switch is in the CONNECT state. See
    /// <see cref="IndiCamera.IsConnected"/> for the rationale.
    /// </summary>
    public bool IsConnected
        => _client.IsConnected
           && _client.GetSwitch(DeviceName, "CONNECTION", "CONNECT");
    public int Position => (int)_client.GetNumber(DeviceName, "ABS_FOCUS_POSITION", "FOCUS_ABSOLUTE_POSITION");
    public double Temperature => _client.GetNumber(DeviceName, "FOCUS_TEMPERATURE", "TEMPERATURE");
    public int MaxPosition => (int)_client.GetNumber(DeviceName, "FOCUS_MAX", "FOCUS_MAX_VALUE");
    public bool IsMoving {
        get {
            var prop = _client.GetProperty(DeviceName, "ABS_FOCUS_POSITION");
            return prop?.State == Protocol.IndiPropertyState.Busy;
        }
    }

    public IndiFocuser(IndiClient client, string deviceName) {
        _client = client;
        DeviceName = deviceName;
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = true, ["DISCONNECT"] = false }, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = false, ["DISCONNECT"] = true }, ct);
    }

    public async Task MoveAbsoluteAsync(int position, CancellationToken ct = default) {
        // Pre-flight: refuse to send to a disconnected device. Some
        // drivers (ZWO EAF) silently accept a number vector while
        // disconnected and only error out when the next operation
        // probes CONNECTION -- by then the UI thinks the move
        // succeeded when it never started.
        if (!IsConnected) {
            throw new InvalidOperationException(
                $"Focuser '{DeviceName}' is not connected. Connect it from the RIGS tab before moving.");
        }
        // Also refuse moves while a previous move is in flight. The
        // EAF driver in particular re-interprets the second
        // FOCUS_ABSOLUTE_POSITION write mid-flight as a relative
        // command, which can over-shoot and trip the limit switch
        // (visible to the user as "disconnect on move").
        if (IsMoving) {
            throw new InvalidOperationException(
                $"Focuser '{DeviceName}' is already moving. Wait for the current move to settle.");
        }
        // Clamp into the driver-reported travel range. Writing a value
        // outside [0, MaxPosition] is a frequent reason INDI focuser
        // drivers (ZWO EAF especially) flip CONNECTION.PARK off as an
        // error response -- from the UI it looks like "I tried to move
        // and the focuser disconnected". MaxPosition can come back as 0
        // before the driver populates FOCUS_MAX; in that case skip the
        // upper clamp and trust the caller.
        var target = position;
        var max = MaxPosition;
        if (max > 0) target = Math.Clamp(target, 0, max);
        else        target = Math.Max(0, target);
        // Short-circuit moves to the current position. Some EAF
        // firmware revisions treat "move to where you already are"
        // as an error condition and toggle CONNECTION off as the
        // response. Cheap to guard, expensive to recover from.
        if (target == Position) return;
        await _client.SetNumberAsync(DeviceName, "ABS_FOCUS_POSITION",
            new Dictionary<string, double> { ["FOCUS_ABSOLUTE_POSITION"] = target }, ct);
    }

    public async Task MoveRelativeAsync(int steps, CancellationToken ct = default) {
        // Compute the absolute target client-side and delegate to
        // MoveAbsoluteAsync, rather than going through the
        // FOCUS_MOTION + REL_FOCUS_POSITION two-step. Two reasons:
        //
        //  1) Race condition. Sending a switch (FOCUS_MOTION) and a
        //     number (REL_FOCUS_POSITION) back-to-back over the same
        //     INDI TCP stream lets the driver receive them in either
        //     order on its parser side. The ZWO EAF driver
        //     specifically has been observed to disconnect itself
        //     when the number arrives before the switch -- it
        //     interprets the unsigned step count as an absolute
        //     destination, sees it's invalid for the current
        //     direction state, and tears down CONNECTION as an error
        //     response.
        //
        //  2) Uniformity. Every IFocuser-compliant INDI driver
        //     handles ABS_FOCUS_POSITION the same way; REL_FOCUS_
        //     POSITION's interplay with FOCUS_MOTION is spec'd
        //     loosely and individual drivers differ. One code path
        //     means one bug to chase per quirky driver.
        var target = Position + steps;
        await MoveAbsoluteAsync(target, ct);
    }

    public async Task AbortAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "FOCUS_ABORT_MOTION",
            new Dictionary<string, bool> { ["ABORT"] = true }, ct);
    }
}
