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
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Tests for the pure-string helpers in HostInfo. The IO-heavy
/// DetectLinux/DetectWindows are not unit-tested directly, they hit
/// real /proc and WMI which differ per host; the classifier +
/// label-builder + placeholder-detector logic is what's worth
/// pinning down.
/// </summary>
[TestFixture]
public class HostInfoTests {

    [TestCase("Raspberry Pi 5 Model B Rev 1.0", "raspberry-pi")]
    [TestCase("Raspberry Pi 4 Model B Rev 1.4", "raspberry-pi")]
    [TestCase("Raspberry Pi 3 Model B Plus Rev 1.3", "raspberry-pi")]
    [TestCase("NVIDIA Jetson Nano Developer Kit", "jetson")]
    [TestCase("Orange Pi 4 Pro", "orangepi")]
    [TestCase("Orange Pi 5 Pro", "orangepi")]
    [TestCase("Radxa Rock Pi 4B", "rockpi")]
    [TestCase("ROCK Pi 5B (RK3588)", "rockpi")]
    [TestCase("Hardkernel Odroid-N2Plus", "odroid")]
    [TestCase("Intel NUC11PAHi7", "mini-pc")]
    [TestCase("VMware Virtual Platform", "vm")]
    [TestCase("Innotek GmbH VirtualBox", "vm")]
    [TestCase("QEMU Standard PC", "vm")]
    [TestCase("Dell Inc. PowerEdge R720", "linux")]   // no rule matches → generic linux
    public void ClassifyLinuxModel_RecognisesCommonHardware(string model, string expected) {
        Assert.That(HostInfo.ClassifyLinuxModel(model), Is.EqualTo(expected));
    }

    [TestCase("Intel NUC11PAHi7", "mini-pc")]
    [TestCase("VMware Virtual Platform", "vm")]
    [TestCase("Microsoft Hyper-V Virtual Machine", "vm")]
    [TestCase("Dell Latitude 7420", "windows")]
    [TestCase("ASUS PRIME X670-P", "windows")]
    public void ClassifyWindowsModel_RecognisesCommonHardware(string model, string expected) {
        Assert.That(HostInfo.ClassifyWindowsModel(model), Is.EqualTo(expected));
    }

    [Test]
    public void BuildShortLabel_StripsManufacturerNoise() {
        // Dell + similar OEMs love trailing "Inc.", "Corporation",
        // "Computer", drops them to keep the bar readable.
        Assert.That(HostInfo.BuildShortLabel("linux", "Dell Inc. PowerEdge R720", null, "x64"),
            Is.EqualTo("Dell PowerEdge R720"));
        Assert.That(HostInfo.BuildShortLabel("linux", "Hewlett-Packard Corporation EliteBook 840", null, "x64"),
            Is.EqualTo("Hewlett-Packard EliteBook 840"));
    }

    [Test]
    public void BuildShortLabel_StripsRevSuffix() {
        // Silicon revisions ("Rev 1.0", "Rev 1.4") are noise to a
        // user reading the status bar.
        Assert.That(HostInfo.BuildShortLabel("raspberry-pi", "Raspberry Pi 5 Model B Rev 1.0", null, "arm64"),
            Is.EqualTo("Raspberry Pi 5 Model B"));
    }

    [Test]
    public void BuildShortLabel_TruncatesAtMaxLength() {
        var long_ = new string('X', 80);
        var s = HostInfo.BuildShortLabel("linux", long_, null, "x64");
        Assert.That(s.Length, Is.LessThanOrEqualTo(56));
        Assert.That(s, Does.EndWith("..."));
    }

    [Test]
    public void BuildShortLabel_UnknownModelWithCpu_FallsBackToCpu() {
        // When the OEM left a placeholder we want at least the CPU
        // to identify the box, better than "Host (x64)".
        Assert.That(HostInfo.BuildShortLabel("windows", "Unknown", "AMD Ryzen 9 7950X", "x64"),
            Is.EqualTo("AMD Ryzen 9 7950X"));
    }

    [Test]
    public void BuildShortLabel_UnknownModelNoCpu_GivesArchFallback() {
        Assert.That(HostInfo.BuildShortLabel("generic", "Unknown", null, "arm64"),
            Is.EqualTo("Host (arm64)"));
        Assert.That(HostInfo.BuildShortLabel("generic", "", null, "x64"),
            Is.EqualTo("Host (x64)"));
    }

    // --- Placeholder detection: the whole point of this round of fixes.
    [TestCase("System Product Name", true)]
    [TestCase("SYSTEM PRODUCT NAME", true)]      // case-insensitive
    [TestCase("All Series", true)]
    [TestCase("To Be Filled By O.E.M.", true)]
    [TestCase("To be filled by OEM", true)]
    [TestCase("Default string", true)]
    [TestCase("Default String", true)]
    [TestCase("Not Specified", true)]
    [TestCase("None", true)]
    [TestCase("Unknown", true)]
    [TestCase("N/A", true)]
    [TestCase("", true)]
    [TestCase(null, true)]
    [TestCase("   ", true)]
    [TestCase("Raspberry Pi 5 Model B Rev 1.0", false)]
    [TestCase("Dell Latitude 7420", false)]
    [TestCase("ASUS PRIME X670-P", false)]
    [TestCase("MSI MAG B650 TOMAHAWK WIFI", false)]
    [TestCase("VMware Virtual Platform", false)]
    [TestCase("Intel NUC11PAHi7", false)]
    public void IsPlaceholderModel_DistinguishesRealVsPlaceholder(string? input, bool expected) {
        Assert.That(HostInfo.IsPlaceholderModel(input), Is.EqualTo(expected));
    }

    // --- Manufacturer + model combination.
    [TestCase("ASUS", "ROG STRIX Z690-E GAMING", "ASUS ROG STRIX Z690-E GAMING")]
    [TestCase("Dell Inc.", "Latitude 7420", "Dell Inc. Latitude 7420")]
    [TestCase("ASUS", "ASUS Prime X670-P", "ASUS Prime X670-P")]   // dedupes
    [TestCase("", "Custom", "Custom")]
    [TestCase("Brand", "", "Brand")]
    [TestCase(null, "Just Model", "Just Model")]
    [TestCase("Just Brand", null, "Just Brand")]
    public void CombineMfgAndModel_WorksAsExpected(string? mfg, string? model, string expected) {
        Assert.That(HostInfo.CombineMfgAndModel(mfg, model), Is.EqualTo(expected));
    }

    [Test]
    public void CombineMfgAndModel_BothEmpty_ReturnsNull() {
        Assert.That(HostInfo.CombineMfgAndModel(null, null), Is.Null);
        Assert.That(HostInfo.CombineMfgAndModel("", ""), Is.Null);
    }

    // --- CPU brand normalisation.
    [TestCase("Intel(R) Core(TM) i7-12700K CPU @ 3.60GHz", "Intel Core i7-12700K")]
    [TestCase("Intel(R) Xeon(R) Gold 6248R CPU @ 3.00GHz", "Intel Xeon Gold 6248R")]
    [TestCase("AMD Ryzen 9 7950X 16-Core Processor", "AMD Ryzen 9 7950X")]
    [TestCase("AMD Ryzen 5 5600X 6-Core Processor", "AMD Ryzen 5 5600X")]
    [TestCase("Apple M2 Pro", "Apple M2 Pro")]
    [TestCase("ARM Cortex-A76 r4p1", "ARM Cortex-A76 r4p1")]
    public void NormaliseCpuName_StripsVendorMarketingNoise(string raw, string expected) {
        Assert.That(HostInfo.NormaliseCpuName(raw), Is.EqualTo(expected));
    }

    [Test]
    public void NormaliseCpuName_NullOrEmpty_ReturnsNull() {
        Assert.That(HostInfo.NormaliseCpuName(null), Is.Null);
        Assert.That(HostInfo.NormaliseCpuName(""), Is.Null);
        Assert.That(HostInfo.NormaliseCpuName("   "), Is.Null);
    }

    // --- CPU label formatting (brand + freq + cores).
    [Test]
    public void BuildCpuLabel_AllParts_RendersFull() {
        Assert.That(HostInfo.BuildCpuLabel("Intel Core i7-12700K", 3600, 20),
            Is.EqualTo("Intel Core i7-12700K @ 3.60 GHz · 20 cores"));
    }

    [Test]
    public void BuildCpuLabel_NoFrequency_DropsClockPart() {
        Assert.That(HostInfo.BuildCpuLabel("AMD Ryzen 9 7950X", null, 32),
            Is.EqualTo("AMD Ryzen 9 7950X · 32 cores"));
        Assert.That(HostInfo.BuildCpuLabel("AMD Ryzen 9 7950X", 0, 32),
            Is.EqualTo("AMD Ryzen 9 7950X · 32 cores"));
    }

    [Test]
    public void BuildCpuLabel_SingleCore_DropsCoreCount() {
        // "1 core" is silly to call out; only render when >1.
        Assert.That(HostInfo.BuildCpuLabel("ARM Cortex-A53", 1400, 1),
            Is.EqualTo("ARM Cortex-A53 @ 1.40 GHz"));
    }

    [Test]
    public void BuildCpuLabel_OnlyBrand_RendersJustBrand() {
        Assert.That(HostInfo.BuildCpuLabel("Apple M2 Pro", null, 1),
            Is.EqualTo("Apple M2 Pro"));
    }

    [Test]
    public void BuildCpuLabel_NothingUseful_ReturnsNull() {
        Assert.That(HostInfo.BuildCpuLabel(null, null, 1), Is.Null);
        Assert.That(HostInfo.BuildCpuLabel("", 0, 1), Is.Null);
    }

    [Test]
    public void BuildCpuLabel_FrequencyFormattingHas2Decimals() {
        // 5800 MHz → "5.80 GHz" (not "5.8 GHz" or "5,80 GHz")
        Assert.That(HostInfo.BuildCpuLabel("X", 5800, 1), Does.Contain("5.80 GHz"));
        Assert.That(HostInfo.BuildCpuLabel("X", 1000, 1), Does.Contain("1.00 GHz"));
    }

    // --- device-tree "compatible" fallback: resolves a friendly board name
    //     when a vendor image only puts the SoC codename in the model node
    //     (e.g. Orange Pi's stock Ubuntu reports "sun60iw2").
    [TestCase("sun60iw2", true)]   // Allwinner A733 (Orange Pi 4 Pro)
    [TestCase("sun55iw3", true)]   // Allwinner T527
    [TestCase("rk3588", true)]
    [TestCase("rk3588s", true)]
    [TestCase("rk3399", true)]
    [TestCase("h616", true)]
    [TestCase("Raspberry Pi 5 Model B Rev 1.0", false)] // real board name (has spaces)
    [TestCase("Orange Pi 4 Pro", false)]
    [TestCase("", false)]
    public void LooksLikeSocCodename_ClassifiesBareCodenames(string model, bool expected) {
        Assert.That(HostInfo.LooksLikeSocCodename(model), Is.EqualTo(expected));
    }

    [TestCase("xunlong", "Orange Pi")]
    [TestCase("raspberrypi", "Raspberry Pi")]
    [TestCase("radxa", "Radxa")]
    [TestCase("hardkernel", "ODROID")]
    [TestCase("allwinner", null)]   // silicon vendor -> skipped
    [TestCase("rockchip", null)]
    [TestCase("qualcomm", null)]
    public void VendorBrand_MapsBoardVendorsOnly(string vendor, string? expected) {
        Assert.That(HostInfo.VendorBrand(vendor), Is.EqualTo(expected));
    }

    [TestCase("Orange Pi", "orangepi-4-pro", "Orange Pi 4 Pro")]
    [TestCase("Orange Pi", "orangepi-5-pro", "Orange Pi 5 Pro")]
    [TestCase("Raspberry Pi", "4-model-b", "Raspberry Pi 4 Model B")]
    [TestCase("Radxa", "rock-5b", "Radxa Rock 5b")]
    public void BeautifyBoardSlug_TitleCasesAndDropsBrandDuplication(string brand, string slug, string expected) {
        Assert.That(HostInfo.BeautifyBoardSlug(brand, slug), Is.EqualTo(expected));
    }

    [Test]
    public void Current_ReturnsCachedInstance_AcrossCalls() {
        // HostInfo.Current is a Lazy<T>; subsequent calls must not
        // re-run the (relatively expensive) detection path.
        var a = HostInfo.Current;
        var b = HostInfo.Current;
        Assert.That(b, Is.SameAs(a));
    }

    [Test]
    public void Current_HasNonEmptyFields() {
        // Best-effort detection should always populate every field
        // with at least a fallback, the UI binds to ShortLabel
        // and expects a string. Cpu is optional (null on systems
        // that don't expose /proc/cpuinfo or WMI).
        var info = HostInfo.Current;
        Assert.That(info.Kind, Is.Not.Null.And.Not.Empty);
        Assert.That(info.Model, Is.Not.Null.And.Not.Empty);
        Assert.That(info.Os, Is.Not.Null.And.Not.Empty);
        Assert.That(info.Architecture, Is.Not.Null.And.Not.Empty);
        Assert.That(info.Cores, Is.GreaterThan(0));
        Assert.That(info.ShortLabel, Is.Not.Null.And.Not.Empty);
        // Cpu may legitimately be null on hosts where the underlying
        // syscall failed (locked-down container, exotic kernel),
        // don't assert non-null here.
    }
}