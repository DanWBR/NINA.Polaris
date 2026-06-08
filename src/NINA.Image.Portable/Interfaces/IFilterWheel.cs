// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

namespace NINA.Image.Interfaces;

/// <summary>
/// Common contract every filter-wheel backend honours so the sequencer,
/// flat-wizard, capture endpoint, and PHD2 dither hook stay backend-
/// agnostic. Implementations: <c>IndiFilterWheel</c> (any INDI EFW),
/// <c>AscomComFilterWheel</c> (direct COM on Windows for ZWO EFW,
/// QHY CFW, Pegasus FilterMaster, etc.).
///
/// <para>Positions are zero-indexed, names are arbitrary strings (the
/// ASCOM Platform spec leaves naming to the driver / user). Filter
/// changes are asynchronous, callers should await the SetPositionAsync
/// call and treat IsMoving as a defensive check before the next
/// exposure.</para>
/// </summary>
public interface IFilterWheel {
    string DeviceName { get; }
    bool IsConnected { get; }
    int Position { get; }
    bool IsMoving { get; }
    string[] FilterNames { get; }
    int FilterCount { get; }
    string CurrentFilterName { get; }

    /// <summary>Optional-feature flags. Drives whether the UI shows
    /// the "Edit filter names" controls -- ASCOM doesn't expose the
    /// names through the driver surface (the host app has to manage
    /// them externally), but INDI's <c>FILTER_NAME</c> text vector
    /// is writable.</summary>
    FilterWheelCapabilities Capabilities => FilterWheelCapabilities.Default;

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task SetPositionAsync(int position, CancellationToken ct = default);
    Task SetFilterByNameAsync(string filterName, CancellationToken ct = default);

    /// <summary>Push a new set of filter names INTO the driver so
    /// they persist across reconnects -- INDI <c>FILTER_NAME</c>
    /// text vector. The array length must match
    /// <see cref="FilterCount"/>. Throws by default; backends opt
    /// in via <see cref="Capabilities"/><c>.SupportsEditNames</c>.</summary>
    Task SetFilterNamesAsync(string[] names, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Editing filter names is not supported by this driver");
}

/// <summary>Optional-feature flags for <see cref="IFilterWheel"/>.</summary>
public record FilterWheelCapabilities(bool SupportsEditNames = false) {
    public static readonly FilterWheelCapabilities Default = new();
}