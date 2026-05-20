using NUnit.Framework;
using NINA.Headless.Services;

namespace NINA.Headless.Test;

[TestFixture]
public class SkyCatalogServiceTests {
    private SkyCatalogService _sut = null!;

    [SetUp]
    public void SetUp() {
        _sut = new SkyCatalogService();
    }

    // --- Search by Messier designation ---

    [TestCase("M 31")]
    [TestCase("M 42")]
    [TestCase("M 1")]
    public void Search_ForMessierObject_ReturnsResult(string query) {
        var results = _sut.Search(query);

        Assert.That(results, Is.Not.Empty);
        Assert.That(results[0].Name, Is.EqualTo(query));
    }

    // --- Search by common name ---

    [TestCase("Andromeda Galaxy", "M 31")]
    [TestCase("Orion Nebula", "M 42")]
    [TestCase("Crab Nebula", "M 1")]
    public void Search_ForCommonName_ReturnsResult(string commonName, string expectedName) {
        var results = _sut.Search(commonName);

        Assert.That(results, Is.Not.Empty);
        Assert.That(results[0].Name, Is.EqualTo(expectedName));
    }

    // --- Case insensitivity ---

    [TestCase("m 31", "M 31")]
    [TestCase("M 31", "M 31")]
    [TestCase("andromeda galaxy", "M 31")]
    [TestCase("ANDROMEDA GALAXY", "M 31")]
    [TestCase("Orion Nebula", "M 42")]
    [TestCase("orion nebula", "M 42")]
    public void Search_CaseInsensitive_Works(string query, string expectedName) {
        var results = _sut.Search(query);

        Assert.That(results, Is.Not.Empty);
        Assert.That(results[0].Name, Is.EqualTo(expectedName));
    }

    // --- Partial match ---

    [Test]
    public void Search_Partial_FindsAndromeda() {
        var results = _sut.Search("Andro");

        Assert.That(results, Is.Not.Empty);
        Assert.That(results.Any(r => r.Name == "M 31"), Is.True,
            "Partial search 'Andro' should find Andromeda Galaxy (M 31)");
    }

    [Test]
    public void Search_Partial_FindsOrion() {
        var results = _sut.Search("Orion");

        Assert.That(results, Is.Not.Empty);
        Assert.That(results.Any(r => r.Name == "M 42"), Is.True,
            "Partial search 'Orion' should find Orion Nebula (M 42)");
    }

    [Test]
    public void Search_Partial_FindsCrab() {
        var results = _sut.Search("Crab");

        Assert.That(results, Is.Not.Empty);
        Assert.That(results.Any(r => r.Name == "M 1"), Is.True,
            "Partial search 'Crab' should find Crab Nebula (M 1)");
    }

    // --- GetByName exact match ---

    [Test]
    public void GetByName_ExactMatch_ReturnsObject() {
        var result = _sut.GetByName("M 31");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("M 31"));
        Assert.That(result.CommonName, Is.EqualTo("Andromeda Galaxy"));
    }

    [Test]
    public void GetByName_ByAlias_ReturnsObject() {
        var result = _sut.GetByName("NGC 224");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("M 31"));
    }

    [Test]
    public void GetByName_NotFound_ReturnsNull() {
        var result = _sut.GetByName("NONEXISTENT_OBJECT_XYZ");

        Assert.That(result, Is.Null);
    }

    // --- MaxResults ---

    [Test]
    public void Search_MaxResults_Respected() {
        // "M" should match many Messier objects; limit to 5
        var results = _sut.Search("NGC", maxResults: 5);

        Assert.That(results.Count, Is.LessThanOrEqualTo(5));
    }

    [Test]
    public void Search_MaxResults_DefaultReturnsUpTo20() {
        // Searching for something broad
        var results = _sut.Search("Cluster");

        Assert.That(results.Count, Is.LessThanOrEqualTo(20));
    }

    // --- All 110 Messier objects present ---

    [Test]
    public void AllMessierObjects_Present() {
        for (int i = 1; i <= 110; i++) {
            var name = $"M {i}";
            var result = _sut.GetByName(name);
            Assert.That(result, Is.Not.Null,
                $"Messier object {name} should be present in the catalog");
        }
    }

    // --- Coordinate validity ---

    [Test]
    public void Coordinates_InValidRange() {
        // Check all Messier objects have valid coordinates
        for (int i = 1; i <= 110; i++) {
            var name = $"M {i}";
            var obj = _sut.GetByName(name);
            Assert.That(obj, Is.Not.Null, $"{name} should exist");

            Assert.That(obj!.Ra, Is.GreaterThanOrEqualTo(0).And.LessThanOrEqualTo(24),
                $"{name} RA ({obj.Ra}) should be between 0 and 24 hours");
            Assert.That(obj.Dec, Is.GreaterThanOrEqualTo(-90).And.LessThanOrEqualTo(90),
                $"{name} Dec ({obj.Dec}) should be between -90 and +90 degrees");
        }
    }

    // --- Edge cases ---

    [Test]
    public void Search_EmptyQuery_ReturnsEmpty() {
        var results = _sut.Search("");

        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Search_WhitespaceQuery_ReturnsEmpty() {
        var results = _sut.Search("   ");

        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Search_NullQuery_ReturnsEmpty() {
        var results = _sut.Search(null!);

        Assert.That(results, Is.Empty);
    }
}
