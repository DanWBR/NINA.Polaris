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

namespace NINA.INDI.Protocol;

/// <summary>
/// The shared-memory BLOB path can no longer be trusted: a descriptor did not
/// match the element announcing it, or a message failed to parse while
/// descriptors may have been riding on it. Either way the queue of received
/// descriptors and the parser's position in the XML may have drifted apart, and
/// from then on every frame would silently get the bytes of the one before it.
/// The connection is dropped; the reconnect starts with an empty queue.
/// </summary>
public sealed class IndiAttachedBlobException : IOException {
    public IndiAttachedBlobException(string message) : base(message) { }
    public IndiAttachedBlobException(string message, Exception inner) : base(message, inner) { }
}
