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

// Part of the built-in gear simulator (see SimGearState / SimStarField, which
// are ported from PHD2 under BSD-3-Clause). This DI singleton owns the one
// shared SimGearState + SimStarField so the simulated guide camera and the
// simulated mount operate on the same virtual sky.

namespace NINA.Polaris.Services.Simulator.Gear;

/// <summary>
/// Singleton that owns the shared simulator state. <c>EquipmentManager</c>
/// resolves it to construct <see cref="SimGuideCamera"/> and
/// <see cref="SimMount"/>, which both read/write the same
/// <see cref="SimGearState"/> — that coupling is what makes a pulse guide on
/// the mount visibly shift the star field the camera captures.
/// </summary>
public sealed class SimGearService {
    /// <summary>Live tunables. Mutating these affects subsequent captures.</summary>
    public SimGearParams Params { get; private set; }

    public SimGearState State { get; private set; }
    public SimStarField StarField { get; private set; }

    public SimGearService() : this(new SimGearParams()) { }

    public SimGearService(SimGearParams p) {
        Params = p;
        State = new SimGearState(Params);
        StarField = new SimStarField(Params);
    }

    /// <summary>Rebuild the star field + state from (optionally) new params,
    /// e.g. after the user changes star count or geometry. Offsets reset.</summary>
    public void Reset(SimGearParams? p = null) {
        Params = p ?? Params;
        State = new SimGearState(Params);
        StarField = new SimStarField(Params);
    }
}