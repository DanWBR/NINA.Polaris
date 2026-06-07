using System.Runtime.InteropServices;

namespace NINA.Camera.ZwoSdk.Native;

/// <summary>
/// P/Invoke surface for the ZWO ASI SDK (<c>ASICamera2</c> →
/// <c>libASICamera2.so</c> / <c>ASICamera2.dll</c>). Names / argument order
/// mirror <c>ASICamera2.h</c>. Control values + buffer sizes are C
/// <c>long</c> (32-bit Windows / 64-bit Linux) → <see cref="CLong"/>.
/// </summary>
internal static class AsiNative {
    private const string DLL = "ASICamera2";

    public enum ASI_ERROR_CODE {
        ASI_SUCCESS = 0,
        ASI_ERROR_INVALID_INDEX,
        ASI_ERROR_INVALID_ID,
        ASI_ERROR_INVALID_CONTROL_TYPE,
        ASI_ERROR_CAMERA_CLOSED,
        ASI_ERROR_CAMERA_REMOVED,
        ASI_ERROR_INVALID_PATH,
        ASI_ERROR_INVALID_FILEFORMAT,
        ASI_ERROR_INVALID_SIZE,
        ASI_ERROR_INVALID_IMGTYPE,
        ASI_ERROR_OUTOF_BOUNDARY,
        ASI_ERROR_TIMEOUT,
        ASI_ERROR_INVALID_SEQUENCE,
        ASI_ERROR_BUFFER_TOO_SMALL,
        ASI_ERROR_VIDEO_MODE_ACTIVE,
        ASI_ERROR_EXPOSURE_IN_PROGRESS,
        ASI_ERROR_GENERAL_ERROR,
        ASI_ERROR_END
    }

    public enum ASI_BAYER_PATTERN { ASI_BAYER_RG = 0, ASI_BAYER_BG, ASI_BAYER_GR, ASI_BAYER_GB }

    public enum ASI_IMG_TYPE {
        ASI_IMG_RAW8 = 0, ASI_IMG_RGB24 = 1, ASI_IMG_RAW16 = 2, ASI_IMG_Y8 = 3, ASI_IMG_END = -1
    }

    // Control type indices (subset). Values match the header enum order.
    public enum ASI_CONTROL_TYPE {
        ASI_GAIN = 0,
        ASI_EXPOSURE = 1,
        ASI_TEMPERATURE = 8,        // returns 10 * temperature (C)
        ASI_COOLER_POWER_PERC = 15, // 0..100
        ASI_TARGET_TEMP = 16,       // C (not *10)
        ASI_COOLER_ON = 17,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct ASI_CAMERA_INFO {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
        public int CameraID;
        public CLong MaxHeight;
        public CLong MaxWidth;
        public int IsColorCam;
        public int BayerPattern;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public int[] SupportedBins;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] SupportedVideoFormat;
        public double PixelSize;
        public int MechanicalShutter;
        public int ST4Port;
        public int IsCoolerCam;
        public int IsUSB3Host;
        public int IsUSB3Camera;
        public float ElecPerADU;
        public int BitDepth;
        public int IsTriggerCam;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Unused;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct ASI_CONTROL_CAPS {
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

    [DllImport(DLL)] public static extern int ASIGetNumOfConnectedCameras();
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASIGetCameraProperty(ref ASI_CAMERA_INFO pInfo, int iCameraIndex);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASIOpenCamera(int iCameraID);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASIInitCamera(int iCameraID);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASICloseCamera(int iCameraID);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASIGetNumOfControls(int iCameraID, out int piNumberOfControls);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASIGetControlCaps(int iCameraID, int iControlIndex, ref ASI_CONTROL_CAPS pCaps);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASIGetControlValue(int iCameraID, ASI_CONTROL_TYPE ControlType, out CLong plValue, out int pbAuto);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASISetControlValue(int iCameraID, ASI_CONTROL_TYPE ControlType, CLong lValue, int bAuto);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASISetROIFormat(int iCameraID, int iWidth, int iHeight, int iBin, ASI_IMG_TYPE Img_type);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASIGetROIFormat(int iCameraID, out int piWidth, out int piHeight, out int piBin, out ASI_IMG_TYPE pImg_type);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASISetStartPos(int iCameraID, int iStartX, int iStartY);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASIStartVideoCapture(int iCameraID);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASIStopVideoCapture(int iCameraID);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASIGetVideoData(int iCameraID, byte[] pBuffer, CLong lBuffSize, int iWaitms);
    [DllImport(DLL)] public static extern ASI_ERROR_CODE ASIGetDroppedFrames(int iCameraID, out int piDropFrames);
}
