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

using NINA.Polaris.Services.Storage;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// What the operator pastes after signing in on another machine.
///
/// <para>The rig has no browser, so a Drive or Dropbox sign-in happens on a
/// laptop and the result is carried across by hand. People bring back either
/// the token blob or a whole section of their desktop rclone.conf, and both
/// have to work: telling someone they pasted the wrong correct thing is a bad
/// product.</para>
/// </summary>
[TestFixture]
public class RcloneConfigPasteTests {
    [Test]
    public void ABareTokenBecomesTheTokenValue() {
        const string token = """{"access_token":"ya29.x","refresh_token":"1//y","expiry":"2026-09-23T20:00:00Z"}""";
        Assert.That(RcloneConfigPaste.TryParse(token, out var parsed, out var err), Is.True, err);
        Assert.Multiple(() => {
            Assert.That(parsed.Values["token"], Is.EqualTo(token));
            Assert.That(parsed.Name, Is.Null, "a bare token carries no name, the form supplies one");
            Assert.That(parsed.Type, Is.Null);
            Assert.That(parsed.AlreadyObscured, Is.False);
        });
    }

    [Test]
    public void TheBannerRcloneAuthorizePrintsIsStripped() {
        const string pasted = """
        Paste the following into your remote machine --->
        {"access_token":"ya29.x","refresh_token":"1//y"}
        <---End paste
        """;
        Assert.That(RcloneConfigPaste.TryParse(pasted, out var parsed, out var err), Is.True, err);
        Assert.That(parsed.Values["token"], Does.StartWith("{").And.EndWith("}"));
        Assert.That(RcloneOutputIsJson(parsed.Values["token"]), Is.True);
    }

    [Test]
    public void AWholeSectionCarriesItsNameTypeAndEveryKey() {
        const string pasted = """
        [gdrive]
        type = drive
        scope = drive
        token = {"access_token":"ya29.x"}
        team_drive =
        """;
        Assert.That(RcloneConfigPaste.TryParse(pasted, out var parsed, out var err), Is.True, err);
        Assert.Multiple(() => {
            Assert.That(parsed.Name, Is.EqualTo("gdrive"));
            Assert.That(parsed.Type, Is.EqualTo("drive"));
            Assert.That(parsed.Values["scope"], Is.EqualTo("drive"));
            Assert.That(parsed.Values["token"], Is.EqualTo("""{"access_token":"ya29.x"}"""));
            Assert.That(parsed.Values.ContainsKey("type"), Is.False, "type is passed separately");
            Assert.That(parsed.AlreadyObscured, Is.True,
                "a password from an existing config must not be obscured twice");
        });
    }

    [Test]
    public void TheOneDriveShapeSurvives() {
        // OneDrive needs drive_id and drive_type, which `rclone authorize` alone
        // does not produce. This is exactly why the whole-section paste exists.
        const string pasted = """
        [onedrive]
        type = onedrive
        token = {"access_token":"x"}
        drive_id = b!abc123
        drive_type = business
        """;
        Assert.That(RcloneConfigPaste.TryParse(pasted, out var parsed, out var err), Is.True, err);
        Assert.That(parsed.Values["drive_id"], Is.EqualTo("b!abc123"));
        Assert.That(parsed.Values["drive_type"], Is.EqualTo("business"));
    }

    [Test]
    public void WindowsLineEndingsAndCommentsAreTolerated() {
        const string pasted = "# my nas\r\n[nas]\r\ntype = webdav\r\n; a comment\r\nurl = https://x/dav\r\n";
        Assert.That(RcloneConfigPaste.TryParse(pasted, out var parsed, out var err), Is.True, err);
        Assert.That(parsed.Name, Is.EqualTo("nas"));
        Assert.That(parsed.Values["url"], Is.EqualTo("https://x/dav"));
    }

    [Test]
    public void AValueContainingAnEqualsSignKeepsIt() {
        const string pasted = "[s3x]\ntype = s3\nsecret_access_key = abc=def=\n";
        Assert.That(RcloneConfigPaste.TryParse(pasted, out var parsed, out _), Is.True);
        Assert.That(parsed.Values["secret_access_key"], Is.EqualTo("abc=def="));
    }

    [Test]
    public void TwoSectionsAreRefused() {
        const string pasted = "[a]\ntype = drive\n[b]\ntype = dropbox\n";
        Assert.That(RcloneConfigPaste.TryParse(pasted, out _, out var err), Is.False);
        Assert.That(err, Does.Contain("one remote"));
    }

    [Test]
    public void ASectionWithoutATypeIsRefused() {
        Assert.That(RcloneConfigPaste.TryParse("[a]\nurl = https://x\n", out _, out var err), Is.False);
        Assert.That(err, Does.Contain("type"));
    }

    [Test]
    public void ATypeOutsideTheAllowlistIsRefused() {
        // The allowlist is the boundary: whatever is pasted ends up in the
        // config file, so an unexpected backend is refused rather than written.
        Assert.That(RcloneConfigPaste.TryParse("[a]\ntype = crypt\n", out _, out var err), Is.False);
        Assert.That(err, Does.Contain("crypt"));
    }

    [TestCase("")]
    [TestCase("   \n  \n")]
    [TestCase(null)]
    public void NothingUsefulGivesAnActionableMessage(string? pasted) {
        Assert.That(RcloneConfigPaste.TryParse(pasted, out _, out var err), Is.False);
        Assert.That(err, Does.Contain("Paste"));
    }

    [Test]
    public void ATruncatedTokenIsRefusedRatherThanStored() {
        Assert.That(RcloneConfigPaste.TryParse("""{"access_token":"ya29.x" """, out _, out var err),
            Is.False);
        Assert.That(err, Does.Contain("complete"));
    }

    [Test]
    public void FreeTextIsRefused() {
        Assert.That(RcloneConfigPaste.TryParse("here is my google password", out _, out var err),
            Is.False);
        Assert.That(err, Is.Not.Null);
    }

    private static bool RcloneOutputIsJson(string s) {
        try { using var _ = System.Text.Json.JsonDocument.Parse(s); return true; }
        catch (System.Text.Json.JsonException) { return false; }
    }
}
