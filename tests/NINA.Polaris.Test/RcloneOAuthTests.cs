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

using NINA.Polaris.Services;
using NINA.Polaris.Services.Storage;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The text handling behind the browser sign in, and the one-shot tickets that
/// let a download be a plain navigation. Both are pure, so neither test starts
/// a process or touches the network.
/// </summary>
[TestFixture]
public class RcloneOAuthTests {

    [Test]
    public void ParseAuthLink_FindsTheLinkRcloneActuallyPrints() {
        // Verbatim from rclone 1.75 on stderr.
        const string stderr = """
            2026/09/25 09:46:05 NOTICE: Config file not found - using defaults
            2026/09/25 09:46:05 NOTICE: Make sure your Redirect URL is set to "http://127.0.0.1:53682/" in your custom config.
            2026/09/25 09:46:05 NOTICE: Please go to the following link: http://127.0.0.1:53682/auth?state=SUYavevqKLnFEDcFwITnag
            2026/09/25 09:46:05 NOTICE: Log in and authorize rclone for access
            2026/09/25 09:46:05 NOTICE: Waiting for code...
            """;
        Assert.That(RcloneOAuthOutput.ParseAuthLink(stderr),
            Is.EqualTo("http://127.0.0.1:53682/auth?state=SUYavevqKLnFEDcFwITnag"));
    }

    [Test]
    public void ParseAuthLink_IgnoresTheRedirectUrlNotice() {
        // The notice above mentions 127.0.0.1:53682 too, but without /auth:
        // picking that one up would send the operator to a blank page.
        const string stderr = "NOTICE: Make sure your Redirect URL is set to \"http://127.0.0.1:53682/\"";
        Assert.That(RcloneOAuthOutput.ParseAuthLink(stderr), Is.Null);
    }

    [Test]
    public void ParseToken_TakesTheJsonBetweenThePasteMarkers() {
        const string stdout = """
            Paste the following into your remote machine --->
            {"access_token":"ya29.a0","token_type":"Bearer","refresh_token":"1//x","expiry":"2026-12-31T00:00:00-03:00"}
            <---End paste
            """;
        var token = RcloneOAuthOutput.ParseToken(stdout);
        Assert.That(token, Does.StartWith("{\"access_token\""));
        Assert.That(token, Does.EndWith("}"));
    }

    [Test]
    public void ParseToken_IsNullWhenRcloneSaidNothingUseful() {
        Assert.That(RcloneOAuthOutput.ParseToken(""), Is.Null);
        Assert.That(RcloneOAuthOutput.ParseToken("Failed to get token: oauth2: cannot fetch token"), Is.Null);
    }

    [TestCase("http://127.0.0.1:53682/?state=abc&code=4/0AX4", "state=abc&code=4/0AX4")]
    [TestCase("?state=abc&code=4/0AX4", "state=abc&code=4/0AX4")]
    [TestCase("state=abc&code=4/0AX4", "state=abc&code=4/0AX4")]
    [TestCase("http://127.0.0.1:53682/?state=abc&code=4/0AX4#frag", "state=abc&code=4/0AX4")]
    public void CallbackQuery_AcceptsWhateverShapeTheOperatorPasted(string pasted, string expected) {
        Assert.That(RcloneOAuthOutput.TryExtractCallbackQuery(pasted, out var q, out _), Is.True);
        Assert.That(q, Is.EqualTo(expected));
    }

    [Test]
    public void CallbackQuery_SaysWhatIsWrongInsteadOfFailingSilently() {
        Assert.That(RcloneOAuthOutput.TryExtractCallbackQuery("", out _, out var e1), Is.False);
        Assert.That(e1, Is.Not.Empty);

        // Half an address, which is what a double-click selection gives you.
        Assert.That(RcloneOAuthOutput.TryExtractCallbackQuery("http://127.0.0.1:53682/", out _, out var e2), Is.False);
        Assert.That(e2, Does.Contain("no sign in result"));

        // The provider refused: say so rather than "could not parse".
        Assert.That(RcloneOAuthOutput.TryExtractCallbackQuery(
            "http://127.0.0.1:53682/?error=access_denied&state=x", out _, out var e3), Is.False);
        Assert.That(e3, Does.Contain("access_denied"));

        // A wrapped paste would break the request line.
        Assert.That(RcloneOAuthOutput.TryExtractCallbackQuery("state=abc&co de=4/0AX4", out _, out var e4), Is.False);
        Assert.That(e4, Does.Contain("one piece"));
    }

    // ----- download tickets -----

    private sealed record Payload(string What);

    [Test]
    public void Ticket_IsSingleUse() {
        var svc = new DownloadTicketService();
        var id = svc.Create(new Payload("zip"));
        Assert.That(svc.TryTake<Payload>(id, out var first), Is.True);
        Assert.That(first!.What, Is.EqualTo("zip"));
        Assert.That(svc.TryTake<Payload>(id, out _), Is.False, "a second redemption must fail");
    }

    [Test]
    public void Ticket_ExpiresSoACopiedUrlIsUselessLater() {
        var now = DateTimeOffset.UtcNow;
        var svc = new DownloadTicketService(() => now);
        var id = svc.Create(new Payload("zip"));
        now += DownloadTicketService.Lifetime + TimeSpan.FromSeconds(1);
        Assert.That(svc.TryTake<Payload>(id, out _), Is.False);
        Assert.That(svc.Count, Is.EqualTo(0), "and it is swept out");
    }

    [Test]
    public void Ticket_UnknownOrEmptyIdIsRefused() {
        var svc = new DownloadTicketService();
        Assert.That(svc.TryTake<Payload>(null, out _), Is.False);
        Assert.That(svc.TryTake<Payload>("", out _), Is.False);
        Assert.That(svc.TryTake<Payload>("deadbeef", out _), Is.False);
    }

    [Test]
    public void Ticket_OutstandingCountIsBounded() {
        var svc = new DownloadTicketService();
        for (int i = 0; i < 200; i++) svc.Create(new Payload("zip" + i));
        Assert.That(svc.Count, Is.LessThanOrEqualTo(64));
    }
}
