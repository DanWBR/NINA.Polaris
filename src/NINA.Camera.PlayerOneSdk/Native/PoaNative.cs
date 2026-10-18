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

    // Config indices: order matches the POAConfig enum in the header. The
    // names are the panel ids (and what NativeCameraControlStore saves).
    public enum POAConfig {
        POA_EXPOSURE = 0,   // us, int
        POA_GAIN = 1,       // int
        POA_HARDWARE_BIN = 2,
        POA_TEMPERATURE = 3, // C, float, read-only
        POA_WB_R = 4,       // -1200..1200
        POA_WB_G = 5,
        POA_WB_B = 6,
        POA_OFFSET = 7,     // int
        POA_AUTOEXPO_MAX_GAIN = 8,
        POA_AUTOEXPO_MAX_EXPOSURE = 9,   // ms
        POA_AUTOEXPO_BRIGHTNESS = 10,
        POA_GUIDE_NORTH = 11,
        POA_GUIDE_SOUTH = 12,
        POA_GUIDE_EAST = 13,
        POA_GUIDE_WEST = 14,
        POA_EGAIN = 15,     // e/ADU, float, read-only
        POA_COOLER_POWER = 16, // 0..100, int, read-only
        POA_TARGET_TEMP = 17,  // C, int
        POA_COOLER = 18,    // bool
        POA_HEATER = 19,    // deprecated, read-only
        POA_HEATER_POWER = 20,
        POA_FAN_POWER = 21,
        POA_FLIP_NONE = 22,
        POA_FLIP_HORI = 23,
        POA_FLIP_VERT = 24,
        POA_FLIP_BOTH = 25,
        POA_FRAME_LIMIT = 26,
        POA_HQI = 27,
        POA_USB_BANDWIDTH_LIMIT = 28,
        POA_PIXEL_BIN_SUM = 29,
        POA_MONO_BIN = 30,
        POA_EXP = 31,       // s, float
    }

    /// <summary>C union {long intValue; double floatValue; POABool boolValue;}.
    /// Total size is 8 bytes (double). We read the low-32 int slot (all our
    /// int configs fit in int32, exposure max 2e9) and the double. Writes go
    /// through the 64-bit slot so a negative value (white balance runs
    /// -1200..1200) is sign-extended for the 64-bit Linux C long; on Windows
    /// the C long is the low 4 bytes and the rest is ignored.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public struct POAConfigValue {
        [FieldOffset(0)] public int intValue;
        [FieldOffset(0)] public long longValue;
        [FieldOffset(0)] public double floatValue;
        public static POAConfigValue Int(int v) => new() { longValue = v };
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
    public struct POASensorModeInfo {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc;
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
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetConfigsCount(int nCameraID, out int pConfCount);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetConfigAttributes(int nCameraID, int nConfIndex, ref POAConfigAttributes pConfAttr);
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

    // Sensor modes (since SDK 3.x; a count of 0 means the camera has none).
    // POASetSensorMode wants the exposure stopped.
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetSensorModeCount(int nCameraID, out int pModeCount);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetSensorModeInfo(int nCameraID, int modeIndex, ref POASensorModeInfo pSenModeInfo);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POASetSensorMode(int nCameraID, int modeIndex);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] public static extern POAErrors POAGetSensorMode(int nCameraID, out int pModeIndex);
}