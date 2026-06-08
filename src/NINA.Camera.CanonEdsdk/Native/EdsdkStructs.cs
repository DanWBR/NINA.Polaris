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

using System.Runtime.InteropServices;

namespace NINA.Camera.CanonEdsdk.Native;

/// <summary>Mirror of the EDSDK <c>EdsDeviceInfo</c> struct returned
/// by <c>EdsGetDeviceInfo</c>. Field order, sizes and packing are dictated
/// by the SDK header, don't reorder.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct EdsDeviceInfo {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szPortName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szDeviceDescription;

    public uint DeviceSubType;
    public uint Reserved;
}

/// <summary>Mirror of <c>EdsDirectoryItemInfo</c>, supplied to the
/// object-event handler when the camera has a captured file ready for
/// transfer. We use <c>Size</c> + <c>szFileName</c> to size the buffer
/// and pick the right extension (.cr2 vs .jpg).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct EdsDirectoryItemInfo {
    public uint Size;
    public int  IsFolder;
    public uint GroupID;
    public uint Option;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szFileName;

    public uint Format;
    public uint DateTime;
}