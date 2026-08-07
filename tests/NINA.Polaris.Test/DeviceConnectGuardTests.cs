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

using Microsoft.AspNetCore.Http;
using NINA.Polaris.Endpoints;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// A device connect must always come back.
///
/// Field report 2026-08-06: a Gemini focuser left selected via INDI on a rig
/// whose focuser is a ZWO EAF. Connecting it took the whole server down. The
/// operator could still find Polaris from the Android app, but the page loaded
/// blank - alive, not serving - and the board had to be rebooted mid-session.
///
/// Every connect and disconnect route used to call ConnectAsync() with no token
/// and no deadline, so a driver that never answers held the request open with
/// nothing to end it. These tests pin the two things that matter: the call is
/// cancelled, and the caller is told which device went quiet.
/// </summary>
[TestFixture]
public class DeviceConnectGuardTests {

    /// <summary>Short deadline: the real one is 45s and these tests are about
    /// the mechanism, not the duration (that is pinned separately below).</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(150);

    /// <summary>The token really reaches the driver, and the request comes back.
    /// Without the first the deadline would fire while the operation carried on
    /// in the background; without the second the server stops answering.</summary>
    [Test]
    public async Task ADriverThatNeverAnswersGetsCancelledAndAnswers504() {
        var observed = new TaskCompletionSource<bool>();

        var result = await DeviceConnectGuard.RunAsync("connect", "Gemini Focuser",
            async ct => {
                ct.Register(() => observed.TrySetResult(true));
                await Task.Delay(Timeout.Infinite, ct);
            },
            () => Results.Ok(),
            Quick);

        Assert.That(observed.Task.IsCompleted, Is.True,
            "o token do guard tem de chegar ao driver, senao o pedido volta e a operacao continua solta");
        Assert.That(result, Is.Not.Null, "o pedido tem de VOLTAR: era isso que faltava e derrubou o servidor");
    }

    /// <summary>A connect that answers normally must be untouched.</summary>
    [Test]
    public async Task ADriverThatAnswersIsNotDisturbed() {
        var ok = Results.Ok(new { status = "connected" });
        var result = await DeviceConnectGuard.RunAsync("connect", "ZWO EAF",
            _ => Task.CompletedTask, () => ok);

        Assert.That(result, Is.SameAs(ok));
    }

    /// <summary>The error has to name the device. "exit 1" style opacity is what
    /// made the original incident take a reboot to understand.</summary>
    [Test]
    public void TheTimeoutMessageNamesTheDeviceAndTheLikelyCause() {
        var ex = Assert.ThrowsAsync<TimeoutException>(async () =>
            await DeviceConnectGuard.BoundedAsync("connect", "Gemini Focuser",
                ct => Task.Delay(Timeout.Infinite, ct), Quick));

        Assert.That(ex!.Message, Does.Contain("Gemini Focuser"));
        Assert.That(ex.Message, Does.Contain("driver"),
            "a mensagem tem de apontar para o driver ausente, nao so dizer que expirou");
    }

    /// <summary>Long enough for a real driver on a slow SBC, short enough that a
    /// wedged one does not read as a dead server.</summary>
    [Test]
    public void TheDeadlineStaysInAUsefulRange() {
        Assert.That(DeviceConnectGuard.Deadline, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(20)));
        Assert.That(DeviceConnectGuard.Deadline, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(120)));
    }
}
