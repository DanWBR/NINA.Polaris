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

using System.Runtime.Versioning;

namespace NINA.Polaris;

/// <summary>
/// Entry-point dispatcher invoked when the Polaris executable is
/// re-launched with <c>--ascom-setup &lt;ProgID&gt;</c>. Runs the
/// ASCOM driver's modal <c>SetupDialog()</c> on a dedicated STA
/// thread, then exits the process with code 0 on success or 1 on
/// failure (error text on stderr).
///
/// <para>Why a subprocess: ASCOM drivers — especially the VB6-era
/// ZWO ones — can throw <see cref="AccessViolationException"/> from
/// inside SetupDialog. .NET 5+ tears down the host process on a
/// corrupted-state exception regardless of any try/catch and
/// regardless of <c>LegacyCorruptedStateExceptionsPolicy</c> in
/// most configurations, so an in-process call gambles the entire
/// API server every time the user clicks Setup. Isolating it in a
/// child process means a buggy driver only kills the helper —
/// systemd / Windows services / `dotnet run` keep running and the
/// HTTP request returns a proper 4xx with the stderr text.</para>
///
/// <para>The dispatch is hardwired at the very top of
/// <c>Program.cs</c> before <c>WebApplication.CreateBuilder</c>
/// runs, so the helper path skips all of the HTTP / WS / Kestrel
/// machinery — fastest start, smallest blast radius.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AscomSetupRunner {
    public static int Run(string progId) {
        if (string.IsNullOrWhiteSpace(progId)) {
            Console.Error.WriteLine("Missing ProgID argument.");
            return 2;
        }
        try {
            NINA.Ascom.Com.AscomComSetup
                .OpenSetupDialogAsync(progId)
                .GetAwaiter().GetResult();
            return 0;
        } catch (Exception ex) {
            // Surface the actual driver-thrown message so the parent
            // process can turn it into a toast on the browser.
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}