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

namespace NINA.Camera.SvbonySdk.Native;

/// <summary>
/// P/Invoke surface for the SVBony camera SDK (<c>SVBCameraSDK</c>). The
/// .NET native loader maps the name to <c>libSVBCameraSDK.so</c> (Linux) or
/// <c>SVBCameraSDK.dll</c> (Windows); the per-RID binary is copied next to
/// the executable by the project's Content items, and SvbonyRegistry also
/// registers a DllImportResolver pointing at the app base dir as a fallback.
///
/// Names / argument order mirror <c>SVBCameraSDK.h</c>. The SDK uses C
/// <c>long</c> for control values and buffer sizes, which is 32-bit on
/// Windows but 64-bit on 64-bit Linux — <see cref="CLong"/> models that
/// per-platform width correctly (do NOT use int/nint here).
/// </summary>
internal static class SvbonyNative {
    private const string DLL = "SVBCameraSDK";

    public enum SVB_ERROR_CODE {
        SVB_SUCCESS = 0,
        SVB_ERROR_INVALID_INDEX,
        SVB_ERROR_INVALID_ID,
        SVB_ERROR_INVALID_CONTROL_TYPE,
        SVB_ERROR_CAMERA_CLOSED,
        SVB_ERROR_CAMERA_REMOVED,
        SVB_ERROR_INVALID_PATH,
        SVB_ERROR_INVALID_FILEFORMAT,
        SVB_ERROR_INVALID_SIZE,
        SVB_ERROR_INVALID_IMGTYPE,
        SVB_ERROR_OUTOF_BOUNDARY,
        SVB_ERROR_TIMEOUT,
        SVB_ERROR_INVALID_SEQUENCE,
        SVB_ERROR_BUFFER_TOO_SMALL,
        SVB_ERROR_VIDEO_MODE_ACTIVE,
        SVB_ERROR_EXPOSURE_IN_PROGRESS,
        SVB_ERROR_GENERAL_ERROR,
        SVB_ERROR_INVALID_MODE,
        SVB_ERROR_INVALID_DIRECTION,
        SVB_ERROR_UNKNOW_SENSOR_TYPE,
        SVB_ERROR_END
    }

    public enum SVB_BAYER_PATTERN { SVB_BAYER_RG = 0, SVB_BAYER_BG, SVB_BAYER_GR, SVB_BAYER_GB }

    public enum SVB_IMG_TYPE {
        SVB_IMG_RAW8 = 0, SVB_IMG_RAW10, SVB_IMG_RAW12, SVB_IMG_RAW14, SVB_IMG_RAW16,
        SVB_IMG_Y8, SVB_IMG_Y10, SVB_IMG_Y12, SVB_IMG_Y14, SVB_IMG_Y16,
        SVB_IMG_RGB24, SVB_IMG_RGB32, SVB_IMG_END = -1
    }

    public enum SVB_CAMERA_MODE { SVB_MODE_NORMAL = 0 }

    // Control indices (subset we use). Values match the header enum order.
    public enum SVB_CONTROL_TYPE {
        SVB_GAIN = 0,
        SVB_EXPOSURE = 1,
        SVB_GAMMA = 2,
        SVB_GAMMA_CONTRAST = 3,
        SVB_WB_R = 4,
        SVB_WB_G = 5,
        SVB_WB_B = 6,
        SVB_FLIP = 7,
        SVB_FRAME_SPEED_MODE = 8,
        SVB_CONTRAST = 9,
        SVB_SHARPNESS = 10,
        SVB_SATURATION = 11,
        SVB_AUTO_TARGET_BRIGHTNESS = 12,
        SVB_BLACK_LEVEL = 13,
        SVB_COOLER_ENABLE = 14,
        SVB_TARGET_TEMPERATURE = 15,   // 0.1 C
        SVB_CURRENT_TEMPERATURE = 16,  // 0.1 C
        SVB_COOLER_POWER = 17,         // 0..100
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SVB_CAMERA_INFO {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string CameraSN;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string PortType;
        public uint DeviceID;
        public int CameraID;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SVB_CAMERA_PROPERTY {
        public CLong MaxHeight;
        public CLong MaxWidth;
        public int IsColorCam;
        public int BayerPattern;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public int[] SupportedBins;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] SupportedVideoFormat;
        public int MaxBitDepth;
        public int IsTriggerCam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SVB_CONTROL_CAPS {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public CLong MaxValue;
        public CLong MinValue;
        public CLong DefaultValue;
        public int IsAutoSupported;
        public int IsWritable;
        public int ControlType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Unused;
    }

    [DllImport(DLL)] public static extern int SVBGetNumOfConnectedCameras();
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBGetCameraInfo(ref SVB_CAMERA_INFO pInfo, int iCameraIndex);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBGetCameraProperty(int iCameraID, ref SVB_CAMERA_PROPERTY pProp);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBOpenCamera(int iCameraID);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBCloseCamera(int iCameraID);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBGetNumOfControls(int iCameraID, out int piNumberOfControls);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBGetControlCaps(int iCameraID, int iControlIndex, ref SVB_CONTROL_CAPS pCaps);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBGetControlValue(int iCameraID, SVB_CONTROL_TYPE ControlType, out CLong plValue, out int pbAuto);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBSetControlValue(int iCameraID, SVB_CONTROL_TYPE ControlType, CLong lValue, int bAuto);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBSetOutputImageType(int iCameraID, SVB_IMG_TYPE ImageType);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBSetROIFormat(int iCameraID, int iStartX, int iStartY, int iWidth, int iHeight, int iBin);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBGetROIFormat(int iCameraID, out int piStartX, out int piStartY, out int piWidth, out int piHeight, out int piBin);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBSetCameraMode(int iCameraID, SVB_CAMERA_MODE mode);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBStartVideoCapture(int iCameraID);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBStopVideoCapture(int iCameraID);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBGetVideoData(int iCameraID, byte[] pBuffer, CLong lBuffSize, int iWaitms);
    [DllImport(DLL)] public static extern SVB_ERROR_CODE SVBGetSensorPixelSize(int iCameraID, out float fPixelSize);
}