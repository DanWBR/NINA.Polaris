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
/// The words on the object card: which description wins, and what has to be
/// said about where it came from.
/// </summary>
[TestFixture]
public class ObjectCardTextTests {

    private static ObjectCardFacts M42 => new() {
        CommonName = "Orion Nebula",
        CatalogId = "M42",
        Aliases = new[] { "NGC 1976", "Great Nebula in Orion" },
        Type = "Emission nebula",
        Constellation = "Orion",
        Magnitude = 4.0,
        SizeArcmin = 85
    };

    [Test]
    public void OurOwnTextWinsOverEverything() {
        var c = ObjectCardText.Compose(M42, bundled: "A stellar nursery 1,344 light years away.",
                                       fetched: "Something fetched from the web.");
        Assert.That(c.Description, Is.EqualTo("A stellar nursery 1,344 light years away."));
        Assert.That(c.Source, Is.EqualTo(ObjectNoteSource.Bundled));
        Assert.That(c.Credit, Is.Null, "our own text credits nobody");
    }

    [Test]
    public void FetchedTextIsUsedWhenWeHaveNoneOfOurOwn_AndIsCredited() {
        // The online text is published under a licence that requires
        // attribution, and a broadcast is publishing.
        var c = ObjectCardText.Compose(M42, bundled: null, fetched: "The nearest region of massive star formation.");
        Assert.That(c.Description, Does.StartWith("The nearest region"));
        Assert.That(c.Source, Is.EqualTo(ObjectNoteSource.Fetched));
        Assert.That(c.Credit, Is.EqualTo("via Wikipedia"));
    }

    [Test]
    public void WithNoTextAtAllTheFactsBecomeASentence() {
        var c = ObjectCardText.Compose(M42, null, null);
        Assert.That(c.Source, Is.EqualTo(ObjectNoteSource.Generated));
        Assert.That(c.Description, Is.EqualTo("Emission nebula in Orion, mag 4, 85'."));
        Assert.That(c.Credit, Is.Null);
    }

    [Test]
    public void BlankIsNotAText() {
        // An empty string in the bundle, or a fetch that returned nothing, must
        // fall through instead of blanking the card.
        var c = ObjectCardText.Compose(M42, bundled: "   ", fetched: "");
        Assert.That(c.Source, Is.EqualTo(ObjectNoteSource.Generated));
        Assert.That(c.Description, Is.Not.Empty);
    }

    [Test]
    public void TheTitleIsTheNamePeopleUse_AndTheIdsGoUnderneath() {
        var c = ObjectCardText.Compose(M42, null, null);
        Assert.That(c.Title, Is.EqualTo("Orion Nebula"));
        Assert.That(c.Subtitle, Does.Contain("M42"));
        Assert.That(c.Subtitle, Does.Contain("NGC 1976"));
    }

    [Test]
    public void TheTitleIsNotRepeatedInTheSubtitle() {
        var noCommonName = M42 with { CommonName = null };
        var c = ObjectCardText.Compose(noCommonName, null, null);
        Assert.That(c.Title, Is.EqualTo("M42"));
        Assert.That(c.Subtitle, Does.Not.Contain("M42"));
        Assert.That(c.Subtitle, Does.Contain("NGC 1976"));
    }

    [Test]
    public void AnObjectTheCatalogueDoesNotKnowStillGetsACard() {
        // A hand typed target: no catalogue row, nothing to say, but the
        // broadcast still needs something on screen.
        var c = ObjectCardText.Compose(null, null, null);
        Assert.That(c.Title, Is.EqualTo("Unnamed target"));
        Assert.That(c.Subtitle, Is.Empty);
        Assert.That(c.Facts, Is.Empty);
        Assert.That(c.Description, Is.Empty);
    }

    [Test]
    public void PartialFactsProduceAPartialSentence() {
        var sparse = new ObjectCardFacts { CatalogId = "NGC 7000", Constellation = "Cygnus" };
        var c = ObjectCardText.Compose(sparse, null, null);
        Assert.That(c.Description, Is.EqualTo("Cygnus."));
        Assert.That(c.Facts, Is.EqualTo(new[] { "Cygnus" }));
    }

    [Test]
    public void TheFactChipsAreFormattedForReadingAtADistance() {
        var c = ObjectCardText.Compose(M42 with { Magnitude = 4.04, SizeArcmin = 8.35 }, null, null);
        Assert.That(c.Facts, Does.Contain("mag 4"));
        Assert.That(c.Facts, Does.Contain("8.4'"), "small objects keep a decimal, big ones do not");
    }

    [Test]
    public void TheTemplatesAreSuppliedByTheCaller() {
        // The broadcast can be in a different language from the interface, so
        // nothing here reaches for a locale of its own.
        var pt = new ObjectCardTemplates {
            TypeInConstellation = "{0} em {1}",
            MagnitudeLabel = "mag {0}",
            SizeLabel = "{0}'",
            ViaWikipedia = "via Wikipédia",
            UnknownObject = "Alvo sem nome"
        };
        var c = ObjectCardText.Compose(M42, null, null, pt);
        Assert.That(c.Description, Is.EqualTo("Emission nebula em Orion, mag 4, 85'."));

        var fetched = ObjectCardText.Compose(M42, null, "texto", pt);
        Assert.That(fetched.Credit, Is.EqualTo("via Wikipédia"));

        var unknown = ObjectCardText.Compose(null, null, null, pt);
        Assert.That(unknown.Title, Is.EqualTo("Alvo sem nome"));
    }
}
