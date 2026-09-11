// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

namespace NINA.Image.Interfaces;

/// <summary>Common surface for mechanical camera rotators.  Keeping this
/// deliberately small lets INDI and Alpaca devices share the equipment card,
/// sequencer instruction, and FITS metadata path. State properties are cached;
/// callers must explicitly refresh a backend that has no push notifications.</summary>
public interface IRotator {
    string DeviceName { get; }
    bool IsConnected { get; }
    double Position { get; }
    bool IsMoving { get; }
    bool IsReversed { get; }

    /// <summary>Refresh the cached state. INDI reads a push-maintained property
    /// tree and completes immediately; Alpaca performs its HTTP reads here,
    /// never from a synchronous property getter.</summary>
    Task RefreshAsync(CancellationToken ct = default);
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task MoveToAsync(double degrees, CancellationToken ct = default);
    Task ReverseAsync(bool reversed, CancellationToken ct = default);
    Task AbortAsync(CancellationToken ct = default);
}
