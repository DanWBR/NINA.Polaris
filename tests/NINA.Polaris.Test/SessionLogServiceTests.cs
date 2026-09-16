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


using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NINA.Polaris.Services.Studio;

namespace NINA.Polaris.Test;

/// <summary>The night log's grouping and calibration matching, on frames
/// built by hand: the evening-date rule, and which darks, bias and flats in
/// the library count for a set of lights.</summary>
[TestFixture]
public class SessionLogServiceTests {

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static NightLogFrame F(string type, string dateObs, string target = "M42", string filter = "L",
            double exp = 60, int gain = 100, int offset = 50, int bin = 1, string camera = "ASI2600MC Pro",
            double? temp = -10, double? rms = 0.8)
        => new(Path: type + dateObs, ImageType: type, Filter: filter, Target: target, ExposureSec: exp,
               Gain: gain, Offset: offset, Binning: bin, Bayer: "RGGB", DateObs: dateObs, DateLoc: "",
               Camera: camera, Telescope: "SV550", FocalLen: 683, CcdTemp: temp, GuideRms: rms, GuidePeak: null,
               FocusPos: 12000, FocusTemp: null, AmbientTemp: null, Humidity: null, PierSide: "East", SkyBrightness: null);

    [Test]
    public void NightKey_IsTheEveningDate() {
        Assert.That(SessionLogService.NightKeyOf("2026-09-14T22:30:00", "", Utc), Is.EqualTo("2026-09-14"));
        Assert.That(SessionLogService.NightKeyOf("2026-09-15T03:10:00", "", Utc), Is.EqualTo("2026-09-14"), "after midnight belongs to the evening before");
        Assert.That(SessionLogService.NightKeyOf("2026-09-15T13:00:00", "", Utc), Is.EqualTo("2026-09-15"), "afternoon starts the next night");
        Assert.That(SessionLogService.NightKeyOf("", "2026-09-15T01:00:00", Utc), Is.EqualTo("2026-09-14"), "DATE-LOC wins when present");
        Assert.That(SessionLogService.NightKeyOf("garbage", "", Utc), Is.EqualTo(""));
    }

    [Test]
    public void GroupByNight_SplitsAtLocalNoon() {
        var frames = new List<NightLogFrame> {
            F("LIGHT", "2026-09-14T23:00:00"), F("LIGHT", "2026-09-15T02:00:00"),
            F("LIGHT", "2026-09-15T22:00:00"),
        };
        var g = SessionLogService.GroupByNight(frames, Utc).OrderBy(x => x.Night).ToList();
        Assert.That(g.Select(x => x.Night), Is.EqualTo(new[] { "2026-09-14", "2026-09-15" }));
        Assert.That(g[0].Frames.Count, Is.EqualTo(2));
    }

    [Test]
    public void Calibration_MatchesDarksBiasAndFlatsByTheRightKeys() {
        var lights = new List<NightLogFrame> { F("LIGHT", "2026-09-14T23:00:00"), F("LIGHT", "2026-09-14T23:01:10") };
        var lib = new List<NightLogFrame>(lights) {
            F("DARK", "2026-09-10T12:00:00", temp: -10, rms: null),                 // same exposure/gain/offset/temp: counts
            F("DARK", "2026-09-10T12:01:00", temp: -10, rms: null, exp: 120),       // wrong exposure
            F("DARK", "2026-09-10T12:02:00", temp: 5, rms: null),                   // sensor 15 degrees warmer
            F("DARK", "2026-09-10T12:03:00", temp: -10, rms: null, gain: 0),        // wrong gain
            F("BIAS", "2026-09-10T12:04:00", exp: 0.001, temp: null, rms: null),    // counts
            F("BIAS", "2026-09-10T12:05:00", exp: 0.001, temp: null, rms: null, bin: 2),
            F("FLAT", "2026-09-14T18:30:00", exp: 2, rms: null),                    // same night, same filter: counts
            F("FLAT", "2026-09-12T18:30:00", exp: 2, rms: null),                    // older night
            F("FLAT", "2026-09-14T18:31:00", exp: 2, rms: null, filter: "Ha"),      // other filter
        };
        var m = SessionLogService.MatchCalibration(lights, lib, "2026-09-14", Utc);
        Assert.That(m.Count, Is.EqualTo(1));
        Assert.That(m[0].Lights, Is.EqualTo(2));
        Assert.That(m[0].Darks, Is.EqualTo(1));
        Assert.That(m[0].DarksNights, Is.EqualTo(new[] { "2026-09-10" }));
        Assert.That(m[0].Biases, Is.EqualTo(1));
        Assert.That(m[0].Flats, Is.EqualTo(1));
        Assert.That(m[0].FlatsNight, Is.EqualTo("2026-09-14"));
    }

    [Test]
    public void Calibration_FallsBackToTheNearestNightOfFlats() {
        var lights = new List<NightLogFrame> { F("LIGHT", "2026-09-14T23:00:00") };
        var lib = new List<NightLogFrame>(lights) {
            F("FLAT", "2026-09-01T18:30:00", exp: 2, rms: null),
            F("FLAT", "2026-09-12T18:30:00", exp: 2, rms: null),
            F("FLAT", "2026-09-12T18:31:00", exp: 2, rms: null),
        };
        var m = SessionLogService.MatchCalibration(lights, lib, "2026-09-14", Utc);
        Assert.That(m[0].Flats, Is.EqualTo(2));
        Assert.That(m[0].FlatsNight, Is.EqualTo("2026-09-12"));
        Assert.That(m[0].Darks, Is.EqualTo(0));
    }
}
