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

namespace NINA.Image.Interfaces;

/// <summary>
/// One addressable channel of a switch / power-distribution device. Maps
/// directly onto an ASCOM ISwitchV2 switch, an Alpaca <c>/switch/</c> index,
/// or a flattened INDI switch-/number-vector element.
///
/// <para><see cref="Boolean"/> distinguishes a two-state outlet (12V port
/// on/off) from an analog channel (PWM dew heater 0-100, an adjustable
/// voltage rail). <see cref="Writable"/> is false for read-only sensor
/// channels (voltage / current / temperature / humidity). <see cref="Id"/>
/// is a stable index the API/UI use to address the channel for writes —
/// it must not shift between refreshes of the same connected device.</para>
///
/// <para><see cref="Key"/> is a stable identity that survives RECONNECTS and
/// restarts, which <see cref="Id"/> does not: the id is a position in the
/// current channel map and would shift if the driver published a different
/// property set. Anything persisted against a channel (operator-assigned
/// names, a device-to-outlet mapping) must key off this instead. Format is
/// per-backend and opaque to callers: <c>PROPERTY.ELEMENT</c> for INDI,
/// <c>#index</c> for ASCOM/Alpaca, which have no richer identity to offer.</para>
/// </summary>
public sealed record SwitchChannel(
    int Id,
    string Name,
    bool Boolean,
    double Value,
    double Min,
    double Max,
    double Step,
    bool Writable,
    string Key = "");

/// <summary>
/// A generic multi-channel switch / power box (ASCOM ISwitchV2 semantics),
/// exposed so <c>EquipmentManager</c>, the RIGS UI and the Advanced Sequencer
/// power-box instructions can drive INDI / ASCOM-COM / Alpaca power hubs the
/// same way. The device model is intentionally brand-agnostic: outlets and
/// dew/PWM channels are just boolean vs analog channels, and voltage/current/
/// temperature readouts are read-only channels — no per-vendor curation.
/// </summary>
public interface ISwitchDevice {
    string DeviceName { get; }
    bool IsConnected { get; }

    /// <summary>Live channel snapshot. Values reflect the latest device state;
    /// channel <see cref="SwitchChannel.Id"/>s stay stable while connected.</summary>
    IReadOnlyList<SwitchChannel> Channels { get; }

    /// <summary>Total number of channels (== <see cref="Channels"/>.Count).</summary>
    int SwitchCount { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>Set a boolean channel on/off. For an analog channel this
    /// maps to its min (off) / max (on) as a convenience.</summary>
    Task SetBoolAsync(int id, bool on, CancellationToken ct = default);

    /// <summary>Set an analog channel value (clamped to the channel's
    /// min/max). For a boolean channel any non-zero value means on.</summary>
    Task SetValueAsync(int id, double value, CancellationToken ct = default);

    /// <summary>Re-read the channel set + current values from the device.</summary>
    Task RefreshAsync(CancellationToken ct = default);
}
