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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// ISO on DSLR frames, from the field with a Canon R8 on indi_gphoto
/// (2026-10-11): the FITS carried no ISO at all, and a GAIN=100 that was only
/// the capture panel's gain field; and the LIVE tab showed a Gain field for a
/// camera set by ISO, because the ISO list that switches the panel over was
/// only fetched from the PREVIEW and VIDEO buttons.
/// </summary>
[TestFixture]
public class DslrIsoMetadataTests {

    private static string Header(ImageMetaData meta, out BaseImageData back) {
        var data = new BaseImageData(new ushort[16], new ImageProperties { Width = 4, Height = 4, BitDepth = 16 }, meta);
        var path = Path.Combine(Path.GetTempPath(), "polaris-iso-" + System.Guid.NewGuid().ToString("N") + ".fits");
        try {
            FITSWriter.Write(data, path);
            var bytes = File.ReadAllBytes(path);
            back = FITSReader.Read(bytes);
            return System.Text.Encoding.ASCII.GetString(bytes.Take(14400).ToArray());
        } finally {
            try { File.Delete(path); } catch { }
        }
    }

    [Test]
    public void IsoIsWrittenAsIsospeed_AndReadBack() {
        var meta = new ImageMetaData();
        meta.Camera.Name = "GPhoto CCD";
        meta.Camera.Iso = 1600;
        var header = Header(meta, out var back);
        Assert.Multiple(() => {
            Assert.That(header, Does.Contain("ISOSPEED= ").And.Contain("1600"));
            Assert.That(header, Does.Not.Contain("GAIN    ="), "no gain was set, so no GAIN card");
            Assert.That(back.MetaData.Camera.Iso, Is.EqualTo(1600));
        });
    }

    [Test]
    public void AnAstroCameraGetsNoIsospeed() {
        var meta = new ImageMetaData();
        meta.Camera.Name = "ZWO ASI2600MC";
        meta.Camera.Gain = 100;
        var header = Header(meta, out _);
        Assert.Multiple(() => {
            Assert.That(header, Does.Not.Contain("ISOSPEED"));
            Assert.That(header, Does.Contain("GAIN    ="));
        });
    }

    // ---- where the ISO comes from, and the stale GAIN that goes ----

    private static string Here([CallerFilePath] string p = "") => p;

    private static string Src(params string[] parts) {
        var all = new List<string> { Path.GetDirectoryName(Here())!, "..", "..", "src" };
        all.AddRange(parts);
        var path = Path.GetFullPath(Path.Combine(all.ToArray()));
        Assert.That(File.Exists(path), $"missing {path}");
        return File.ReadAllText(path);
    }

    [Test]
    public void TheGphotoCaptureStampsTheIsoTheDriverReports() {
        var cam = Src("NINA.INDI", "Devices", "IndiCamera.cs");
        Assert.That(System.Text.RegularExpressions.Regex.Matches(cam, @"MetaData\.Camera\.Iso = SelectedIso;").Count,
            Is.EqualTo(2), "both the BLOB path and the local-file path");
    }

    [Test]
    public void TheCallersGainIsNotStampedOnAnIsoCamera() {
        var writer = Src("NINA.Polaris", "Services", "ImageWriterService.cs");
        Assert.Multiple(() => {
            Assert.That(writer, Does.Contain("if (gain > 0 && isoCam == null) m.Camera.Gain = gain;"));
            Assert.That(writer, Does.Contain("m.Camera.Iso = isoCam.SelectedIso;"));
        });
    }

    [Test]
    public void LiveSendsTheIso_AndTheServerPassesItOn() {
        var js = Src("NINA.Polaris", "wwwroot", "js", "app.js");
        Assert.Multiple(() => {
            Assert.That(System.Text.RegularExpressions.Regex.Matches(js, @"iso: this\.liveIso\(\),").Count,
                Is.EqualTo(2), "single capture and the loop");
            Assert.That(Src("NINA.Polaris", "Services", "LiveCaptureService.cs"), Does.Contain("Iso: Iso,"));
            Assert.That(Src("NINA.Polaris", "Endpoints", "CameraEndpoints.cs"),
                Does.Contain("Iso: request.Iso is > 0 ? request.Iso : null,"));
        });
    }

    /// <summary>The list that turns Gain into ISO is fetched from the status
    /// feed, not only from the PREVIEW and VIDEO buttons.</summary>
    [Test]
    public void TheIsoListIsFetchedWhicheverTabIsOpen() {
        var js = Src("NINA.Polaris", "wwwroot", "js", "app.js");
        var at = js.IndexOf("if (camConn && isoNow && !(this.cameraIso.options || []).length", System.StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0));
        Assert.That(js.Substring(at, 300), Does.Contain("this.loadCameraCapabilities();"));
    }
}
