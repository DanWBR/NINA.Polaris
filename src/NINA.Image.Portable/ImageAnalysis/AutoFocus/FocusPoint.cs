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

namespace NINA.Image.ImageAnalysis.AutoFocus;

/// <summary>
/// One measured autofocus sample: focuser position (X), focus measure (Y,
/// mean star HFR) and its 1-sigma uncertainty (ErrorY). Every fit in this
/// folder weights samples by 1/ErrorY², which is the mechanism that unifies
/// outlier handling: a sample with no detected stars is stored as
/// (X, 0, 1000) so its weight is ~1e-6 — effectively ignored by the fits
/// while still participating in the sweep planner's termination logic.
/// Callers floor ErrorY at 0.001 so the weight never divides by zero.
///
/// Portable replacement for the OxyPlot ScatterErrorPoint used by the
/// N.I.N.A. desktop autofocus classes this folder is ported from.
/// </summary>
public readonly record struct FocusPoint(double X, double Y, double ErrorY);
