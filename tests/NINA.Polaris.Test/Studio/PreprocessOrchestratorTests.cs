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

using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace NINA.Polaris.Test.Studio;

/// <summary>
/// The orchestrator's one decidable, isolatable rule is the master pass-through
/// gate: a calibration slot holding a single frame whose FITS IMAGETYP is
/// already a master is used as-is (no rebuild). Everything else in the pipeline
/// is I/O wiring around the existing single-stage services, covered by the
/// end-to-end manual verification (same stance as CalibrationServiceTests).
/// </summary>
[TestFixture]
public class PreprocessOrchestratorTests {
    private static readonly MethodInfo IsMasterFits = Type
        .GetType("NINA.Polaris.Services.Studio.PreprocessOrchestrator, NINA.Polaris")!
        .GetMethod("IsMasterFits", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool IsMaster(string path) => (bool)IsMasterFits.Invoke(null, new object[] { path })!;

    private string _dir = "";

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "polaris-prep-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    // Minimal single-block FITS primary header carrying just the IMAGETYP we
    // want to probe. ReadHeadersOnly reads one 2880-byte block of 80-char cards.
    private string WriteFits(string name, string imageType) {
        string Card(string kw, string val) {
            var body = kw.PadRight(8) + "= " + val;
            return (body.Length > 80 ? body[..80] : body).PadRight(80);
        }
        var sb = new StringBuilder();
        sb.Append(Card("SIMPLE", "T"));
        sb.Append(Card("BITPIX", "16"));
        sb.Append(Card("NAXIS", "0"));
        sb.Append(Card("IMAGETYP", "'" + imageType + "'"));
        sb.Append("END".PadRight(80));
        var block = sb.ToString().PadRight(2880);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(block));
        return path;
    }

    [Test]
    public void MasterDark_IsRecognisedAsMaster() {
        Assert.That(IsMaster(WriteFits("md.fits", "MASTERDARK")), Is.True);
    }

    [Test]
    public void MasterFlatAndBias_AreRecognisedAsMaster() {
        Assert.That(IsMaster(WriteFits("mf.fits", "MASTERFLAT")), Is.True);
        Assert.That(IsMaster(WriteFits("mb.fits", "MASTERBIAS")), Is.True);
    }

    [Test]
    public void RawLightOrDark_IsNotAMaster() {
        Assert.That(IsMaster(WriteFits("light.fits", "LIGHT")), Is.False);
        Assert.That(IsMaster(WriteFits("dark.fits", "DARK")), Is.False);
    }

    [Test]
    public void MissingFile_IsNotAMaster_AndDoesNotThrow() {
        Assert.That(IsMaster(Path.Combine(_dir, "nope.fits")), Is.False);
    }
}
