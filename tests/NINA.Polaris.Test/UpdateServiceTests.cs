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

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.Polaris.Services.External;

namespace NINA.Polaris.Test;

/// <summary>
/// UpdateService is the SBC .deb self-updater. These tests pin the
/// platform/arch logic and the safe behaviour off a .deb install — the
/// network check + privileged install path are exercised manually on a
/// real Pi, since they touch GitHub and systemd.
/// </summary>
[TestFixture]
public class UpdateServiceTests {

    private static UpdateService Make() {
        // No HttpClient is needed for the supported/version/arch checks; pass a
        // factory that throws if actually used so the tests stay offline. The
        // ProfileService is an empty in-memory config: these checks don't read
        // the update channel off it.
        var profiles = new ProfileService(new ConfigurationBuilder().Build(),
            NullLogger<ProfileService>.Instance);
        return new UpdateService(NullLogger<UpdateService>.Instance,
            new ThrowingHttpClientFactory(), profiles);
    }

    [Test]
    public void CurrentVersion_is_non_null() {
        Assert.That(UpdateService.CurrentVersion, Is.Not.Null);
    }

    [Test]
    public void DpkgArch_maps_known_architectures() {
        // Whatever this test host is, the result must be a non-empty lowercase
        // token and one of the dpkg names for the common arches.
        var arch = UpdateService.DpkgArch;
        Assert.That(arch, Is.Not.Null.And.Not.Empty);
        Assert.That(arch, Is.EqualTo(arch.ToLowerInvariant()));

        var expected = RuntimeInformation.ProcessArchitecture switch {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "amd64",
            Architecture.Arm => "armhf",
            Architecture.X86 => "i386",
            _ => arch
        };
        Assert.That(arch, Is.EqualTo(expected));
    }

    [Test]
    public void CandidateBaseTags_includes_3part_tag_for_4part_assembly_version() {
        // Regression: release tags are 3-part (v0.84.8) but the assembly
        // version is normalised to 4 parts (0.84.8.0). "v"+version → v0.84.8.0
        // doesn't exist → compare 404 → "changelog unavailable". The candidate
        // list must include the 3-part spelling so the changelog resolves.
        var cands = UpdateService.CandidateBaseTags("v", new Version(0, 84, 8, 0)).ToList();
        Assert.That(cands, Does.Contain("v0.84.8.0"));  // 4-part (kept for safety)
        Assert.That(cands, Does.Contain("v0.84.8"));    // 3-part (the real tag)
        Assert.That(cands, Does.Contain("v0.84"));      // 2-part fallback
        // Most-specific first, de-duplicated.
        Assert.That(cands[0], Is.EqualTo("v0.84.8.0"));
        Assert.That(cands, Is.Unique);
    }

    [Test]
    public void CandidateBaseTags_honours_empty_prefix() {
        var cands = UpdateService.CandidateBaseTags("", new Version(1, 2, 3, 0)).ToList();
        Assert.That(cands, Does.Contain("1.2.3"));
        Assert.That(cands, Has.None.StartWith("v"));
    }

    [Test]
    public void IsSupported_is_false_off_a_deb_install() {
        // The CI / dev host is not a /opt/polaris .deb layout, so the feature
        // must report unsupported (Windows always; Linux dev boxes too).
        var svc = Make();
        Assert.That(svc.IsSupported, Is.False);
    }

    [Test]
    public async System.Threading.Tasks.Task CheckAsync_returns_unsupported_without_touching_network() {
        var svc = Make();
        var r = await svc.CheckAsync(force: true, CancellationToken.None);
        Assert.That(r.Supported, Is.False);
        Assert.That(r.UpdateAvailable, Is.False);
        Assert.That(r.CurrentVersion, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async System.Threading.Tasks.Task InstallAsync_refuses_off_a_deb_install() {
        var svc = Make();
        var (ok, error) = await svc.InstallAsync(CancellationToken.None);
        Assert.That(ok, Is.False);
        Assert.That(error, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async System.Threading.Tasks.Task ListReleasesAsync_returns_empty_off_a_deb_install() {
        // Off a .deb install the rollback list short-circuits to empty before
        // any network call (the ThrowingHttpClientFactory would blow up if hit).
        var svc = Make();
        var list = await svc.ListReleasesAsync(15, force: true, CancellationToken.None);
        Assert.That(list, Is.Not.Null.And.Empty);
    }

    [Test]
    public async System.Threading.Tasks.Task InstallVersionAsync_refuses_off_a_deb_install() {
        var svc = Make();
        var (ok, error) = await svc.InstallVersionAsync("0.84.5", CancellationToken.None);
        Assert.That(ok, Is.False);
        Assert.That(error, Is.Not.Null.And.Not.Empty);
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory {
        public System.Net.Http.HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("network must not be used in this test");
    }
}
