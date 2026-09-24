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
/// The rclone command lines, checked on a machine with no rclone installed.
/// That is the whole reason this logic is a pure static class: the argument
/// order, the fs composition and the validators are where the bugs live, and
/// none of them need a process to prove.
/// </summary>
[TestFixture]
public class RcloneArgsTests {
    private const string Cfg = "/data/rclone/rclone.conf";

    private static StorageConfig Rclone(string remote = "gdrive", string basePath = "astro",
                                        string bw = "") =>
        new("rclone", Host: "", Port: 0, Share: "", BasePath: basePath, Domain: "",
            Username: "", Password: "", LinkSharePercent: 100,
            RemoteName: remote, BandwidthLimit: bw);

    // ---- fs composition ----

    [TestCase("astro", "rig/lights/a.fits", "gdrive:astro/rig/lights/a.fits")]
    [TestCase("", "rig/a.fits", "gdrive:rig/a.fits")]
    [TestCase("/astro/", "rig/a.fits", "gdrive:astro/rig/a.fits")]
    [TestCase("astro/sub", "a.fits", "gdrive:astro/sub/a.fits")]
    public void Fs_JoinsRemoteBaseAndRelative(string basePath, string rel, string expected) {
        Assert.That(RcloneArgs.Fs("gdrive", basePath, rel), Is.EqualTo(expected));
    }

    [Test]
    public void Fs_NormalisesWindowsSeparators() {
        Assert.That(RcloneArgs.Fs("gdrive", "astro", @"rig\lights\a.fits"),
            Is.EqualTo("gdrive:astro/rig/lights/a.fits"));
    }

    [Test]
    public void Fs_WithNoBaseAndNoRelativeIsTheRemoteRoot() {
        Assert.That(RcloneArgs.Fs("gdrive", "", null), Is.EqualTo("gdrive:"));
    }

    [Test]
    public void Fs_RejectsAPathThatClimbsOut() {
        // The guard lives in StoragePath.Segments and must stay on this path:
        // a relative path comes from the capture tree, and ".." on a remote is
        // somebody else's folder.
        Assert.Throws<ArgumentException>(() => RcloneArgs.Fs("gdrive", "astro", "../../etc/passwd"));
    }

    // ---- copy ----

    [Test]
    public void Copy_PutsTheConfigFirstAndTheDestinationAfterTheSource() {
        var a = RcloneArgs.Copy(Cfg, Rclone(), @"C:\cap\rig\a.fits", "rig/a.fits");
        var verb = a.IndexOf("copyto");
        Assert.Multiple(() => {
            Assert.That(a[0], Is.EqualTo("--config"));
            Assert.That(a[1], Is.EqualTo(Cfg));
            Assert.That(verb, Is.GreaterThan(1), "the verb comes after the global flags");
            Assert.That(a[verb + 1], Is.EqualTo(@"C:\cap\rig\a.fits"), "the local path is passed through as it is");
            Assert.That(a[verb + 2], Is.EqualTo("gdrive:astro/rig/a.fits"));
        });
    }

    [Test]
    public void Copy_AsksForMachineReadableProgress() {
        var a = RcloneArgs.Copy(Cfg, Rclone(), "/cap/a.fits", "a.fits");
        Assert.That(a, Does.Contain("--use-json-log"));
        Assert.That(Pair(a, "--stats"), Is.EqualTo("1s"));
        Assert.That(Pair(a, "--stats-log-level"), Is.EqualTo("NOTICE"));
    }

    [Test]
    public void Copy_LeavesRetryToPolarisNotToRclone() {
        // The push lane retries three times with backoff and trips a breaker
        // after three consecutive failures. rclone's defaults would spend
        // minutes per file against a dead remote and hide exactly what the
        // breaker exists to notice.
        var a = RcloneArgs.Copy(Cfg, Rclone(), "/cap/a.fits", "a.fits");
        Assert.That(Pair(a, "--retries"), Is.EqualTo("1"));
        Assert.That(Pair(a, "--low-level-retries"), Is.EqualTo("3"));
    }

    [Test]
    public void Copy_KeepsThePartialSuffixConvention() {
        var a = RcloneArgs.Copy(Cfg, Rclone(), "/cap/a.fits", "a.fits");
        Assert.That(Pair(a, "--partial-suffix"), Is.EqualTo(StoragePath.PartialSuffix));
    }

    [Test]
    public void Copy_OmitsTheSpeedCapWhenThereIsNone() {
        Assert.That(RcloneArgs.Copy(Cfg, Rclone(bw: ""), "/cap/a.fits", "a.fits"),
            Does.Not.Contain("--bwlimit"));
    }

    [Test]
    public void Copy_IncludesTheSpeedCapWhenSet() {
        var a = RcloneArgs.Copy(Cfg, Rclone(bw: "2M"), "/cap/a.fits", "a.fits");
        Assert.That(Pair(a, "--bwlimit"), Is.EqualTo("2M"));
    }

    [Test]
    public void Copy_IgnoresASpeedCapThatIsNotOnTheList() {
        // Defence in depth: the endpoint validates too, but a value that got
        // past it must not reach the command line.
        var cfg = Rclone(bw: "; rm -rf /");
        Assert.That(RcloneArgs.Copy(Cfg, cfg, "/cap/a.fits", "a.fits"), Does.Not.Contain("--bwlimit"));
    }

    // ---- the other commands ----

    [Test]
    public void EveryCommandCarriesTheExplicitConfigPath() {
        // Without --config, a root-owned ~/.config/rclone/rclone.conf written by
        // a sudo session silently wins over ours.
        var cfg = Rclone();
        foreach (var args in new[] {
                     RcloneArgs.Copy(Cfg, cfg, "/a", "a"),
                     RcloneArgs.ListJson(Cfg, cfg),
                     RcloneArgs.Lsd(Cfg, cfg),
                     RcloneArgs.ListRemotes(Cfg),
                     RcloneArgs.About(Cfg, "gdrive"),
                     RcloneArgs.Version(Cfg),
                     RcloneArgs.ConfigDelete(Cfg, "gdrive")
                 }) {
            Assert.That(Pair(args, "--config"), Is.EqualTo(Cfg));
        }
    }

    [Test]
    public void EveryCommandAsksForJsonLogging() {
        // The failure message the operator reads is parsed out of the log, and
        // only the JSON form is parseable. Without this on the probe commands,
        // a wrong WebDAV URL surfaced as "rclone rejected the command" with
        // rclone's actual explanation thrown away.
        var cfg = Rclone();
        foreach (var args in new[] {
                     RcloneArgs.Copy(Cfg, cfg, "/a", "a"),
                     RcloneArgs.ListJson(Cfg, cfg),
                     RcloneArgs.Lsd(Cfg, cfg),
                     RcloneArgs.ListRemotes(Cfg),
                     RcloneArgs.About(Cfg, "gdrive"),
                     RcloneArgs.Version(Cfg),
                     RcloneArgs.ConfigDelete(Cfg, "gdrive")
                 }) {
            Assert.That(args.Count(a => a == "--use-json-log"), Is.EqualTo(1));
        }
    }

    [Test]
    public void ListJson_ScopesToASubTreeWhenAskedTo() {
        var a = RcloneArgs.ListJson(Cfg, Rclone(), "rig/M31/lights/2026-09-23");
        Assert.That(a, Does.Contain("gdrive:astro/rig/M31/lights/2026-09-23"));
        Assert.That(a, Does.Contain("--recursive"));
        Assert.That(a, Does.Contain("--files-only"));
        Assert.That(a, Does.Contain("--fast-list"));
    }

    [Test]
    public void ConfigCreate_PassesTheValuesAsPairsAndSaysWhetherToObscure() {
        var a = RcloneArgs.ConfigCreate(Cfg, "nas", "webdav",
            new Dictionary<string, string> { ["url"] = "https://x/dav", ["user"] = "me" },
            obscure: true);
        Assert.Multiple(() => {
            Assert.That(a, Does.Contain("create"));
            Assert.That(a, Does.Contain("nas"));
            Assert.That(a, Does.Contain("webdav"));
            Assert.That(Pair(a, "url"), Is.EqualTo("https://x/dav"));
            Assert.That(Pair(a, "user"), Is.EqualTo("me"));
            Assert.That(a, Does.Contain("--non-interactive"));
            Assert.That(a, Does.Contain("--obscure"));
        });
    }

    [Test]
    public void ConfigCreate_DoesNotObscureAValueThatIsAlreadyObscured() {
        // A password copied out of a working rclone.conf is already in rclone's
        // obscured form; obscuring it twice produces a credential that fails.
        var a = RcloneArgs.ConfigCreate(Cfg, "nas", "webdav",
            new Dictionary<string, string> { ["pass"] = "already-obscured" }, obscure: false);
        Assert.That(a, Does.Contain("--no-obscure"));
        Assert.That(a, Does.Not.Contain("--obscure"));
    }

    [TestCase("drive", "rclone authorize \"drive\"")]
    [TestCase("OneDrive", "rclone authorize \"onedrive\"")]
    public void AuthorizeCommand_IsBuiltFromTheProviderId(string type, string expected) {
        Assert.That(RcloneArgs.AuthorizeCommand(type), Is.EqualTo(expected));
    }

    // ---- validators ----

    [TestCase("")]
    [TestCase("500k")]
    [TestCase("10M")]
    public void Bandwidth_AcceptsWhatTheUiOffers(string v) {
        Assert.That(RcloneArgs.IsValidBandwidth(v), Is.True);
    }

    [TestCase("2 M")]
    [TestCase("; rm -rf /")]
    [TestCase("1TB/s")]
    [TestCase("--flag")]
    public void Bandwidth_RejectsAnythingElse(string v) {
        Assert.That(RcloneArgs.IsValidBandwidth(v), Is.False);
    }

    [TestCase("gdrive")]
    [TestCase("my-nas")]
    [TestCase("nas.1")]
    [TestCase("a_b")]
    public void RemoteName_AcceptsTheUsualShapes(string name) {
        Assert.That(RcloneArgs.IsValidRemoteName(name, out _), Is.True, name);
    }

    [TestCase("", "name")]
    [TestCase("a:b", "colon")]
    [TestCase("a/b", "slash")]
    [TestCase("a b", "space")]
    [TestCase("-lead", "leading dash")]
    public void RemoteName_RejectsWhatRcloneOrTheFsStringCannotTake(string name, string why) {
        Assert.That(RcloneArgs.IsValidRemoteName(name, out var reason), Is.False, why);
        Assert.That(reason, Is.Not.Null.And.Not.Empty, "the operator has to be told what to fix");
    }

    [TestCase("smb")]
    [TestCase("sftp")]
    [TestCase("local")]
    [TestCase("rclone")]
    public void RemoteName_RefusesTheNamesOfPolarisOwnKinds(string name) {
        // "the sftp target failed" would stop meaning one thing.
        Assert.That(RcloneArgs.IsValidRemoteName(name, out _), Is.False);
    }

    [Test]
    public void RemoteName_RefusesSomethingTooLongToReadInALog() {
        Assert.That(RcloneArgs.IsValidRemoteName(new string('a', 40), out _), Is.False);
    }

    [TestCase("drive", true)]
    [TestCase("onedrive", true)]
    [TestCase("dropbox", true)]
    [TestCase("webdav", false)]
    [TestCase("sftp", false)]
    [TestCase("s3", false)]
    public void OnlyTheOAuthProvidersNeedASecondMachine(string type, bool needsBrowser) {
        Assert.That(RcloneArgs.IsProviderType(type), Is.True);
        Assert.That(RcloneArgs.NeedsBrowserSignIn(type), Is.EqualTo(needsBrowser));
    }

    [TestCase("crypt")]
    [TestCase("")]
    [TestCase("nonsense")]
    public void AProviderOutsideTheAllowlistIsNotAProvider(string type) {
        Assert.That(RcloneArgs.IsProviderType(type), Is.False);
    }

    /// <summary>The value that follows a flag, so a test can assert a pair
    /// without depending on where in the list it landed.</summary>
    private static string? Pair(IReadOnlyList<string> args, string flag) {
        var i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }
}
