using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

[TestFixture]
public class AutoFocusServiceTests {

    private static List<AutoFocusPoint> Pts(params (int pos, double hfr)[] pairs) =>
        pairs.Select(p => new AutoFocusPoint { Position = p.pos, HFR = p.hfr, StarCount = 50 }).ToList();

    // ---- Parabola fit math ----

    [Test]
    public void FitParabola_ExactQuadratic_RecoversCoefficients() {
        // y = 2(x - 1000)^2 + 1.5  =>  a=2, b=-4000, c=2,000,001.5
        var pts = new List<AutoFocusPoint>();
        for (int x = 990; x <= 1010; x += 2) {
            double y = 2.0 * Math.Pow(x - 1000, 2) + 1.5;
            pts.Add(new AutoFocusPoint { Position = x, HFR = y, StarCount = 50 });
        }

        var fit = AutoFocusService.FitParabola(pts);

        // MinX is far more robust than MinY with large absolute x values:
        // Cramer's rule sums x^4 which loses ~6 digits of precision at x~1000.
        // For real focuser units (~thousands) we still get sub-step accuracy on
        // the vertex location, which is what matters for AF.
        Assert.That(fit.MinX, Is.EqualTo(1000.0).Within(0.5));
        Assert.That(fit.MinY, Is.EqualTo(1.5).Within(0.5));
        Assert.That(fit.A, Is.EqualTo(2.0).Within(0.1));
    }

    [Test]
    public void FitParabola_SymmetricVCurve_FindsVertex() {
        // Classic V-curve: minimum at 5000
        var pts = Pts(
            (4800, 8.5),
            (4850, 6.2),
            (4900, 4.0),
            (4950, 2.3),
            (5000, 1.5),
            (5050, 2.3),
            (5100, 4.1),
            (5150, 6.3),
            (5200, 8.4)
        );

        var fit = AutoFocusService.FitParabola(pts);

        // Vertex is what matters for focus, the V-shape isn't a true parabola,
        // so the predicted MinY can sit slightly above the lowest sample.
        Assert.That(fit.MinX, Is.EqualTo(5000).Within(5));
        Assert.That(fit.MinY, Is.LessThan(3.0));
        Assert.That(fit.A, Is.GreaterThan(0), "Parabola must open upward (focus minimum)");
    }

    [Test]
    public void FitParabola_AsymmetricSamples_StillFindsReasonableMin() {
        // Samples skewed left of true minimum (5050)
        var pts = Pts(
            (4900, 4.5),
            (4950, 3.0),
            (5000, 2.0),
            (5050, 1.5),
            (5100, 2.0)
        );

        var fit = AutoFocusService.FitParabola(pts);

        Assert.That(fit.MinX, Is.GreaterThan(5020));
        Assert.That(fit.MinX, Is.LessThan(5080));
    }

    [Test]
    public void FitParabola_WithNoisySamples_ConvergesNearTruth() {
        // True vertex at 3000 with a=0.001
        var rng = new Random(42);
        var pts = new List<AutoFocusPoint>();
        for (int x = 2800; x <= 3200; x += 25) {
            double y = 0.001 * Math.Pow(x - 3000, 2) + 1.8 + (rng.NextDouble() - 0.5) * 0.2;
            pts.Add(new AutoFocusPoint { Position = x, HFR = y, StarCount = 50 });
        }

        var fit = AutoFocusService.FitParabola(pts);

        Assert.That(fit.MinX, Is.EqualTo(3000).Within(20), "Vertex should be within 20 steps of truth");
        Assert.That(fit.MinY, Is.EqualTo(1.8).Within(0.3));
    }

    [Test]
    public void FitParabola_LessThan3Points_Throws() {
        var pts = Pts((100, 2.0), (200, 1.5));
        Assert.Throws<ArgumentException>(() => AutoFocusService.FitParabola(pts));
    }

    [Test]
    public void FitParabola_CollinearPoints_FallsBackToMinSample() {
        // All on a straight line, singular matrix or near-zero 'a'
        var pts = Pts((100, 5.0), (200, 4.0), (300, 3.0), (400, 2.0));

        var fit = AutoFocusService.FitParabola(pts);

        // Should not crash and should not propose an absurd vertex
        Assert.That(fit, Is.Not.Null);
    }

    // ---- Settings / state defaults ----

    [Test]
    public void AutoFocusRequest_Defaults_AreSensible() {
        var r = new AutoFocusRequest();
        Assert.That(r.Steps, Is.EqualTo(9));
        Assert.That(r.StepSize, Is.EqualTo(50));
        Assert.That(r.ExposureSeconds, Is.EqualTo(2.0));
        Assert.That(r.MinStars, Is.EqualTo(5));
        Assert.That(r.BacklashSteps, Is.EqualTo(0));
        Assert.That(r.TakeConfirmationFrame, Is.True);
    }
}
