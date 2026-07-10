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

namespace NINA.Polaris.Services;

/// <summary>
/// Tags a frame broadcast over <c>/ws/image-stream</c> with the panel
/// it belongs to, so the browser can route it to that panel's canvas
/// only instead of fanning every frame out to every visible canvas.
///
/// <para>Wire-encoded as a single <see cref="int"/> at offset 20 of
/// the stream header. <see cref="Live"/> is the legacy default (kind=0)
/// and is the only kind that feeds the WASM live-stack accumulator.</para>
///
/// <para>The enum int values are part of the on-wire protocol; do NOT
/// renumber. Add new kinds at the end.</para>
/// </summary>
public enum FrameKind {
    /// <summary>LIVE tab capture / live-stack output. Goes to
    /// liveCanvas + feeds the running mean stacker.</summary>
    Live = 0,
    /// <summary>PREVIEW tab one-off snap. Goes to previewCanvas only.</summary>
    Preview = 1,
    /// <summary>FOCUS tab manual + V-curve auto-focus exposures.
    /// Goes to focusCanvas / manualFocusCanvas.</summary>
    Focus = 2,
    /// <summary>VIDEO tab planetary stream + recording frames.
    /// Goes to videoCaptureCanvas only.</summary>
    Video = 3,
    /// <summary>SKY-tab inset slew preview (background capture loop
    /// auto-fired while the mount is slewing). Goes to
    /// slewPreviewCanvas only.</summary>
    SlewPreview = 4,
    /// <summary>AUTORUN / ADV sequence-engine capture. Goes to
    /// autorunCanvas only — kept distinct from <see cref="Live"/> so a
    /// running sequence's frames don't leak onto the LIVE tab (and the
    /// LIVE live-stack output doesn't leak into the AUTORUN preview).</summary>
    Autorun = 5,
    /// <summary>Server-integrated live-stack OUTPUT (the running master,
    /// colour JPEG or mono raw). Dedicated kind so the LIVE canvas can show
    /// ONLY stack results while a server stack runs — any stray kind-0 frame
    /// (a raw sub, a driver CFA dropout rendering mono) is ignored instead of
    /// flashing between the colour stack and a B&amp;W frame.</summary>
    LiveStack = 6
}