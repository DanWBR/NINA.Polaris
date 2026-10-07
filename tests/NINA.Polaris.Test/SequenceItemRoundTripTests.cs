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
using System.Text.RegularExpressions;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// What survives the AUTORUN card list going to the host and back.
///
/// <para>The browser does not keep its own copy of the schedule. It replaces
/// its cards with whatever <c>GET /api/sequence</c> returns, on boot and on
/// reconnect, and posts that list back when the run starts. So a field the GET
/// projection leaves out is a setting the operator made and the run then
/// ignores, with nothing on screen saying so.</para>
///
/// <para>Reported from the field: Auto was ticked on a FLAT item and the flats
/// came out at the 60 s sitting in the exposure box, because <c>autoExposure</c>
/// was not in that projection. <c>enabled</c> was missing the same way and had
/// been papered over with a default in the browser, which is how the first
/// omission stayed invisible.</para>
/// </summary>
[TestFixture]
public class SequenceItemRoundTripTests {

    private static string Here([CallerFilePath] string p = "") => p;

    private static string Source(params string[] parts) {
        var here = Path.GetDirectoryName(Here())!;
        var all = new List<string> { here, "..", ".." };
        all.AddRange(parts);
        var path = Path.GetFullPath(Path.Combine(all.ToArray()));
        Assert.That(File.Exists(path), $"nao achei {path}");
        return File.ReadAllText(path);
    }

    /// <summary>The projection body of <c>GET /api/sequence</c>.</summary>
    private static string Projection() {
        var src = Source("src", "NINA.Polaris", "Endpoints", "SequenceEndpoints.cs");
        var start = src.IndexOf("items = engine.Items.Select(i => new {",
            System.StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), "the GET projection moved");
        var end = src.IndexOf("}),", start, System.StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(start));
        return src.Substring(start, end - start);
    }

    /// <summary>Every settable property of the item has to come back, or the
    /// next field added to the model is lost in exactly the same silence.</summary>
    [Test]
    public void TheGetProjectionCarriesEveryFieldOfTheItem() {
        var body = Projection();
        var missing = typeof(SequenceItem).GetProperties()
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .Where(name => !Regex.IsMatch(body, @"\bi\." + Regex.Escape(name) + @"\b"))
            .ToList();

        Assert.That(missing, Is.Empty,
            "GET /api/sequence drops " + string.Join(", ", missing)
            + ": the browser replaces its cards with this response, so whatever "
            + "is missing is a setting the operator loses");
    }

    /// <summary>The two that were actually lost, named so a regression says
    /// what broke rather than just "a field".</summary>
    [Test]
    public void AutoExposureAndEnabledComeBack() {
        var body = Projection();
        Assert.Multiple(() => {
            Assert.That(body, Does.Contain("i.AutoExposure"),
                "a FLAT item marked Auto was shot at the exposure in the box");
            Assert.That(body, Does.Contain("i.Enabled"));
        });
    }

    // ---- ISO on a DSLR -------------------------------------------------

    /// <summary>A DSLR over indi_gphoto has no analogue-gain property, so the
    /// gain the card sent landed nowhere at all. ISO is where its amplification
    /// lives and the item has to be able to carry one.</summary>
    [Test]
    public void TheItemCarriesAnIso_SeparateFromGain() {
        var item = new SequenceItem();

        Assert.Multiple(() => {
            Assert.That(item.Iso, Is.Null, "null means leave the camera where it is");
            Assert.That(item.Gain, Is.EqualTo(100), "the gain default is untouched");
        });

        item.Iso = 400;
        Assert.That(item.Iso, Is.EqualTo(400));
    }

    [Test]
    public void TheEngineSendsTheIsoWithTheExposure() {
        var src = Source("src", "NINA.Polaris", "Services", "SequenceEngine.cs");
        Assert.That(src, Does.Contain("Iso: item.Iso is > 0 ? item.Iso : null"),
            "the capture has to be told the ISO, and only when one was chosen");
    }

    /// <summary>CaptureOptions has had an Iso field all along and the INDI
    /// camera never read it, so even a caller that asked for one got nothing.</summary>
    [Test]
    public void TheIndiCameraAppliesTheRequestedIso() {
        var src = Source("src", "NINA.INDI", "Devices", "IndiCamera.cs");
        Assert.Multiple(() => {
            Assert.That(src, Does.Contain("opts?.Iso is int isoWanted"));
            Assert.That(src, Does.Contain("await TrySetIsoAsync(isoWanted, ct, force)"));
            Assert.That(src, Does.Contain("if (!force && SelectedIso == iso) return;"),
                "rewriting CCD_ISO before every frame is the per-frame "
                + "reconfiguration that wedges INDI drivers");
        });
    }

    /// <summary>The card offers whichever control the camera actually has.</summary>
    [Test]
    public void TheCardSwapsGainForIsoOnACameraThatUsesIso() {
        var html = Source("src", "NINA.Polaris", "wwwroot", "index.html");
        Assert.Multiple(() => {
            Assert.That(html, Does.Contain(@"<label x-show=""!cameraUsesIso()"">G<input"),
                "the numeric gain field hides on a DSLR");
            Assert.That(html, Does.Contain(@"<label x-show=""cameraUsesIso()"">ISO<select"));
            Assert.That(html, Does.Contain(@"item.iso = Number($event.target.value) || null"));
        });
    }
}
