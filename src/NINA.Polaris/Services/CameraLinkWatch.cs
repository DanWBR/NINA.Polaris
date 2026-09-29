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

namespace NINA.Polaris.Services;

/// <summary>
/// Turns "the camera link went down on its own" into one message, once.
///
/// <para>The detection itself lives in the SDK adapters, which now drop
/// IsConnected when the driver reports the device removed. This is the part
/// that decides whether the operator hears about it: the transition matters,
/// the steady state does not. Without the latch a camera that is gone would
/// push a notification every second for the rest of the night, which is how a
/// warning becomes wallpaper.</para>
///
/// <para>Pure and per-device so it can be tested without a camera, a socket or
/// a clock.</para>
/// </summary>
public sealed class CameraLinkWatch {
    private readonly Dictionary<string, bool> _wasConnected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Feed the current state of one device. Returns the sentence to tell the
    /// operator, or null when there is nothing new to say.
    ///
    /// <para><paramref name="reason"/> is what the adapter recorded. A drop
    /// with no reason is a deliberate disconnect (the operator pressed the
    /// button, a rig switch, a driver restart) and stays silent: this is only
    /// for the ones nobody asked for.</para>
    /// </summary>
    public string? Observe(string role, string? deviceName, bool connected, string? reason) {
        var key = role ?? "";
        var had = _wasConnected.TryGetValue(key, out var prev) && prev;
        _wasConnected[key] = connected;

        if (connected || !had) return null;
        if (string.IsNullOrWhiteSpace(reason)) return null;

        var name = string.IsNullOrWhiteSpace(deviceName) ? role : deviceName;
        return $"{name} disconnected on its own: {reason}. "
             + "Check the cable and the power on the camera. Nothing will capture until it is back.";
    }

    /// <summary>Forget what is known about a device, so the next connection
    /// starts clean. Used when the rig changes under us.</summary>
    public void Forget(string role) => _wasConnected.Remove(role ?? "");
}
