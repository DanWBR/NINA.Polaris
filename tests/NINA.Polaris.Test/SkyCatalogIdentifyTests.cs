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

using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

[TestFixture]
public class SkyCatalogIdentifyTests {

    // Pointing used by most cases: 10h RA, +20° Dec.
    private const double RaH = 10.0;
    private const double DecD = 20.0;

    private static CatalogObject Obj(string name, double raH, double decD,
            double mag = 99, double? sizeArcmin = null, string? catalog = null) =>
        new() { Name = name, Ra = raH, Dec = decD, Magnitude = mag,
                SizeArcmin = sizeArcmin, Catalog = catalog };

    [Test]
    public void PickBest_SingleCentredObject_IsReturned() {
        var cands = new[] { Obj("NGC 1", RaH, DecD) };

        var best = SkyCatalogService.PickBest(RaH, DecD, 1.0, cands);

        Assert.That(best, Is.Not.Null);
        Assert.That(best!.Object.Name, Is.EqualTo("NGC 1"));
        Assert.That(best.WithinExtent, Is.True, "Centre sits on the object");
        Assert.That(best.SeparationArcmin, Is.EqualTo(0).Within(0.1));
    }

    [Test]
    public void PickBest_LargeEnclosingObject_BeatsNearerSmallOne() {
        // Small galaxy 0.3° off centre, tiny extent.
        var small = Obj("NGC 99", RaH, DecD + 0.3, sizeArcmin: 2);
        // Big nebula centre 0.8° off, but 3°-wide so the pointing is inside it.
        var big = Obj("Big Neb", RaH, DecD + 0.8, sizeArcmin: 180);

        var best = SkyCatalogService.PickBest(RaH, DecD, 0.5, new[] { small, big });

        Assert.That(best!.Object.Name, Is.EqualTo("Big Neb"),
            "Being inside the large object's extent should win over a nearer small one");
        Assert.That(best.WithinExtent, Is.True);
    }

    [Test]
    public void PickBest_NothingInRange_ReturnsNull() {
        var far = Obj("NGC 5000", RaH, DecD + 5.0);

        var best = SkyCatalogService.PickBest(RaH, DecD, 1.0, new[] { far });

        Assert.That(best, Is.Null);
    }

    [Test]
    public void PickBest_ProminenceBreaksNearTie_MessierWins() {
        // Two objects at the exact same off-centre position; only the catalog
        // tier differs. The Messier should win on the prominence bonus.
        var messier = Obj("M 51", RaH, DecD + 0.2, catalog: "M");
        var ngc     = Obj("NGC 5195", RaH, DecD + 0.2, catalog: "NGC");

        var best = SkyCatalogService.PickBest(RaH, DecD, 0.5, new[] { ngc, messier });

        Assert.That(best!.Object.Name, Is.EqualTo("M 51"));
    }

    [Test]
    public void PickBest_ProximityDominatesProminence() {
        // A faint obscure object dead-centre must beat a famous Messier near
        // the frame edge: proximity is the dominant term, prominence only a
        // tie-breaker.
        var centred = Obj("PGC 12345", RaH, DecD, catalog: "PGC");
        var edgeM   = Obj("M 1", RaH, DecD + 0.45, catalog: "M", mag: 5, sizeArcmin: 6);

        var best = SkyCatalogService.PickBest(RaH, DecD, 0.5, new[] { edgeM, centred });

        Assert.That(best!.Object.Name, Is.EqualTo("PGC 12345"));
    }

    [Test]
    public void PickBest_EmptyList_ReturnsNull() {
        Assert.That(SkyCatalogService.PickBest(RaH, DecD, 1.0, System.Array.Empty<CatalogObject>()),
            Is.Null);
    }
}
