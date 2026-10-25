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

using System.Text.Json;
using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

[TestFixture]
public class CloudForecastTests {
    // Trimmed from a real Open-Meteo answer (timezone=GMT, models=gfs_seamless).
    private const string Sample = """
        {"hourly":{
          "time":["2026-10-24T23:00","2026-10-25T00:00","2026-10-25T01:00"],
          "cloud_cover":[98,100,40],
          "cloud_cover_low":[1,0,null],
          "cloud_cover_mid":[10,20,5],
          "cloud_cover_high":[98,100,35],
          "precipitation_probability":[6,55,null],
          "relative_humidity_2m":[80,85,90],
          "dew_point_2m":[19.2,19.1,18.9],
          "temperature_2m":[22.0,21.0,19.5],
          "wind_speed_10m":[7.2,6.1,4.0]}}
        """;

    [Test]
    public void HoursAreReadAsUtcWithNullsKept() {
        var raw = JsonSerializer.Deserialize<CloudForecastService.OpenMeteoRoot>(Sample);
        var f = CloudForecastService.Parse(raw, "gfs_seamless");

        Assert.That(f.Available, Is.True);
        Assert.That(f.Hours, Has.Count.EqualTo(3));
        var first = f.Hours[0];
        Assert.Multiple(() => {
            Assert.That(first.UtcStart, Is.EqualTo(new DateTime(2026, 10, 24, 23, 0, 0, DateTimeKind.Utc)));
            Assert.That(first.UtcStart.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(first.CloudCover, Is.EqualTo(98));
            Assert.That(first.High, Is.EqualTo(98));
            Assert.That(f.Hours[1].PrecipProbability, Is.EqualTo(55));
            Assert.That(f.Hours[2].Low, Is.Null, "a model with no value for an hour sends null");
            Assert.That(f.Hours[2].PrecipProbability, Is.Null);
        });
    }

    [Test]
    public void AnEmptyAnswerIsUnavailable() {
        var f = CloudForecastService.Parse(new CloudForecastService.OpenMeteoRoot(), "best_match");
        Assert.That(f.Available, Is.False);
        Assert.That(f.Hours, Is.Empty);
    }
}
