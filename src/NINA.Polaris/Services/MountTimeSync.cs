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

using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Pushes the correct wall-clock UTC + observatory location into a mount right
/// before a GoTo. A mount resolves RA/Dec → Alt/Az with its OWN internal clock
/// and site; if either is stale, a GoTo swings the OTA to a physically wrong
/// place and can hit the tripod/pier. Pushing is idempotent and cheap, so we do
/// it before every slew when the rig opts in. When a driver can't accept the
/// push, the caller asks the operator to confirm rather than slewing blind.
/// </summary>
public static class MountTimeSync {
    /// <param name="Ok">True when the mount is safe to slew: its clock was set
    /// and its location is both configured and set. False ⇒ the caller should
    /// confirm (or refuse) the slew.</param>
    public record Result(bool Ok, bool TimeSet, bool LocationSet,
                         bool LocationConfigured, string? Reason);

    /// <summary>Push UTC + location. Best-effort: a driver that doesn't expose
    /// TIME_UTC / GEOGRAPHIC_COORD throws NotSupportedException, which we treat
    /// as "couldn't guarantee" (Ok=false) rather than an error.</summary>
    public static async Task<Result> SyncAsync(ITelescope telescope, UserProfile profile,
            CancellationToken ct = default) {
        bool timeSet = false;
        try {
            var utc = DateTime.UtcNow;
            var offsetHours = TimeZoneInfo.Local.GetUtcOffset(utc).TotalHours;
            await telescope.SetSiteTimeAsync(utc, offsetHours, ct);
            timeSet = true;
        } catch (NotSupportedException) { /* driver has no settable clock */ }
        catch { /* transient push error */ }

        bool locConfigured = Math.Abs(profile.Latitude) > 1e-6 || Math.Abs(profile.Longitude) > 1e-6;
        bool locSet = false;
        if (locConfigured) {
            try {
                await telescope.SetSiteLocationAsync(profile.Latitude, profile.Longitude, profile.Altitude, ct);
                locSet = true;
            } catch (NotSupportedException) { /* no settable site */ }
            catch { /* transient */ }
        }

        string? reason = null;
        if (!locConfigured)
            reason = "Your observatory location is not set (0, 0). Set your latitude/longitude "
                   + "in Settings so the mount can compute where targets are.";
        else if (!timeSet && !locSet)
            reason = "This mount driver does not accept a time or location from Polaris, so its "
                   + "clock/site can't be verified. A GoTo with a wrong mount clock can swing the "
                   + "scope the wrong way. Make sure the mount's date/time/location are correct.";
        else if (!timeSet)
            reason = "This mount driver does not accept a clock from Polaris, so its time can't be "
                   + "verified. A GoTo with a wrong mount clock can swing the scope the wrong way. "
                   + "Make sure the mount's date/time are correct.";
        else if (!locSet)
            reason = "This mount driver did not accept the observatory location from Polaris, so its "
                   + "site can't be verified. Make sure the mount's location is correct.";

        bool ok = timeSet && locSet;   // both pushed = mount is trustworthy for this GoTo
        return new Result(ok, timeSet, locSet, locConfigured, reason);
    }
}
