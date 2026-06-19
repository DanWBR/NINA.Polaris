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

namespace NINA.Polaris.Services.Storage;

/// <summary>Abstraction so tests can inject a fake target.</summary>
public interface IStorageTargetFactory {
    IStorageTarget Create(string kind);
}

/// <summary>
/// Creates a fresh <see cref="IStorageTarget"/> per connect cycle. Adapters own
/// a live connection so they are NOT DI singletons — the factory hands out new
/// instances that <see cref="StoragePushService"/> disposes on drop.
/// </summary>
public sealed class StorageTargetFactory : IStorageTargetFactory {
    public IStorageTarget Create(string kind) => (kind ?? "").Trim().ToLowerInvariant() switch {
        "smb"   => new SmbStorageTarget(),
        "sftp"  => new SftpStorageTarget(),
        "local" => new LocalStorageTarget(),
        var k   => throw new NotSupportedException($"Unknown storage kind: '{k}'")
    };
}
