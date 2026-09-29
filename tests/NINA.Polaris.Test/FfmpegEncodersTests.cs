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

using NINA.Polaris.Services.Broadcast;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Choosing the H.264 encoder from what the host's ffmpeg reports. Getting this
/// wrong is not a quality question: a board encoding in software while it
/// captures is spending CPU the camera needs.
/// </summary>
[TestFixture]
public class FfmpegEncodersTests {

    // Real output, ffmpeg 6.1.1 on Ubuntu, trimmed to the H.264 rows.
    private const string UbuntuX86 = """
         V..... h263_v4l2m2m         V4L2 mem2mem H.263 encoder wrapper (codec h263)
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D libx264rgb           libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 RGB (codec h264)
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         V..... h264_qsv             H.264 / AVC (Intel Quick Sync Video acceleration) (codec h264)
         V..... h264_v4l2m2m         V4L2 mem2mem H.264 encoder wrapper (codec h264)
         V....D h264_vaapi           H.264/AVC (VAAPI) (codec h264)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    private const string RaspberryPi = """
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V..... h264_v4l2m2m         V4L2 mem2mem H.264 encoder wrapper (codec h264)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    private const string SoftwareOnly = """
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    private const string Rk3588 = """
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC (codec h264)
         V..... h264_rkmpp           Rockchip MPP H.264 encoder (codec h264)
         V..... h264_v4l2m2m         V4L2 mem2mem H.264 encoder wrapper (codec h264)
        """;

    private const string Filters = """
         ... anullsrc          |->A       Null audio source, return empty audio frames.
         T.C drawtext          V->V       Draw text on top of video frames using libfreetype library.
         ..C scale             V->V       Scale the input video size and/or convert the image format.
         TB.C overlay          VV->V      Overlay a video source on top of the input.
        """;

    [Test]
    public void ARockchipBoardUsesItsOwnEncoder() {
        // Both are present on an RK3588; the vendor one is the faster path.
        Assert.That(FfmpegEncoders.Choose(FfmpegEncoders.ParseEncoders(Rk3588), isArm: true),
            Is.EqualTo("h264_rkmpp"));
    }

    [Test]
    public void APiUsesTheKernelEncoderRatherThanSoftware() {
        Assert.That(FfmpegEncoders.Choose(FfmpegEncoders.ParseEncoders(RaspberryPi), isArm: true),
            Is.EqualTo("h264_v4l2m2m"));
    }

    [Test]
    public void OnX86TheQuickSyncPathWinsOverSoftware() {
        Assert.That(FfmpegEncoders.Choose(FfmpegEncoders.ParseEncoders(UbuntuX86), isArm: false),
            Is.EqualTo("h264_qsv"));
    }

    [Test]
    public void OnX86TheKernelEncoderLosesToSoftware() {
        // The stock Ubuntu x86 build reports h264_v4l2m2m whether or not the
        // machine has a kernel encoder. Picking it by name gets an encoder
        // that opens no device and dies on the first frame, which is exactly
        // how this was found.
        var noQsv = FfmpegEncoders.ParseEncoders("""
             V....D libx264              libx264 H.264 / AVC (codec h264)
             V..... h264_v4l2m2m         V4L2 mem2mem H.264 encoder wrapper (codec h264)
            """);
        Assert.That(FfmpegEncoders.Choose(noQsv, isArm: false), Is.EqualTo("libx264"));
        Assert.That(FfmpegEncoders.Choose(noQsv, isArm: true), Is.EqualTo("h264_v4l2m2m"));
    }

    [Test]
    public void AnEncoderWithNoDeviceNodeIsNotAnOption() {
        // Compiled in is not present, part two: an ARM board whose kernel
        // exposes no encoder node has to fall back to software as well.
        var pi = FfmpegEncoders.ParseEncoders(RaspberryPi);
        var withoutDevices = FfmpegEncoders.DropUnusable(pi, _ => false);
        Assert.That(FfmpegEncoders.Choose(withoutDevices, isArm: true), Is.EqualTo("libx264"));

        var withDevice = FfmpegEncoders.DropUnusable(pi, p => p == "/dev/video11");
        Assert.That(FfmpegEncoders.Choose(withDevice, isArm: true), Is.EqualTo("h264_v4l2m2m"));
    }

    [Test]
    public void WithNothingElseItIsSoftware() {
        Assert.That(FfmpegEncoders.Choose(FfmpegEncoders.ParseEncoders(SoftwareOnly), isArm: true),
            Is.EqualTo("libx264"));
    }

    [Test]
    public void AnEmptyOrUnreadableProbeStillYieldsAnEncoder() {
        // A build that reports nothing is not something Polaris can fix, and
        // failing at spawn with ffmpeg's own message beats refusing on a guess.
        Assert.That(FfmpegEncoders.Choose(FfmpegEncoders.ParseEncoders(null)), Is.EqualTo("libx264"));
        Assert.That(FfmpegEncoders.Choose(FfmpegEncoders.ParseEncoders("")), Is.EqualTo("libx264"));
        Assert.That(FfmpegEncoders.Choose(new List<string>()), Is.EqualTo("libx264"));
    }

    [Test]
    public void TheNameIsReadWhole() {
        // libx264rgb must not be mistaken for libx264, and the codec note in
        // the description must not be read as a name.
        var found = FfmpegEncoders.ParseEncoders(UbuntuX86);
        Assert.That(found, Does.Contain("libx264"));
        Assert.That(found, Does.Contain("libx264rgb"));
        Assert.That(found, Does.Contain("h264_vaapi"));
        Assert.That(found, Does.Not.Contain("H.264"));
        Assert.That(found, Does.Not.Contain("(codec"));
    }

    [Test]
    public void HardwareIsNamedAsSuch() {
        Assert.That(FfmpegEncoders.IsHardware("h264_v4l2m2m"), Is.True);
        Assert.That(FfmpegEncoders.IsHardware("h264_rkmpp"), Is.True);
        Assert.That(FfmpegEncoders.IsHardware("libx264"), Is.False);
        Assert.That(FfmpegEncoders.IsHardware(null), Is.False);
    }

    [Test]
    public void FiltersAreDetectedByName() {
        Assert.That(FfmpegEncoders.HasFilter(Filters, "drawtext"), Is.True);
        Assert.That(FfmpegEncoders.HasFilter(Filters, "overlay"), Is.True);
        Assert.That(FfmpegEncoders.HasFilter(Filters, "zoompan"), Is.False,
            "a build without it broadcasts the bare image instead of failing");
        Assert.That(FfmpegEncoders.HasFilter(null, "drawtext"), Is.False);
        Assert.That(FfmpegEncoders.HasFilter(Filters, "draw"), Is.False,
            "a prefix is not the filter");
    }
}
