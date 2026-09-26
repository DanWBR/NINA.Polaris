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
/// Identity of THIS server process: a random id minted at startup, and when it
/// started.
///
/// It exists so the browser can tell two very different things apart, which
/// look identical from the client side: the network dropped, or the server
/// went away and came back. The socket closes and reopens either way. If the
/// id that comes back differs, this is a new process, which means the old one
/// exited, which means anything it was running stopped.
///
/// That distinction is not cosmetic. A user on a fresh Lubuntu spent a day
/// chasing his ethernet because the banner said the connection was lost, when
/// the server was in fact crashing at startup and systemd was restarting it
/// every five seconds. The browser had the evidence and did not use it.
/// </summary>
public static class ServerInstance {
    /// <summary>Unique to this process. Deliberately not derived from the host
    /// or the profile: two consecutive runs on the same machine must differ.</summary>
    public static string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    public static DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    public static TimeSpan Uptime => DateTimeOffset.UtcNow - StartedAtUtc;
}
