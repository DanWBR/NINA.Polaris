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

using NINA.Polaris.Services.OpenCl;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>An Orange Pi 5 Pro moved from the vendor kernel to Armbian's
/// mainline 6.18 kept the vendor Mali OpenCL (libmali). Its first call printed
/// "No mali devices found" and segfaulted Polaris, twice in a row after boot.
/// Mesa's rusticl ran all six of Polaris's kernels on the same board.</summary>
[TestFixture]
public class OpenClStrandedMaliTests {
    private static readonly (string File, string Lib) Mali =
        ("/etc/OpenCL/vendors/mali.icd", "/usr/lib/aarch64-linux-gnu/libmali.so.1");
    private static readonly (string File, string Lib) Rusticl =
        ("/etc/OpenCL/vendors/rusticl.icd", "libRusticlOpenCL.so.1");

    [Test]
    public void OnlyLibmaliWithoutItsKernelDeviceIsRefused() {
        var (refuse, keep) = OpenClRuntime.PlanIcds(new[] { Mali }, maliDevicePresent: false);
        Assert.That(refuse, Does.Contain("libmali").And.Contain("/dev/mali0").And.Contain("mesa-opencl-icd"));
        Assert.That(keep, Is.Null);
    }

    [Test]
    public void LibmaliBesideRusticlIsSkippedSoRusticlCanRun() {
        // The loader opens every listed driver, so libmali would still crash
        // the process; it is left out and rusticl kept.
        var (refuse, keep) = OpenClRuntime.PlanIcds(new[] { Mali, Rusticl }, maliDevicePresent: false);
        Assert.That(refuse, Is.Null);
        Assert.That(keep, Is.EqualTo(new[] { Rusticl.File }));
    }

    [Test]
    public void LibmaliOnTheVendorKernelIsLeftAlone() {
        var (refuse, keep) = OpenClRuntime.PlanIcds(new[] { Mali, Rusticl }, maliDevicePresent: true);
        Assert.That(refuse, Is.Null);
        Assert.That(keep, Is.Null);
    }

    [Test]
    public void WithoutLibmaliThereIsNothingToDo() {
        Assert.That(OpenClRuntime.PlanIcds(new[] { Rusticl }, false), Is.EqualTo(((string?)null, (IReadOnlyList<string>?)null)));
        Assert.That(OpenClRuntime.PlanIcds(System.Array.Empty<(string, string)>(), false).Refuse, Is.Null);
    }
}
