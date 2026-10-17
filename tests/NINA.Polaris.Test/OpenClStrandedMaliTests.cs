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
/// "No mali devices found" and segfaulted Polaris, twice in a row after boot.</summary>
[TestFixture]
public class OpenClStrandedMaliTests {
    private static readonly string[] OnlyLibmali = { "/usr/lib/aarch64-linux-gnu/libmali.so.1" };

    [Test]
    public void LibmaliWithoutItsKernelDeviceIsRefused() {
        var why = OpenClRuntime.StrandedMaliReason(OnlyLibmali, maliDevicePresent: false);
        Assert.That(why, Does.Contain("libmali").And.Contain("/dev/mali0").And.Contain("mesa-opencl-icd"));
    }

    [Test]
    public void LibmaliOnTheVendorKernelIsFine() {
        Assert.That(OpenClRuntime.StrandedMaliReason(OnlyLibmali, maliDevicePresent: true), Is.Null);
    }

    [Test]
    public void AnotherDriverOrNoneIsNotThisCase() {
        Assert.That(OpenClRuntime.StrandedMaliReason(new[] { "libRusticlOpenCL.so.1" }, false), Is.Null);
        Assert.That(OpenClRuntime.StrandedMaliReason(new[] { "libmali.so.1", "libRusticlOpenCL.so.1" }, false), Is.Null,
            "with rusticl beside it the loader still opens libmali; the crash guard covers that");
        Assert.That(OpenClRuntime.StrandedMaliReason(System.Array.Empty<string>(), false), Is.Null);
    }
}
