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

using NINA.Polaris.Services;
using NINA.Polaris.Services.Focus;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// The per-filter focus run: measure every selected filter once, show the
/// operator what it found, and write the offsets only when they say so.
///
/// <para>Every rule the run follows lives in <see cref="FilterSweepMath"/> so it
/// can be tested without a wheel, a focuser or a sky. The one test that matters
/// most is <c>Write_OffsetsMatchThePreview</c>: the table the operator approves
/// has to be the table that gets stored, and the offset recomputation picks its
/// own reference filter, so the two can disagree unless Apply pins it.</para>
/// </summary>
[TestFixture]
public class FilterFocusSweepTests {
    private static readonly string[] Wheel = { "L", "R", "G", "B", "Ha" };

    private static AutoFocusResult Ok(int final, double hfr = 2.5, double r2 = 0.95,
                                      int best = 0, int? stars = 40) => new() {
        Success = true,
        FinalPosition = final,
        BestPosition = best == 0 ? final : best,
        FinalMeasuredHfr = hfr,
        BestPredictedHfr = hfr,
        RSquared = r2,
        FinalStarCount = stars,
        Method = "TRENDHYPERBOLIC",
        CompletedAt = new DateTime(2026, 9, 22, 22, 0, 0, DateTimeKind.Utc)
    };

    private static FilterFocusSweepResult Measured(string filter, int pos, double temp) =>
        FilterSweepMath.BuildResult(filter, Ok(pos), null, false, 1, temp, "Foc-A", 0.7);

    // ---- PlanFilters ----

    [Test]
    public void PlanFilters_NoRequest_TakesEveryFilterOnTheWheel() {
        Assert.That(FilterSweepMath.PlanFilters(Wheel, null), Is.EqualTo(Wheel));
    }

    [Test]
    public void PlanFilters_KeepsWheelOrderNotTheOrderAsked() {
        // The wheel then turns one way through the set instead of jumping back
        // and forth across the slots.
        var plan = FilterSweepMath.PlanFilters(Wheel, new[] { "Ha", "L", "G" });
        Assert.That(plan, Is.EqualTo(new[] { "L", "G", "Ha" }));
    }

    [Test]
    public void PlanFilters_IsCaseInsensitive() {
        Assert.That(FilterSweepMath.PlanFilters(Wheel, new[] { "ha" }), Is.EqualTo(new[] { "Ha" }));
    }

    [Test]
    public void PlanFilters_EmptyWheel_Throws() {
        var ex = Assert.Throws<ArgumentException>(
            () => FilterSweepMath.PlanFilters(Array.Empty<string>(), null));
        Assert.That(ex!.Message, Does.Contain("has not published"));
    }

    [Test]
    public void PlanFilters_UnknownFilter_ThrowsNamingIt() {
        var ex = Assert.Throws<ArgumentException>(
            () => FilterSweepMath.PlanFilters(Wheel, new[] { "L", "OIII" }));
        Assert.That(ex!.Message, Does.Contain("OIII"));
    }

    [Test]
    public void PlanFilters_DuplicateFilter_Throws() {
        Assert.Throws<ArgumentException>(
            () => FilterSweepMath.PlanFilters(Wheel, new[] { "L", "L" }));
    }

    [Test]
    public void PlanFilters_OnlyBlankNames_Throws() {
        Assert.Throws<ArgumentException>(
            () => FilterSweepMath.PlanFilters(Wheel, new[] { "", "  " }));
    }

    // ---- BuildResult ----

    [Test]
    public void BuildResult_Success_IsMeasuredAndCarriesTheRun() {
        var row = FilterSweepMath.BuildResult("L", Ok(1000, 2.2, 0.98, best: 1002),
                                              null, false, 1, 9.5, "Foc-A", 0.7);
        Assert.Multiple(() => {
            Assert.That(row.Measured, Is.True);
            Assert.That(row.LowQuality, Is.False);
            Assert.That(row.Position, Is.EqualTo(1000));
            Assert.That(row.BestPosition, Is.EqualTo(1002));
            Assert.That(row.Hfr, Is.EqualTo(2.2));
            Assert.That(row.RSquared, Is.EqualTo(0.98));
            Assert.That(row.TemperatureC, Is.EqualTo(9.5));
            Assert.That(row.FocuserName, Is.EqualTo("Foc-A"));
            Assert.That(row.MeasuredAtUtc, Is.EqualTo(new DateTime(2026, 9, 22, 22, 0, 0, DateTimeKind.Utc)));
            Assert.That(row.Error, Is.Null);
        });
    }

    [Test]
    public void BuildResult_NoResult_IsAFailure() {
        var row = FilterSweepMath.BuildResult("L", null, null, false, 2, 9.5, "Foc-A", 0.7);
        Assert.That(row.Measured, Is.False);
        Assert.That(row.Error, Does.Contain("no result"));
    }

    [Test]
    public void BuildResult_Timeout_SaysSo() {
        var row = FilterSweepMath.BuildResult("L", null, "auto-focus did not finish within 20 minutes",
                                              true, 1, 9.5, "Foc-A", 0.7);
        Assert.That(row.Measured, Is.False);
        Assert.That(row.Error, Does.Contain("20 minutes"));
    }

    [Test]
    public void BuildResult_AutoFocusFailed_CarriesTheServiceError() {
        var failed = new AutoFocusResult { Success = false, Error = "no stars in frame" };
        var row = FilterSweepMath.BuildResult("Ha", failed, null, false, 2, 9.5, "Foc-A", 0.7);
        Assert.That(row.Measured, Is.False);
        Assert.That(row.Error, Is.EqualTo("no stars in frame"));
    }

    [Test]
    public void BuildResult_PoorFitWithTheGateDisabled_IsMeasuredButFlagged() {
        // The only way a bad fit arrives as a success: the rig set its own
        // R-squared threshold to 0, which disables the check inside autofocus.
        var row = FilterSweepMath.BuildResult("Ha", Ok(1000, 3.0, r2: 0.41),
                                              null, false, 1, 9.5, "Foc-A",
                                              FilterFocusSweepService.DefaultRSquaredGate);
        Assert.Multiple(() => {
            Assert.That(row.Measured, Is.True);
            Assert.That(row.LowQuality, Is.True);
            Assert.That(row.Warning, Does.Contain("0.41"));
        });
    }

    // ---- ShouldRetry ----

    [Test]
    public void ShouldRetry_FailedFirstAttempt_Retries() {
        var row = FilterSweepMath.BuildResult("L", null, null, false, 1, 9, "Foc-A", 0.7);
        Assert.That(FilterSweepMath.ShouldRetry(row, 1, 2), Is.True);
    }

    [Test]
    public void ShouldRetry_FailedSecondAttempt_GivesUp() {
        var row = FilterSweepMath.BuildResult("L", null, null, false, 2, 9, "Foc-A", 0.7);
        Assert.That(FilterSweepMath.ShouldRetry(row, 2, 2), Is.False);
    }

    [Test]
    public void ShouldRetry_Measured_NeverRetries() {
        Assert.That(FilterSweepMath.ShouldRetry(Measured("L", 1000, 9), 1, 2), Is.False);
    }

    [Test]
    public void ShouldRetry_LowQuality_NeverRetries() {
        // Repeating it costs minutes for a marginal gain; the tab can re-run it.
        var row = FilterSweepMath.BuildResult("Ha", Ok(1000, 3.0, r2: 0.41),
                                              null, false, 1, 9, "Foc-A", 0.7);
        Assert.That(FilterSweepMath.ShouldRetry(row, 1, 2), Is.False);
    }

    // ---- ResolveSweepReference ----

    [Test]
    public void Reference_PrefersTheRigsConfiguredFilterWhenItWasMeasured() {
        var rows = new[] { Measured("L", 1000, 9), Measured("R", 1040, 9) };
        Assert.That(FilterSweepMath.ResolveSweepReference(rows, "R"), Is.EqualTo("R"));
    }

    [Test]
    public void Reference_FallsBackWhenTheConfiguredFilterFailed() {
        var rows = new[] {
            Measured("L", 1000, 9),
            FilterSweepMath.BuildResult("R", null, null, false, 2, 9, "Foc-A", 0.7)
        };
        Assert.That(FilterSweepMath.ResolveSweepReference(rows, "R"), Is.EqualTo("L"));
    }

    [Test]
    public void Reference_PrefersLWhenNothingIsConfigured() {
        var rows = new[] { Measured("R", 1040, 9), Measured("L", 1000, 9) };
        Assert.That(FilterSweepMath.ResolveSweepReference(rows, null), Is.EqualTo("L"));
    }

    [Test]
    public void Reference_WithoutLTakesTheFirstMeasuredInSweepOrder() {
        var rows = new[] { Measured("R", 1040, 9), Measured("B", 975, 9) };
        Assert.That(FilterSweepMath.ResolveSweepReference(rows, null), Is.EqualTo("R"));
    }

    [Test]
    public void Reference_NothingMeasured_IsNull() {
        var rows = new[] { FilterSweepMath.BuildResult("L", null, null, false, 2, 9, "Foc-A", 0.7) };
        Assert.That(FilterSweepMath.ResolveSweepReference(rows, "L"), Is.Null);
    }

    // ---- DeriveOffsets (the preview) ----

    [Test]
    public void Offsets_AreRelativeToTheReferenceWhichIsZero() {
        var rows = new List<FilterFocusSweepResult> {
            Measured("L", 1000, 9), Measured("R", 1040, 9), Measured("B", 975, 9)
        };
        FilterSweepMath.DeriveOffsets(rows, "L", 1.5);
        Assert.Multiple(() => {
            Assert.That(rows[0].Offset, Is.EqualTo(0));
            Assert.That(rows[1].Offset, Is.EqualTo(40));
            Assert.That(rows[2].Offset, Is.EqualTo(-25));
            Assert.That(rows.All(r => r.OffsetDerived), Is.True);
        });
    }

    [Test]
    public void Offsets_OutsideTheTemperatureWindow_AreShownButNotDerived() {
        // An eight-filter run on a cooling night can put its late filters
        // outside the tolerance, and RecomputeOffsets refuses to store those.
        // The preview has to say so instead of promising a number.
        var rows = new List<FilterFocusSweepResult> {
            Measured("L", 1000, 9), Measured("Ha", 1120, 20)
        };
        FilterSweepMath.DeriveOffsets(rows, "L", 1.5);
        Assert.Multiple(() => {
            Assert.That(rows[1].Offset, Is.EqualTo(120), "the measured delta is still shown");
            Assert.That(rows[1].OffsetDerived, Is.False);
            Assert.That(rows[1].OffsetWarning, Does.Contain("1.5"));
        });
    }

    [Test]
    public void Offsets_AFailedFilterHasNone() {
        var rows = new List<FilterFocusSweepResult> {
            Measured("L", 1000, 9),
            FilterSweepMath.BuildResult("Ha", null, null, false, 2, 9, "Foc-A", 0.7)
        };
        FilterSweepMath.DeriveOffsets(rows, "L", 1.5);
        Assert.That(rows[1].Offset, Is.Null);
        Assert.That(rows[1].OffsetDerived, Is.False);
    }

    [Test]
    public void Offsets_UnknownReference_LeavesEverythingUnderived() {
        var rows = new List<FilterFocusSweepResult> { Measured("L", 1000, 9) };
        FilterSweepMath.DeriveOffsets(rows, "OIII", 1.5);
        Assert.That(rows[0].Offset, Is.Null);
    }

    [Test]
    public void Offsets_AreRecomputedFromScratchOnEveryCall() {
        // The run derives them again after each filter, so a stale value from
        // an earlier reference must not survive.
        var rows = new List<FilterFocusSweepResult> { Measured("L", 1000, 9), Measured("R", 1040, 9) };
        FilterSweepMath.DeriveOffsets(rows, "L", 1.5);
        FilterSweepMath.DeriveOffsets(rows, "R", 1.5);
        Assert.That(rows[0].Offset, Is.EqualTo(-40));
        Assert.That(rows[1].Offset, Is.EqualTo(0));
    }

    // ---- what Apply writes ----

    private static EquipmentProfile NewRig() =>
        new() { Id = "rig-1", Name = "Test rig", AutoFocus = new AutoFocusSettings() };

    /// <summary>What <see cref="FilterFocusSweepService.Apply"/> does inside the
    /// profile mutation, without the service: pin the reference, then record
    /// every accepted filter.</summary>
    private static void Write(EquipmentProfile rig, IEnumerable<FilterFocusSweepResult> rows,
                              string? reference, double tol = 1.5) {
        if (!string.IsNullOrWhiteSpace(reference)) {
            rig.AutoFocus ??= new AutoFocusSettings();
            rig.AutoFocus.FilterOffsetReference = reference;
        }
        foreach (var r in rows.Where(r => r.Measured))
            FilterFocusMemoryService.RecordAndRecompute(
                rig, r.Filter, r.Position, r.TemperatureC, r.FocuserName, r.Hfr, tol, r.MeasuredAtUtc);
    }

    [Test]
    public void Write_OffsetsMatchThePreview() {
        // The regression this whole design turns on. RecomputeOffsets resolves
        // the reference itself and, with none configured and no L, falls back to
        // the FRESHEST memory entry, which after a bulk write is the last filter
        // written. Pinning the reference is what makes the preview a promise.
        var rows = new List<FilterFocusSweepResult> {
            Measured("R", 1040, 9), Measured("G", 1010, 9), Measured("B", 975, 9)
        };
        var reference = FilterSweepMath.ResolveSweepReference(rows, null);
        FilterSweepMath.DeriveOffsets(rows, reference, 1.5);

        var rig = NewRig();
        Write(rig, rows, reference);

        foreach (var r in rows)
            Assert.That(rig.FilterOffsets[r.Filter], Is.EqualTo(r.Offset),
                $"stored offset for {r.Filter} must equal the previewed one");
    }

    [Test]
    public void Write_WithoutPinningTheReference_CanDisagreeWithThePreview() {
        // The negative of the test above, kept so the reason for pinning cannot
        // be quietly removed.
        var rows = new List<FilterFocusSweepResult> {
            Measured("R", 1040, 9), Measured("G", 1010, 9), Measured("B", 975, 9)
        };
        var reference = FilterSweepMath.ResolveSweepReference(rows, null);   // R
        FilterSweepMath.DeriveOffsets(rows, reference, 1.5);

        var rig = NewRig();
        // Same writes, reference NOT pinned: the newest entry (B) becomes the
        // reference, so R is no longer zero.
        var now = new DateTime(2026, 9, 22, 22, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < rows.Count; i++)
            FilterFocusMemoryService.RecordAndRecompute(
                rig, rows[i].Filter, rows[i].Position, rows[i].TemperatureC,
                "Foc-A", rows[i].Hfr, 1.5, now.AddMinutes(i));

        Assert.That(rig.FilterOffsets["B"], Is.EqualTo(0));
        Assert.That(rig.FilterOffsets["R"], Is.Not.EqualTo(rows[0].Offset));
    }

    [Test]
    public void Write_StampsTheMeasurementTimeNotTheApplyTime() {
        // The freshness clock (FilterMemoryMaxAgeHours) and the freshest-anchor
        // choice both read this, and a bulk write would otherwise make every
        // filter equally fresh at Apply time.
        var rows = new List<FilterFocusSweepResult> { Measured("L", 1000, 9) };
        var rig = NewRig();
        Write(rig, rows, "L");
        Assert.That(rig.FilterFocusMemory["L"].Utc,
            Is.EqualTo(new DateTime(2026, 9, 22, 22, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void Write_RecordsPositionTemperatureFocuserAndHfr() {
        var rows = new List<FilterFocusSweepResult> { Measured("L", 1234, 7.5) };
        var rig = NewRig();
        Write(rig, rows, "L");
        var mem = rig.FilterFocusMemory["L"];
        Assert.Multiple(() => {
            Assert.That(mem.Position, Is.EqualTo(1234));
            Assert.That(mem.TemperatureC, Is.EqualTo(7.5));
            Assert.That(mem.FocuserName, Is.EqualTo("Foc-A"));
            Assert.That(mem.Hfr, Is.EqualTo(2.5));
        });
    }

    [Test]
    public void Write_LeavesAnExcludedFiltersHandEnteredOffsetAlone() {
        var rig = NewRig();
        rig.FilterOffsets["Ha"] = 999;       // typed in by hand, not measured here
        var rows = new List<FilterFocusSweepResult> { Measured("L", 1000, 9), Measured("R", 1040, 9) };
        Write(rig, rows, "L");
        Assert.That(rig.FilterOffsets["Ha"], Is.EqualTo(999));
        Assert.That(rig.FilterOffsets["R"], Is.EqualTo(40));
    }

    [Test]
    public void Write_IsIdempotent() {
        var rows = new List<FilterFocusSweepResult> { Measured("L", 1000, 9), Measured("R", 1040, 9) };
        var rig = NewRig();
        Write(rig, rows, "L");
        Write(rig, rows, "L");
        Assert.That(rig.FilterFocusMemory.Count, Is.EqualTo(2));
        Assert.That(rig.FilterOffsets["R"], Is.EqualTo(40));
    }

    [Test]
    public void BeforeApply_TheRigIsUntouched() {
        // Decision, pinned: a run holds its results, so measuring and then
        // walking away must leave the rig exactly as it was.
        var rows = new List<FilterFocusSweepResult> { Measured("L", 1000, 9), Measured("R", 1040, 9) };
        FilterSweepMath.DeriveOffsets(rows, "L", 1.5);
        var rig = NewRig();
        Assert.That(rig.FilterFocusMemory, Is.Empty);
        Assert.That(rig.FilterOffsets, Is.Empty);
    }
}
