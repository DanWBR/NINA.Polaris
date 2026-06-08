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

namespace NINA.INDI.Client;

/// <summary>Result of an ack-based property write (SetNumberAsyncAck /
/// SetSwitchAsyncAck). INDI is a fire-and-forget XML protocol — the
/// server never replies with a per-write status code. Instead, after
/// the client sends a <c>newNumberVector</c> / <c>newSwitchVector</c>,
/// the driver echoes back a <c>set*Vector</c> with the property's new
/// <c>state</c>:
///
/// <list type="bullet">
///   <item><c>Busy</c> — driver accepted the command and is working on
///     it (e.g. mount is slewing, focuser is moving).</item>
///   <item><c>Ok</c> — driver acted instantly (e.g. tracking toggle).</item>
///   <item><c>Alert</c> — driver rejected the command. The
///     <c>message="..."</c> attribute usually explains why.</item>
/// </list>
///
/// The ack helpers wait for one of those state transitions to come
/// back within a timeout, then return this result so the caller can
/// distinguish "command accepted, watch for completion separately"
/// from "command rejected, surface to user" from "driver silent,
/// probably wedged".
///
/// This was added based on the NINA PINS pattern (NINA.INDI/Devices/
/// INDIDevice.cs:203-262 in that fork) — our previous
/// fire-and-forget SetNumberAsync had a race where IsSlewing could
/// read the property's previous Ok state before the driver flipped
/// it to Busy, making slews appear to finish instantly.</summary>
public record IndiAckResult(
    /// <summary>True when the property transitioned to <c>Busy</c> or
    /// <c>Ok</c> within the timeout. The driver has accepted the
    /// operation; the caller should poll for completion separately
    /// when <c>Busy</c> was the response (i.e. async ops like slew).</summary>
    bool Acknowledged,
    /// <summary>True when the property transitioned to <c>Alert</c>
    /// within the timeout. The driver rejected the operation; the
    /// caller should typically throw <see cref="System.InvalidOperationException"/>
    /// with <see cref="AlertMessage"/> so it surfaces as a toast.</summary>
    bool Rejected,
    /// <summary>True when no state transition happened within the
    /// timeout window. Usually means the driver is wedged, or the
    /// property name was wrong and the write was silently ignored.</summary>
    bool TimedOut,
    /// <summary>Populated when <see cref="Rejected"/> is true. Comes
    /// from the <c>message="..."</c> attribute on the rejecting
    /// set*Vector. Most well-behaved INDI drivers (indilib mounts,
    /// ZWO ASI, PlayerOne, etc.) put a useful sentence here ("Below
    /// horizon", "Slew limit exceeded", "Mount is parked"). Null when
    /// driver didn't supply one.</summary>
    string? AlertMessage);