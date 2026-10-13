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

using System.IO.Compression;

namespace NINA.Polaris.Services.External;

/// <summary>
/// Puts the Canon EDSDK DLLs where Polaris looks for them, from what the
/// operator downloaded from Canon: the SDK zip itself, or the DLLs taken out of
/// it. Canon's licence does not let us ship EDSDK, so the operator downloads
/// it; this only saves them finding the right two files and the right folder.
///
/// The zip carries each DLL twice, a 32-bit copy under <c>EDSDK/Dll</c> and a
/// 64-bit one under <c>EDSDK_64/Dll</c>. Polaris is 64-bit and can load only
/// the second, so the copies are told apart by their PE header, not by the
/// folder they sit in: a renamed or repacked zip still installs the right one.
/// </summary>
public static class CanonEdsdkInstaller {
    /// <summary>The DLLs EDSDK needs; EDSDK.dll loads EdsImage.dll.</summary>
    public static readonly string[] Required = { "EDSDK.dll", "EdsImage.dll" };

    public sealed record Result(bool Ok, string Message, IReadOnlyList<string> Installed);

    /// <summary>Machine type of a PE image, or 0 when the bytes are not one.
    /// 0x8664 is x64, 0xAA64 ARM64, 0x14C 32-bit x86.</summary>
    public static ushort PeMachine(ReadOnlySpan<byte> image) {
        if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z') return 0;
        int pe = BitConverter.ToInt32(image.Slice(0x3C, 4));
        if (pe < 0 || pe + 6 > image.Length) return 0;
        if (image[pe] != (byte)'P' || image[pe + 1] != (byte)'E' || image[pe + 2] != 0 || image[pe + 3] != 0) return 0;
        return BitConverter.ToUInt16(image.Slice(pe + 4, 2));
    }

    public static bool Is64Bit(ReadOnlySpan<byte> image) {
        var m = PeMachine(image);
        return m == 0x8664 || m == 0xAA64;
    }

    /// <summary>Install from uploaded files (zips and/or DLLs) into
    /// <paramref name="destDir"/>.</summary>
    public static Result Install(IEnumerable<(string Name, byte[] Data)> uploads, string destDir) {
        var found = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var only32 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string fileName, byte[] data) {
            var name = Path.GetFileName(fileName);
            var wanted = Required.FirstOrDefault(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase));
            if (wanted == null) return;
            if (Is64Bit(data)) found[wanted] = data;
            else if (PeMachine(data) != 0) only32.Add(wanted);
        }

        foreach (var (name, data) in uploads) {
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
                try {
                    using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
                    foreach (var entry in zip.Entries) {
                        if (!Required.Any(r => string.Equals(r, entry.Name, StringComparison.OrdinalIgnoreCase))) continue;
                        using var s = entry.Open();
                        using var ms = new MemoryStream();
                        s.CopyTo(ms);
                        Consider(entry.Name, ms.ToArray());
                    }
                } catch (InvalidDataException) {
                    return new Result(false, $"{name} is not a readable zip file.", Array.Empty<string>());
                }
            } else {
                Consider(name, data);
            }
        }

        var missing = Required.Where(r => !found.ContainsKey(r)).ToList();
        if (missing.Count > 0) {
            var why = missing.All(only32.Contains)
                ? $"Only the 32-bit {string.Join(" and ", missing)} were found. Polaris needs the 64-bit ones, from the EDSDK_64\\Dll folder of the Canon SDK."
                : $"{string.Join(" and ", missing)} not found. Upload the EDSDK zip from Canon, or EDSDK.dll and EdsImage.dll from its EDSDK_64\\Dll folder.";
            return new Result(false, why, Array.Empty<string>());
        }

        Directory.CreateDirectory(destDir);
        var installed = new List<string>();
        foreach (var (name, data) in found) {
            var dest = Path.Combine(destDir, name);
            var tmp = dest + ".part";
            try {
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, dest, overwrite: true);
            } catch (IOException) {
                // Windows keeps a loaded DLL locked: replacing an EDSDK this
                // process already uses has to wait for a restart.
                try { File.Delete(tmp); } catch { }
                return new Result(false,
                    $"{name} in {destDir} is in use. Restart Polaris and install again to replace it.",
                    installed);
            }
            installed.Add(dest);
        }
        return new Result(true, $"Canon EDSDK installed in {destDir}.", installed);
    }
}
