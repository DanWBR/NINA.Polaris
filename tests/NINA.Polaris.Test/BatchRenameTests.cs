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

using Microsoft.Extensions.Logging.Abstractions;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the STUDIO batch-rename contract: the pure template engine
/// (<see cref="BatchRenameTemplate"/>) and the filesystem-touching
/// <see cref="FileBrowserService.BatchRenameAsync"/> (header read,
/// sanitisation, collision auto-suffix, non-FITS skip, dry-run).
/// </summary>
[TestFixture]
public class BatchRenameTests {

    // ----- Pure template engine -----

    private static Dictionary<string, string> Values(params (string, string)[] kv) {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    [Test]
    public void Apply_SubstitutesKeywords_CaseInsensitive() {
        var v = Values(("OBJECT", "NGC7000"), ("FILTER", "Ha"));
        Assert.That(BatchRenameTemplate.Apply("{object}_{FILTER}", v, 1),
            Is.EqualTo("NGC7000_Ha"));
    }

    [Test]
    public void Apply_MissingKeyword_BecomesEmpty() {
        var v = Values(("OBJECT", "M31"));
        Assert.That(BatchRenameTemplate.Apply("{OBJECT}_{NOPE}", v, 1),
            Is.EqualTo("M31_"));
    }

    [Test]
    public void Apply_Counter_NoPadAndPadded() {
        var v = Values();
        Assert.That(BatchRenameTemplate.Apply("img_{n}", v, 7), Is.EqualTo("img_7"));
        Assert.That(BatchRenameTemplate.Apply("img_{n:03}", v, 7), Is.EqualTo("img_007"));
        Assert.That(BatchRenameTemplate.Apply("img_{N:3}", v, 42), Is.EqualTo("img_042"));
    }

    [Test]
    public void Apply_LiteralTextPassesThrough() {
        var v = Values(("FILTER", "OIII"));
        Assert.That(BatchRenameTemplate.Apply("Light-{FILTER}-sub", v, 1),
            Is.EqualTo("Light-OIII-sub"));
    }

    [Test]
    public void Apply_EmptyTemplate_ReturnsEmpty() {
        Assert.That(BatchRenameTemplate.Apply("", Values(), 1), Is.EqualTo(""));
    }

    // ----- Service: header read + rename on disk -----

    private string _tmp = "";
    private FileBrowserService _svc = null!;

    [SetUp]
    public void SetUp() {
        _tmp = Path.Combine(Path.GetTempPath(), "polaris_batchrename_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _svc = new FileBrowserService(NullLogger<FileBrowserService>.Instance);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string WriteFits(string name, params (string key, string val)[] headers) {
        var data = new ushort[] { 1, 2, 3, 4 };
        var props = new ImageProperties { Width = 2, Height = 2, BitDepth = 16, Channels = 1 };
        var img = new BaseImageData(data, props);
        var path = Path.Combine(_tmp, name);
        var custom = headers.Select(h => new KeyValuePair<string, string>(h.key, h.val));
        FITSWriter.Write(img, path, customKeywords: custom);
        return path;
    }

    [Test]
    public async Task BatchRename_AppliesTemplateFromHeaders() {
        var a = WriteFits("raw1.fits", ("OBJECT", "NGC7000"), ("FILTER", "Ha"));
        var b = WriteFits("raw2.fits", ("OBJECT", "NGC7000"), ("FILTER", "Ha"));

        var r = await _svc.BatchRenameAsync([a, b], "{OBJECT}_{FILTER}_{n:03}",
            dryRun: false, CancellationToken.None);

        Assert.That(r.WillRename, Is.EqualTo(2));
        Assert.That(File.Exists(Path.Combine(_tmp, "NGC7000_Ha_001.fits")), Is.True);
        Assert.That(File.Exists(Path.Combine(_tmp, "NGC7000_Ha_002.fits")), Is.True);
        Assert.That(File.Exists(a), Is.False);
    }

    [Test]
    public async Task BatchRename_CollisionGetsAutoSuffix() {
        var a = WriteFits("raw1.fits", ("OBJECT", "M42"), ("FILTER", "L"));
        var b = WriteFits("raw2.fits", ("OBJECT", "M42"), ("FILTER", "L"));

        // No counter → both map to the same name; the 2nd must be suffixed.
        var r = await _svc.BatchRenameAsync([a, b], "{OBJECT}_{FILTER}",
            dryRun: false, CancellationToken.None);

        Assert.That(r.Conflicts, Is.EqualTo(1));
        Assert.That(File.Exists(Path.Combine(_tmp, "M42_L.fits")), Is.True);
        Assert.That(File.Exists(Path.Combine(_tmp, "M42_L_1.fits")), Is.True);
    }

    [Test]
    public async Task BatchRename_DryRun_DoesNotTouchDisk() {
        var a = WriteFits("raw1.fits", ("OBJECT", "M81"), ("FILTER", "G"));

        var r = await _svc.BatchRenameAsync([a], "{OBJECT}_{FILTER}",
            dryRun: true, CancellationToken.None);

        Assert.That(r.Items[0].Status, Is.EqualTo("preview"));
        Assert.That(r.Items[0].NewName, Is.EqualTo("M81_G.fits"));
        Assert.That(File.Exists(a), Is.True, "dry-run must not move the file");
        Assert.That(File.Exists(Path.Combine(_tmp, "M81_G.fits")), Is.False);
    }

    [Test]
    public async Task BatchRename_SkipsNonFits() {
        var txt = Path.Combine(_tmp, "notes.txt");
        File.WriteAllText(txt, "hello");

        var r = await _svc.BatchRenameAsync([txt], "{OBJECT}_{n}",
            dryRun: false, CancellationToken.None);

        Assert.That(r.Items[0].Status, Is.EqualTo("skipped"));
        Assert.That(File.Exists(txt), Is.True);
    }

    [Test]
    public void BatchRename_EmptyTemplate_Throws() {
        Assert.That(async () => await _svc.BatchRenameAsync(["x.fits"], "  ",
            dryRun: true, CancellationToken.None), Throws.ArgumentException);
    }
}
