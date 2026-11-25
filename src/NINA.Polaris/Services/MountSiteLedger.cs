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
/// When Polaris last sent the date and time, and the location, to the mount
/// (from Sync Site, Sync Time, the nightly prompt, the auto-connect or the
/// sync before a GoTo). The mount card shows it next to what the mount holds,
/// so the operator can tell whether the mount's values came from Polaris.
/// Kept in memory only: after a restart Polaris doesn't claim any earlier sync.
/// </summary>
public static class MountSiteLedger {
    public sealed record Entry(DateTime AtUtc, string Device);

    private static Entry? _time;
    private static Entry? _location;

    public static Entry? TimeSent => Volatile.Read(ref _time);
    public static Entry? LocationSent => Volatile.Read(ref _location);

    public static void NoteTimeSent(string? device) =>
        Volatile.Write(ref _time, new Entry(DateTime.UtcNow, device ?? ""));

    public static void NoteLocationSent(string? device) =>
        Volatile.Write(ref _location, new Entry(DateTime.UtcNow, device ?? ""));

    /// <summary>The entry, only if it was for <paramref name="device"/>, so a
    /// sync to another mount doesn't show as this one's.</summary>
    public static Entry? For(Entry? e, string? device) =>
        e != null && string.Equals(e.Device, device ?? "", StringComparison.Ordinal) ? e : null;
}
