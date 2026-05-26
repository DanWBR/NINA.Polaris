using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// Pure-function tests for the observation-score formula. Exercises the
/// edges that matter for an astrophotographer planning a night, clear
/// sky with great seeing should score near 100, anything with active
/// precipitation should hard-zero, and clear-but-dewy conditions should
/// drop heavily because dew on optics kills a session even under stars.
///
/// The HTTP / cache path of WeatherForecastService is intentionally not
/// covered here, it's a thin wrapper around HttpClient and an in-memory
/// dictionary; mocking HttpMessageHandler just to verify "didn't call
/// twice" is more ceremony than signal. We exercise the scoring math,
/// which is where the actual judgement lives.
/// </summary>
[TestFixture]
public class WeatherForecastServiceTests {
    private static SevenTimerSlot Slot(int cloud, int seeing, int trans,
                                       string? prec = "none", int rh = 0) {
        return new SevenTimerSlot {
            Cloudcover   = cloud,
            Seeing       = seeing,
            Transparency = trans,
            PrecType     = prec,
            Rh2m         = rh,
            Temp2m       = 15,
            Wind10m      = new SevenTimerWind { Speed = 2, Direction = "N" }
        };
    }

    [Test]
    public void ScoreSlot_ClearSky_GoodSeeing_ScoresHigh() {
        // cloud=1 (0–6%), seeing=2 (0.5–0.75"), trans=2 (great), no precip.
        var s = Slot(cloud: 1, seeing: 2, trans: 2);
        var score = WeatherForecastService.ScoreSlot(s);
        Assert.That(score, Is.GreaterThanOrEqualTo(85),
            "A clear sky with sub-arcsec seeing should score very high");
    }

    [Test]
    public void ScoreSlot_Overcast_NoPrecip_ScoresLow() {
        // cloud=9 (94–100%), best seeing/transparency, still cloudy.
        var s = Slot(cloud: 9, seeing: 1, trans: 1);
        var score = WeatherForecastService.ScoreSlot(s);
        Assert.That(score, Is.LessThan(50),
            "Heavy cloud cover should dominate even with perfect seeing");
    }

    [Test]
    public void ScoreSlot_AnyPrecipitation_HardZeros() {
        var s = Slot(cloud: 1, seeing: 1, trans: 1, prec: "rain");
        Assert.That(WeatherForecastService.ScoreSlot(s), Is.EqualTo(0),
            "Rain should zero the score regardless of other conditions");

        var snow = Slot(cloud: 1, seeing: 1, trans: 1, prec: "snow");
        Assert.That(WeatherForecastService.ScoreSlot(snow), Is.EqualTo(0));
    }

    [Test]
    public void ScoreSlot_HighHumidity_PenalisedHeavily() {
        // 7Timer rh2m bucket 16 ≈ ~99% humidity.
        var dry  = Slot(cloud: 1, seeing: 2, trans: 2, rh: 0);   // ~20%
        var dewy = Slot(cloud: 1, seeing: 2, trans: 2, rh: 16);  // ~99%
        var dryScore  = WeatherForecastService.ScoreSlot(dry);
        var dewyScore = WeatherForecastService.ScoreSlot(dewy);
        Assert.That(dewyScore, Is.LessThan(dryScore * 0.5),
            "Humidity > 95% should cut the score by at least half");
    }

    [Test]
    public void ScoreSlot_ClampsToZeroToOneHundred() {
        // Sanity: out-of-spec inputs should still land in [0, 100].
        var weird = Slot(cloud: 99, seeing: 99, trans: 99);
        var score = WeatherForecastService.ScoreSlot(weird);
        Assert.That(score, Is.InRange(0, 100));
    }

    [Test]
    public void ScoreSlot_PrecTypeNullOrEmpty_TreatedAsNone() {
        var sNull  = Slot(cloud: 1, seeing: 2, trans: 2, prec: null);
        var sEmpty = Slot(cloud: 1, seeing: 2, trans: 2, prec: "");
        Assert.That(WeatherForecastService.ScoreSlot(sNull),  Is.GreaterThan(80));
        Assert.That(WeatherForecastService.ScoreSlot(sEmpty), Is.GreaterThan(80));
    }
}
