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

// Entry point. browser-wasm requires OutputType=Exe + a Main, but in
// the browser the "main" never runs to completion, the WASM runtime
// stays alive waiting for JS to call into the [JSExport] surface
// defined in Interop.cs.
//
// We keep Main empty + use the explicit Main signature (instead of
// top-level statements) so the linker has a stable entry-point symbol.

namespace NINA.Polaris.Wasm;

public class Program {
    public static void Main() {
        // Intentionally empty.
    }
}