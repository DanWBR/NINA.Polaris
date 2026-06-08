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

namespace NINA.Camera.PlayerOneSdk.Native;

/// <summary>
/// P/Invoke surface for the PlayerOne camera SDK (<c>PlayerOneCamera</c> →
/// <c>libPlayerOneCamera.so</c> / <c>PlayerOneCamera.dll</c>). Names / argument
/// order mirror <c>PlayerOneCamera.h</c>. The config union holds a C
/// <c>long</c> (32-bit Windows / 64-bit Linux); we read/write its low 32 bits
/// via an overlapping <see cref="int"/> field, and the buffer size of
/// <see cref="POAGetImageData"/> is a C <c>long</c> → <see cref="CLong"/>.
/// </summary>
internal static class PoaNative {
    private const string DLL = "PlayerOneCamera";

    public enum POAErrors {
        POA_OK = 0,
        POA_ERROR_INVALID_INDEX,
        POA_ERROR_INVALID_ID,
        POA_ERROR_INVALID_CONFIG,
        POA_ERROR_INVALID_ARGU,
        POA_ERROR_NOT_OPENED,
        POA_ERROR_DEVICE_NOT_FOUND,
        POA_ERROR_OUT_OF_LIMIT,
        POA_ERROR_EXPOSURE_FAILED,
        POA_ERROR_TIMEOUT,
        POA_ERROR_SIZE_LESS,
        POA_ERROR_POINTER,
        POA_ERROR_IMG_FORMAT,
        POA_ERROR_NULL_POINTER,
        POA_ERROR_ACCESS_DENIED,
        POA_ERROR_OPERATION_FAILED,
        POA_ERROR_MEMORY_FAILED,
    }

    public enum POABool { POA_FALSE = 0, POA_TRUE = 1 }

    public enum POABayerPattern { POA_BAYER_RG = 0, POA_BAYER_BG, POA_BAYER_GR, POA_BAYER_GB, POA_BAYER_MONO = -1 }

    public enum POAImgFormat { POA_RAW8 = 0, POA_RAW16, POA_RGB24, POA_MONO8, POA_END = -1 }

    // Config indices: order matches the POAConfig enum in the header.
    public enum POAConfig {
        POA_EXPOSURE = 0,   // us, int
        POA_GAIN = 1,       // int
        POA_TEMPERATURE = 3, // C, float, read-only
        POA_OFFSET = 7,     // int
        POA_COOLER_POWER = 16, // 0..100, int, read-only
        POA_TARGET_TEMP = 17,  // C, int
        POA_COOLER = 18,    // bool
    }

    /// <summary>C union {long intValue; double floatValue; POABool boolValue;}.
    /// Total size is 8 bytes (double). We expose only the low-32 int slot
    /// (all our int configs fit in int32, exposure max 2e9) and the double.
    /// A fresh value zero-inits the high 4 bytes so the 64-bit Linux C long
    /// is read correctly for positive values.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public struct POAConfigValue {
        [FieldOffset(0)] public int intValue;
        [FieldOffset(0)] public double floatValue;
        public static POAConfigValue Int(int v) => new() { intValue = v };
        public static POAConfigValue Float(double v) => new() { floatValue = v };
        public static POAConfigValue Bool(bool v) => new() { intValue = v ? 1 : 0 };
    }

    public enum POAValueType { VAL_INT = 0, VAL_FLOAT, VAL_BOOL }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct POAConfigAttributes {
        public int isSupportAuto;
        public int isWritable;
        public int isReadable;
        public POAConfig configID;
        public POAValueType valueType;
        public POAConfigValue maxValue;
        public POAConfigValue minValue;
        public POAConfigValue defaultValue;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szConfName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szDescription;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct POACameraProperties {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string cameraModelName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string userCustomID;
        public int cameraID;
        public int maxWidth;
        public int maxHeight;
        public int bitDepth;
        public int isColorCamera;
        public int isHasST4Port;
        public int isHasCooler;
        public int isUSB3Speed;
        public POABayerPattern bayerPattern;
        public double pixelSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string SN;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string sensorModelName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string localPath;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] bins;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] imgFormats;
        public int isSupportHardBin;
        public int pID;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 248)] public byte[] reserved;
    }

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern int POAGetCameraCount();
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetCameraProperties(int nIndex, ref POACameraProperties pProp);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetCameraPropertiesByID(int nCameraID, ref POACameraProperties pProp);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAOpenCamera(int nCameraID);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAInitCamera(int nCameraID);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POACloseCamera(int nCameraID);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetConfigAttributesByConfigID(int nCameraID, POAConfig confID, ref POAConfigAttributes pConfAttr);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetConfig(int nCameraID, POAConfig confID, ref POAConfigValue pConfValue, ref POABool pIsAuto);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POASetConfig(int nCameraID, POAConfig confID, POAConfigValue confValue, POABool isAuto);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetImageSize(int nCameraID, out int pWidth, out int pHeight);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POASetImageSize(int nCameraID, int width, int height);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetImageStartPos(int nCameraID, out int pStartX, out int pStartY);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POASetImageStartPos(int nCameraID, int startX, int startY);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetImageBin(int nCameraID, out int pBin);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POASetImageBin(int nCameraID, int bin);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetImageFormat(int nCameraID, out POAImgFormat pImgFormat);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POASetImageFormat(int nCameraID, POAImgFormat imgFormat);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAStartExposure(int nCameraID, POABool bSingleFrame);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAStopExposure(int nCameraID);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAImageReady(int nCameraID, ref POABool pIsReady);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetImageData(int nCameraID, byte[] pBuf, CLong lBufSize, int nTimeoutms);
}