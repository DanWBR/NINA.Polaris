// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors

namespace NINA.Polaris.Services;

/// <summary>Single safety rule for absolute rotator moves. Keep API and
/// sequencer behaviour identical for partial-travel rotators.</summary>
public static class RotatorRange {
    public static double NormalizeMaximum(double maximum) =>
        maximum is 90 or 180 or 360 ? maximum : 360;

    public static bool Allows(double angle, double maximum) =>
        double.IsFinite(angle) && angle >= 0 && angle <= NormalizeMaximum(maximum);

    public static string Error(double maximum) =>
        $"Rotator target must be between 0° and {NormalizeMaximum(maximum):0}° for this rig.";
}
