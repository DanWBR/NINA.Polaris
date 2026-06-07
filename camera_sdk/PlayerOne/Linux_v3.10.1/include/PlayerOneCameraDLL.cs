using System;
using System.Runtime.InteropServices;
using System.Text;

namespace POA.PlayerOneSDK
{
    //Please put the "PlayerOneCamera.dll" and "PlayerOneCamera_x64.dll" into the executable directory(bin/debug, bin/release)
    //----->For more information, please read 'PlayerOneCamera.h' and development manual

    public class PlayerOneCameraDLL
    {
        public enum POABool // BOOL Value Definition
        {
            POA_FALSE = 0,  // false
            POA_TRUE        // true
        }

        public enum POABayerPattern // Bayer Pattern Definition
        {
            POA_BAYER_RG = 0,   // RGGB
            POA_BAYER_BG,       // BGGR
            POA_BAYER_GR,       // GRBG
            POA_BAYER_GB,       // GBRG
            POA_BAYER_MONO = -1 // Monochrome, the mono camera with this
        }
                
        public enum POAImgFormat  // Image Data Format Definition
        {
            POA_RAW8 = 0,       // 8bit raw data, 1 pixel 1 byte, value range[0, 255]
            POA_RAW16,      // 16bit raw data, 1 pixel 2 bytes, value range[0, 65535]
            POA_RGB24,      // RGB888 color data, 1 pixel 3 bytes, value range[0, 255] (only color camera)
            POA_MONO8,      // 8bit monochrome data, convert the Bayer Filter Array to monochrome data. 1 pixel 1 byte, value range[0, 255] (only color camera)
            POA_END = -1
        }

        public enum POAErrors                 // Return Error Code Definition
        {
            POA_OK = 0,                         // operation successful
            POA_ERROR_INVALID_INDEX,            // invalid index, means the index is < 0 or >= the count( camera or config)
            POA_ERROR_INVALID_ID,               // invalid camera ID
            POA_ERROR_INVALID_CONFIG,           // invalid POAConfig
            POA_ERROR_INVALID_ARGU,             // invalid argument(parameter)
            POA_ERROR_NOT_OPENED,               // camera not opened
            POA_ERROR_DEVICE_NOT_FOUND,         // camera not found, may be removed
            POA_ERROR_OUT_OF_LIMIT,             // the value out of limit
            POA_ERROR_EXPOSURE_FAILED,          // camera exposure failed
            POA_ERROR_TIMEOUT,                  // timeout
            POA_ERROR_SIZE_LESS,                // the data buffer size is not enough
            POA_ERROR_EXPOSING,                 // camera is exposing. some operation, must stop exposure first
            POA_ERROR_POINTER,                  // invalid pointer, when get some value, do not pass the NULL pointer to the function
            POA_ERROR_CONF_CANNOT_WRITE,        // the POAConfig is not writable
            POA_ERROR_CONF_CANNOT_READ,         // the POAConfig is not readable
            POA_ERROR_ACCESS_DENIED,            // access denied
            POA_ERROR_OPERATION_FAILED,         // operation failed
            POA_ERROR_MEMORY_FAILED             // memory allocation failed
        }

        public enum POACameraState            // Camera State Definition
        {
            STATE_CLOSED = 0,                   // camera was closed
            STATE_OPENED,                       // camera was opened, but not exposing
            STATE_EXPOSING                      // camera is exposing
        }

        public enum POAValueType              // Config Value Type Definition
        {
            VAL_INT = 0,                        // integer(int)
            VAL_FLOAT,                          // float(double)
            VAL_BOOL                            // bool(POABool)
        }

        public enum POAConfig                 // Camera Config Definition
        {
            POA_EXPOSURE = 0,                   // exposure time(unit: us), range:[10 - 2000000000], read-write, recommended to use POA_EXP, valueType == VAL_INT
            POA_GAIN,                           // gain, read-write, valueType == VAL_INT
            POA_HARDWARE_BIN,                   // hardware bin, read-write, valueType == VAL_BOOL
            POA_TEMPERATURE,                    // camera temperature(uint: C), read-only, valueType == VAL_FLOAT
            POA_WB_R,                           // red channel of white balance, range:[-1200, 1200], read-write, valueType == VAL_INT
            POA_WB_G,                           // green channel of white balance, range:[-1200, 1200], read-write, valueType == VAL_INT
            POA_WB_B,                           // blue channel of white balance, range:[-1200, 1200], read-write, valueType == VAL_INT
            POA_OFFSET,                         // camera offset, read-write, valueType == VAL_INT
            POA_AUTOEXPO_MAX_GAIN,              // maximum gain when auto-adjust, read-write, valueType == VAL_INT
            POA_AUTOEXPO_MAX_EXPOSURE,          // maximum exposure when auto-adjust(uint: ms), read-write, valueType == VAL_INT
            POA_AUTOEXPO_BRIGHTNESS,            // target brightness when auto-adjust, read-write, valueType == VAL_INT
            POA_GUIDE_NORTH,                    // ST4 guide north, generally,it's DEC+ on the mount, read-write, valueType == VAL_BOOL
            POA_GUIDE_SOUTH,                    // ST4 guide south, generally,it's DEC- on the mount, read-write, valueType == VAL_BOOL
            POA_GUIDE_EAST,                     // ST4 guide east, generally,it's RA+ on the mount, read-write, valueType == VAL_BOOL
            POA_GUIDE_WEST,                     // ST4 guide west, generally,it's RA- on the mount, read-write, valueType == VAL_BOOL
            POA_EGAIN,                          // e/ADU, This value will change with gain, read-only, valueType == VAL_FLOAT
            POA_COOLER_POWER,                   // cooler power percentage[0-100%](only cool camera), read-only, valueType == VAL_INT
            POA_TARGET_TEMP,                    // camera target temperature(uint: C), read-write, valueType == VAL_INT
            POA_COOLER,                         // turn cooler(and fan) on or off, read-write, valueType == VAL_BOOL
            POA_HEATER,                         // (deprecated)get state of lens heater(on or off), read-only, valueType == VAL_BOOL
            POA_HEATER_POWER,                   // lens heater power percentage[0-100%], read-write, valueType == VAL_INT
            POA_FAN_POWER,                      // radiator fan power percentage[0-100%], read-write, valueType == VAL_INT
            POA_FLIP_NONE,                      // no flip, Note: set this config(POASetConfig), the 'confValue' will be ignored, read-write, valueType == VAL_BOOL
            POA_FLIP_HORI,                      // flip the image horizontally, Note: set this config(POASetConfig), the 'confValue' will be ignored, read-write, valueType == VAL_BOOL
            POA_FLIP_VERT,                      // flip the image vertically, Note: set this config(POASetConfig), the 'confValue' will be ignored, read-write, valueType == VAL_BOOL
            POA_FLIP_BOTH,                      // flip the image horizontally and vertically, Note: set this config(POASetConfig), the 'confValue' will be ignored, read-write, valueType == VAL_BOOL
            POA_FRAME_LIMIT,                    // Frame rate limit, the range:[0, 2000], 0 means no limit, read-write, valueType == VAL_INT
            POA_HQI,                            // High quality image, for those without DDR camera(guide camera), if set POA_TRUE, this will reduce the waviness and stripe of the image,
                                                // but frame rate may go down, note: this config has no effect on those cameras that with DDR. read-write, valueType == VAL_BOOL
            POA_USB_BANDWIDTH_LIMIT,            // USB bandwidth limit, read-write, valueType == VAL_INT
            POA_PIXEL_BIN_SUM,                  // take the sum of pixels after binning, POA_TRUE is sum and POA_FLASE is average, default is POA_FLASE, read-write, valueType == VAL_BOOL
            POA_MONO_BIN,                       // only for color camera, when set to POA_TRUE, pixel binning will use neighbour pixels and image after binning will lose the bayer pattern, read-write, valueType == VAL_BOOL
            POA_EXP                             // exposure time(unit: s),range [0.00001 - 7200.0], read-write, valueType == VAL_FLOAT
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POACameraProperties     // Camera Properties Definition
        {
            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U1, SizeConst = 256)]
            public byte[] cameraName;            // the camera name

            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U1, SizeConst = 16)]
            public byte[] customID;            // user custom name, it will be will be added after the camera name, max len 16 bytes,like:Mars-C [Juno], default is empty

            public int cameraID;                       // it's unique,camera can be controlled and set by the cameraID
            public int maxWidth;                       // max width of the camera
            public int maxHeight;                      // max height of the camera
            public int bitDepth;                       // ADC depth of image sensor
            public POABool isColorCamera;              // is a color camera or not
            public POABool isHasST4Port;               // does the camera have ST4 port, if not, camera don't support ST4 guide
            public POABool isHasCooler;                // does the camera have cooler, generally, the cool camera with cooler
            public POABool isUSB3Speed;                // is usb3.0 speed
            public POABayerPattern bayerPattern;       // the bayer filter pattern of camera
            public double pixelSize;                   // camera pixel size(unit: um)
            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U1, SizeConst = 64)]
            public byte[] serialNumber;                        // the serial number of camera,it's unique

            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U1, SizeConst = 32)]
            public byte[] sensorName;                        // the sersor model(name) of camera, eg: IMX462

            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U1, SizeConst = 256)]
            public byte[] localPathInHost;                // the path of the camera in the computer host

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public int [] bins;                        // bins supported by the camera, 1 == bin1, 2 == bin2,..., end with 0, eg:[1,2,3,4,0,0,0,0]

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public POAImgFormat[] imgFormats;         // image data format supported by the camera, end with POA_END, eg:[POA_RAW8, POA_RAW16, POA_END,...]

            public POABool isSupportHardBin;        // does the camera sensor support hardware bin
            public int pID;                         // camera's Product ID, note: the vID of PlayerOne is 0xA0A0

            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U1, SizeConst = 248)]
            public byte[] reserved;                 // reserved

            public string cameraModelName
            {
                get { return Encoding.ASCII.GetString(cameraName).TrimEnd((Char)0); }
            }

            public string userCustomID
            {
                get { return Encoding.ASCII.GetString(customID).TrimEnd((Char)0); }
            }

            public string SN
            {
                get { return Encoding.ASCII.GetString(serialNumber).TrimEnd((Char)0); }
            }

            public string sensorModelName
            {
                get { return Encoding.ASCII.GetString(sensorName).TrimEnd((Char)0); }
            }

            public string localPath
            {
                get { return Encoding.ASCII.GetString(localPathInHost).TrimEnd((Char)0); }
            }
        }

        // https://csharppedia.com/en/tutorial/5626/how-to-use-csharp-structs-to-create-a-union-type---similar-to-c-unions-
        //The Struct needs to be annotated as "Explicit Layout", like this:
        [StructLayout(LayoutKind.Explicit)]
        public struct POAConfigValue           // Config Value Definition
        {
            //The "FieldOffset:" means that this Integer starts 
            //Offset 0, in bytes, (with sizeof(long) = 4 bytes length): 
            [FieldOffset(0)]
            public int intValue;                      // int

            //Offset 0, (length sizeof(double) = 8 bytes)...   
            [FieldOffset(0)]
            public double floatValue;                  // double

            //Offset 0, (length sizeof(enum) = 4 bytes)...   
            [FieldOffset(0)]
            public POABool boolValue;                  // POABool
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POAConfigAttributes     // Camera Config Attributes Definition(every POAConfig has a POAConfigAttributes)
        {
            public POABool isSupportAuto;              // is support auto?
            public POABool isWritable;                 // is writable?
            public POABool isReadable;                 // is readable?
            public POAConfig configID;                 // config ID, eg: POA_EXPOSURE
            public POAValueType valueType;             // value type, eg: VAL_INT
            public POAConfigValue maxValue;            // maximum value
            public POAConfigValue minValue;            // minimum value
            public POAConfigValue defaultValue;        // default value
            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U1, SizeConst = 64)]
            public byte[] configName;                // POAConfig name, eg: POA_EXPOSURE: "Exposure", POA_TARGET_TEMP: "TargetTemp"
            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U1, SizeConst = 128)]
            public byte [] configDescription;            // a brief introduction about this one POAConfig

            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U1, SizeConst = 64)]
            public byte[] reserved;                  // reserved

            public string szConfName
            {
                get { return Encoding.ASCII.GetString(configName).TrimEnd((Char)0); }
            }

            public string szDescription
            {
                get { return Encoding.ASCII.GetString(configDescription).TrimEnd((Char)0); }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POASensorModeInfo         //The information of sensor mode 
        {
            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U1, SizeConst = 64)]
            public byte[] modeName;                        //name of sensor mode that can be displayed on the UI, eg: combobox

            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U1, SizeConst = 128)]
            public byte[] modeDesc;                        //description of sensor mode, which can be used for tooltips 

            public string name
            {
                get { return Encoding.ASCII.GetString(modeName).TrimEnd((Char)0); }
            }

            public string desc
            {
                get { return Encoding.ASCII.GetString(modeDesc).TrimEnd((Char)0); }
            }
        }

        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetCameraCount", CallingConvention = CallingConvention.Cdecl)]
        private static extern int POAGetCameraCount32();

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetCameraCount", CallingConvention = CallingConvention.Cdecl)]
        private static extern int POAGetCameraCount64();


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetCameraProperties", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetCameraProperties32(int nIndex, out POACameraProperties pProp);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetCameraProperties", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetCameraProperties64(int nIndex, out POACameraProperties pProp);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetCameraPropertiesByID", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetCameraPropertiesByID32(int nCameraID, out POACameraProperties pProp);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetCameraPropertiesByID", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetCameraPropertiesByID64(int nCameraID, out POACameraProperties pProp);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAOpenCamera", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAOpenCamera32(int nCameraID);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAOpenCamera", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAOpenCamera64(int nCameraID);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAInitCamera", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAInitCamera32(int nCameraID);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAInitCamera", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAInitCamera64(int nCameraID);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POACloseCamera", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POACloseCamera32(int nCameraID);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POACloseCamera", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POACloseCamera64(int nCameraID);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetConfigsCount", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetConfigsCount32(int nCameraID, out int pConfCount);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetConfigsCount", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetConfigsCount64(int nCameraID, out int pConfCount);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetConfigAttributes", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetConfigAttributes32(int nCameraID, int nConfIndex, out POAConfigAttributes pConfAttr);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetConfigAttributes", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetConfigAttributes64(int nCameraID, int nConfIndex, out POAConfigAttributes pConfAttr);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetConfigAttributesByConfigID", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetConfigAttributesByConfigID32(int nCameraID, POAConfig confID, out POAConfigAttributes pConfAttr);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetConfigAttributesByConfigID", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetConfigAttributesByConfigID64(int nCameraID, POAConfig confID, out POAConfigAttributes pConfAttr);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POASetConfig", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetConfig32(int nCameraID, POAConfig confID, POAConfigValue confValue, POABool isAuto);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POASetConfig", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetConfig64(int nCameraID, POAConfig confID, POAConfigValue confValue, POABool isAuto);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetConfig", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetConfig32(int nCameraID, POAConfig confID, out POAConfigValue confValue, out POABool isAuto);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetConfig", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetConfig64(int nCameraID, POAConfig confID, out POAConfigValue confValue, out POABool isAuto);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetConfigValueType", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetConfigValueType32(POAConfig confID, out POAValueType pConfValueType);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetConfigValueType", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetConfigValueType64(POAConfig confID, out POAValueType pConfValueType);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POASetImageStartPos", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetImageStartPos32(int nCameraID, int startX, int startY);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POASetImageStartPos", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetImageStartPos64(int nCameraID, int startX, int startY);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetImageStartPos", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetImageStartPos32(int nCameraID, out int pStartX, out int pStartY);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetImageStartPos", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetImageStartPos64(int nCameraID, out int pStartX, out int pStartY);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POASetImageSize", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetImageSize32(int nCameraID, int width, int height);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POASetImageSize", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetImageSize64(int nCameraID, int width, int height);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetImageSize", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetImageSize32(int nCameraID, out int pWidth, out int pHeight);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetImageSize", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetImageSize64(int nCameraID, out int pWidth, out int pHeight);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POASetImageBin", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetImageBin32(int nCameraID, int bin);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POASetImageBin", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetImageBin64(int nCameraID, int bin);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetImageBin", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetImageBin32(int nCameraID, out int pBin);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetImageBin", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetImageBin64(int nCameraID, out int pBin);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POASetImageFormat", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetImageFormat32(int nCameraID, POAImgFormat imgFormat);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POASetImageFormat", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetImageFormat64(int nCameraID, POAImgFormat imgFormat);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetImageFormat", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetImageFormat32(int nCameraID, out POAImgFormat pImgFormat);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetImageFormat", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetImageFormat64(int nCameraID, out POAImgFormat pImgFormat);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAStartExposure", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAStartExposure32(int nCameraID, POABool bSingleFrame);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAStartExposure", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAStartExposure64(int nCameraID, POABool bSingleFrame);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAStopExposure", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAStopExposure32(int nCameraID);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAStopExposure", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAStopExposure64(int nCameraID);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetCameraState", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetCameraState32(int nCameraID, out POACameraState pCameraState);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetCameraState", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetCameraState64(int nCameraID, out POACameraState pCameraState);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAImageReady", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAImageReady32(int nCameraID, out POABool pIsReady);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAImageReady", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAImageReady64(int nCameraID, out POABool pIsReady);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetImageData", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetImageData32(int nCameraID, IntPtr pBuf, int nBufSize, int nTimeoutms);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetImageData", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetImageData64(int nCameraID, IntPtr pBuf, int nBufSize, int nTimeoutms);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetDroppedImagesCount", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetDroppedImagesCount32(int nCameraID, out int pDroppedCount);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetDroppedImagesCount", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetDroppedImagesCount64(int nCameraID, out int pDroppedCount);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetSensorModeCount", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetSensorModeCount32(int nCameraID, out int pModeCount);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetSensorModeCount", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetSensorModeCount64(int nCameraID, out int pModeCount);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetSensorModeInfo", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetSensorModeInfo32(int nCameraID, int index, out POASensorModeInfo pSenModeInfo);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetSensorModeInfo", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetSensorModeInfo64(int nCameraID, int index, out POASensorModeInfo pSenModeInfo);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POASetSensorMode", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetSensorMode32(int nCameraID, int modeIndex);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POASetSensorMode", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetSensorMode64(int nCameraID, int modeIndex);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetSensorMode", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetSensorMode32(int nCameraID, out int pModeIndex);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetSensorMode", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetSensorMode64(int nCameraID, out int pModeIndex);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POASetUserCustomID", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetUserCustomID32(int nCameraID, IntPtr pCustomID, int len);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POASetUserCustomID", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POASetUserCustomID64(int nCameraID, IntPtr pCustomID, int len);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetGainOffset", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetGainOffset32(int nCameraID, out int pOffsetHighestDR, out int pOffsetUnityGain, out int pGainLowestRN, out int pOffsetLowestRN, out int pHCGain);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetGainOffset", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetGainOffset64(int nCameraID, out int pOffsetHighestDR, out int pOffsetUnityGain, out int pGainLowestRN, out int pOffsetLowestRN, out int pHCGain);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetGainsAndOffsets", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetGainsAndOffsets32(int nCameraID, out int pGainHighestDR, out int pHCGain, out int pUnityGain, out int pGainLowestRN, out int pOffsetHighestDR, out int pOffsetHCGain, out int pOffsetUnityGain, out int pOffsetLowestRN);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetGainsAndOffsets", CallingConvention = CallingConvention.Cdecl)]
        private static extern POAErrors POAGetGainsAndOffsets64(int nCameraID, out int pGainHighestDR, out int pHCGain, out int pUnityGain, out int pGainLowestRN, out int pOffsetHighestDR, out int pOffsetHCGain, out int pOffsetUnityGain, out int pOffsetLowestRN);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetErrorString", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr POAGetErrorString32(POAErrors err);

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetErrorString", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr POAGetErrorString64(POAErrors err);


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetAPIVersion", CallingConvention = CallingConvention.Cdecl)]
        private static extern int POAGetAPIVersion32();

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetAPIVersion", CallingConvention = CallingConvention.Cdecl)]
        private static extern int POAGetAPIVersion64();


        [DllImport("PlayerOneCamera.dll", EntryPoint = "POAGetSDKVersion", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr POAGetSDKVersion32();

        [DllImport("PlayerOneCamera_x64.dll", EntryPoint = "POAGetSDKVersion", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr POAGetSDKVersion64();



        //define c sharp interface

        // 32bit: IntPtr.Size == 4, 64bit: IntPtr.Size == 8
        // The value of this property is 4 in a 32-bit process, and 8 in a 64-bit process.
        // https://docs.microsoft.com/zh-cn/dotnet/api/system.intptr.size?view=netframework-3.5

        public static int POAGetCameraCount()
        { return IntPtr.Size == 8 ? POAGetCameraCount64() : POAGetCameraCount32(); }


        public static POAErrors POAGetCameraProperties(int nIndex, out POACameraProperties pProp)
        { return IntPtr.Size == 8 ? POAGetCameraProperties64(nIndex, out pProp) : POAGetCameraProperties32(nIndex, out pProp); }


        public static POAErrors POAGetCameraPropertiesByID(int nCameraID, out POACameraProperties pProp)
        { return IntPtr.Size == 8 ? POAGetCameraPropertiesByID64(nCameraID, out pProp) : POAGetCameraPropertiesByID32(nCameraID, out pProp); }


        public static POAErrors POAOpenCamera(int nCameraID)
        { return IntPtr.Size == 8 ? POAOpenCamera64(nCameraID) : POAOpenCamera32(nCameraID); }


        public static POAErrors POAInitCamera(int nCameraID)
        { return IntPtr.Size == 8 ? POAInitCamera64(nCameraID) : POAInitCamera32(nCameraID); }


        public static POAErrors POACloseCamera(int nCameraID)
        { return IntPtr.Size == 8 ? POACloseCamera64(nCameraID) : POACloseCamera32(nCameraID); }


        public static POAErrors POAGetConfigsCount(int nCameraID, out int pConfCount)
        { return IntPtr.Size == 8 ? POAGetConfigsCount64(nCameraID, out pConfCount) : POAGetConfigsCount32(nCameraID, out pConfCount); }


        public static POAErrors POAGetConfigAttributes(int nCameraID, int nConfIndex, out POAConfigAttributes pConfAttr)
        { return IntPtr.Size == 8 ? POAGetConfigAttributes64(nCameraID, nConfIndex, out pConfAttr) : POAGetConfigAttributes32(nCameraID, nConfIndex, out pConfAttr); }


        public static POAErrors POAGetConfigAttributesByConfigID(int nCameraID, POAConfig confID, out POAConfigAttributes pConfAttr)
        { return IntPtr.Size == 8 ? POAGetConfigAttributesByConfigID64(nCameraID, confID, out pConfAttr) : POAGetConfigAttributesByConfigID32(nCameraID, confID, out pConfAttr); }


        public static POAErrors POASetConfig(int nCameraID, POAConfig confID, POAConfigValue confValue, POABool isAuto)
        { return IntPtr.Size == 8 ? POASetConfig64(nCameraID, confID, confValue, isAuto) : POASetConfig32(nCameraID, confID, confValue, isAuto); }

        //overload POASetConfig begin-------
        public static POAErrors POASetConfig(int nCameraID, POAConfig confID, int nValue, bool isAuto)
        {
            if(IntPtr.Size == 8) 
            {
                POAValueType pConfValueType; // Must all variables be placed in 64 processes??? 
                POAErrors error = POAGetConfigValueType64(confID, out pConfValueType);
                if (error == POAErrors.POA_OK)
                {
                    if (pConfValueType != POAValueType.VAL_INT)
                    {
                        return POAErrors.POA_ERROR_INVALID_CONFIG;
                    }
                }
                else
                {
                    return error;
                }
                POAConfigValue confValue = new POAConfigValue();
                confValue.intValue = nValue;

                return POASetConfig64(nCameraID, confID, confValue, isAuto ? POABool.POA_TRUE : POABool.POA_FALSE);
            }
            else
            {
                POAValueType pConfValueType;
                POAErrors error = POAGetConfigValueType32(confID, out pConfValueType);
                if (error == POAErrors.POA_OK)
                {
                    if (pConfValueType != POAValueType.VAL_INT)
                    {
                        return POAErrors.POA_ERROR_INVALID_CONFIG;
                    }
                }
                else
                {
                    return error;
                }
                POAConfigValue confValue = new POAConfigValue();
                confValue.intValue = nValue;

                return POASetConfig32(nCameraID, confID, confValue, isAuto ? POABool.POA_TRUE : POABool.POA_FALSE);
            }
        }

        public static POAErrors POASetConfig(int nCameraID, POAConfig confID, double fValue, bool isAuto)
        {
            if (IntPtr.Size == 8)
            {
                POAValueType pConfValueType;
                POAErrors error = POAGetConfigValueType64(confID, out pConfValueType);
                if (error == POAErrors.POA_OK)
                {
                    if (pConfValueType != POAValueType.VAL_FLOAT)
                    {
                        return POAErrors.POA_ERROR_INVALID_CONFIG;
                    }
                }
                else
                {
                    return error;
                }
                POAConfigValue confValue = new POAConfigValue();
                confValue.floatValue = fValue;

                return POASetConfig64(nCameraID, confID, confValue, isAuto ? POABool.POA_TRUE : POABool.POA_FALSE);
            }
            else
            {
                POAValueType pConfValueType;
                POAErrors error = POAGetConfigValueType32(confID, out pConfValueType);
                if (error == POAErrors.POA_OK)
                {
                    if (pConfValueType != POAValueType.VAL_FLOAT)
                    {
                        return POAErrors.POA_ERROR_INVALID_CONFIG;
                    }
                }
                else
                {
                    return error;
                }
                POAConfigValue confValue = new POAConfigValue();
                confValue.floatValue = fValue;

                return POASetConfig32(nCameraID, confID, confValue, isAuto ? POABool.POA_TRUE : POABool.POA_FALSE);
            }
        }

        public static POAErrors POASetConfig(int nCameraID, POAConfig confID, bool isEnable)
        {
            if (IntPtr.Size == 8)
            {
                POAValueType pConfValueType;
                POAErrors error = POAGetConfigValueType64(confID, out pConfValueType);
                if (error == POAErrors.POA_OK)
                {
                    if (pConfValueType != POAValueType.VAL_BOOL)
                    {
                        return POAErrors.POA_ERROR_INVALID_CONFIG;
                    }
                }
                else
                {
                    return error;
                }
                POAConfigValue confValue = new POAConfigValue();
                confValue.boolValue = isEnable ? POABool.POA_TRUE : POABool.POA_FALSE;

                return POASetConfig64(nCameraID, confID, confValue, POABool.POA_FALSE);
            }
            else
            {
                POAValueType pConfValueType;
                POAErrors error = POAGetConfigValueType32(confID, out pConfValueType);
                if (error == POAErrors.POA_OK)
                {
                    if (pConfValueType != POAValueType.VAL_BOOL)
                    {
                        return POAErrors.POA_ERROR_INVALID_CONFIG;
                    }
                }
                else
                {
                    return error;
                }
                POAConfigValue confValue = new POAConfigValue();
                confValue.boolValue = isEnable ? POABool.POA_TRUE : POABool.POA_FALSE;

                return POASetConfig32(nCameraID, confID, confValue, POABool.POA_FALSE);
            }
        }
        //overload POASetConfig end-------


        public static POAErrors POAGetConfig(int nCameraID, POAConfig confID, out POAConfigValue confValue, out POABool isAuto)
        { return IntPtr.Size == 8 ? POAGetConfig64(nCameraID, confID, out confValue, out isAuto) : POAGetConfig32(nCameraID, confID, out confValue, out isAuto); }

        //overload POAGetConfig begin-------
        public static POAErrors POAGetConfig(int nCameraID, POAConfig confID, out int nValue, out bool isAuto)
        {
            if (IntPtr.Size == 8)
            {       
                POAConfigValue confValue = new POAConfigValue();
                POABool boolValue;

                POAErrors error = POAGetConfig64(nCameraID, confID, out confValue, out boolValue);
                if(error == POAErrors.POA_OK)
                {
                    nValue = confValue.intValue;
                    isAuto = boolValue == POABool.POA_TRUE ? true : false;
                    return POAErrors.POA_OK;
                }
                else
                {
                    nValue = 0;
                    isAuto = false;
                    return error;
                }
            }
            else
            {              
                POAConfigValue confValue = new POAConfigValue();
                POABool boolValue;

                POAErrors error = POAGetConfig32(nCameraID, confID, out confValue, out boolValue);
                if (error == POAErrors.POA_OK)
                {
                    nValue = confValue.intValue;
                    isAuto = boolValue == POABool.POA_TRUE ? true : false;
                    return POAErrors.POA_OK;
                }
                else
                {
                    nValue = 0;
                    isAuto = false;
                    return error;
                }
            }
        }

        public static POAErrors POAGetConfig(int nCameraID, POAConfig confID, out double fValue, out bool isAuto)
        {
            if (IntPtr.Size == 8)
            {
                POAConfigValue confValue = new POAConfigValue();
                POABool boolValue;

                POAErrors error = POAGetConfig64(nCameraID, confID, out confValue, out boolValue);
                if (error == POAErrors.POA_OK)
                {
                    fValue = confValue.floatValue;
                    isAuto = boolValue == POABool.POA_TRUE ? true : false;
                    return POAErrors.POA_OK;
                }
                else
                {
                    fValue = 0;
                    isAuto = false;
                    return error;
                }
            }
            else
            {
                POAConfigValue confValue = new POAConfigValue();
                POABool boolValue;

                POAErrors error = POAGetConfig32(nCameraID, confID, out confValue, out boolValue);
                if (error == POAErrors.POA_OK)
                {
                    fValue = confValue.floatValue;
                    isAuto = boolValue == POABool.POA_TRUE ? true : false;
                    return POAErrors.POA_OK;
                }
                else
                {
                    fValue = 0;
                    isAuto = false;
                    return error;
                }
            }
        }

        public static POAErrors POAGetConfig(int nCameraID, POAConfig confID, out bool isEnable)
        {
            if (IntPtr.Size == 8)
            {
                POAConfigValue confValue = new POAConfigValue();
                POABool boolValue;

                POAErrors error = POAGetConfig64(nCameraID, confID, out confValue, out boolValue);
                if (error == POAErrors.POA_OK)
                {
                    isEnable = confValue.boolValue == POABool.POA_TRUE ? true : false;
                    return POAErrors.POA_OK;
                }
                else
                {
                    isEnable = false;
                    return error;
                }
            }
            else
            {
                POAConfigValue confValue = new POAConfigValue();
                POABool boolValue;

                POAErrors error = POAGetConfig32(nCameraID, confID, out confValue, out boolValue);
                if (error == POAErrors.POA_OK)
                {
                    isEnable = confValue.boolValue == POABool.POA_TRUE ? true : false;
                    return POAErrors.POA_OK;
                }
                else
                {
                    isEnable = false;
                    return error;
                }
            }
        }
        //overload POAGetConfig end-------


        public static POAErrors POAGetConfigValueType(POAConfig confID, out POAValueType pConfValueType)
        { return IntPtr.Size == 8 ? POAGetConfigValueType64(confID, out pConfValueType) : POAGetConfigValueType32(confID, out pConfValueType); }


        public static POAErrors POASetImageStartPos(int nCameraID, int startX, int startY)
        { return IntPtr.Size == 8 ? POASetImageStartPos64(nCameraID, startX, startY) : POASetImageStartPos32(nCameraID, startX, startY); }


        public static POAErrors POAGetImageStartPos(int nCameraID, out int pStartX, out int pStartY)
        { return IntPtr.Size == 8 ? POAGetImageStartPos64(nCameraID, out pStartX, out pStartY) : POAGetImageStartPos32(nCameraID, out pStartX, out pStartY); }


        public static POAErrors POASetImageSize(int nCameraID, int width, int height)
        { return IntPtr.Size == 8 ? POASetImageSize64(nCameraID, width, height) : POASetImageSize32(nCameraID, width, height); }


        public static POAErrors POAGetImageSize(int nCameraID, out int pWidth, out int pHeight)
        { return IntPtr.Size == 8 ? POAGetImageSize64(nCameraID, out pWidth, out pHeight) : POAGetImageSize32(nCameraID, out pWidth, out pHeight); }


        public static POAErrors POASetImageBin(int nCameraID, int bin)
        { return IntPtr.Size == 8 ? POASetImageBin64(nCameraID, bin) : POASetImageBin32(nCameraID, bin); }


        public static POAErrors POAGetImageBin(int nCameraID, out int pBin)
        { return IntPtr.Size == 8 ? POAGetImageBin64(nCameraID, out pBin) : POAGetImageBin32(nCameraID, out pBin); }


        public static POAErrors POASetImageFormat(int nCameraID, POAImgFormat imgFormat)
        { return IntPtr.Size == 8 ? POASetImageFormat64(nCameraID, imgFormat) : POASetImageFormat32(nCameraID, imgFormat); }


        public static POAErrors POAGetImageFormat(int nCameraID, out POAImgFormat pImgFormat)
        { return IntPtr.Size == 8 ? POAGetImageFormat64(nCameraID, out pImgFormat) : POAGetImageFormat32(nCameraID, out pImgFormat); }


        public static POAErrors POAStartExposure(int nCameraID, POABool bSingleFrame)
        { return IntPtr.Size == 8 ? POAStartExposure64(nCameraID, bSingleFrame) : POAStartExposure32(nCameraID, bSingleFrame); }


        public static POAErrors POAStopExposure(int nCameraID)
        { return IntPtr.Size == 8 ? POAStopExposure64(nCameraID) : POAStopExposure32(nCameraID); }


        public static POAErrors POAGetCameraState(int nCameraID, out POACameraState pCameraState)
        { return IntPtr.Size == 8 ? POAGetCameraState64(nCameraID, out pCameraState) : POAGetCameraState32(nCameraID, out pCameraState); }


        public static POAErrors POAImageReady(int nCameraID, out POABool pIsReady)
        { return IntPtr.Size == 8 ? POAImageReady64(nCameraID, out pIsReady) : POAImageReady32(nCameraID, out pIsReady); }


        public static POAErrors POAGetImageData(int nCameraID, IntPtr pBuf, int nBufSize, int nTimeoutms)
        { return IntPtr.Size == 8 ? POAGetImageData64(nCameraID, pBuf, nBufSize, nTimeoutms) : POAGetImageData32(nCameraID, pBuf, nBufSize, nTimeoutms); }


        public static POAErrors POAGetDroppedImagesCount(int nCameraID, out int pDroppedCount)
        { return IntPtr.Size == 8 ? POAGetDroppedImagesCount64(nCameraID, out pDroppedCount) : POAGetDroppedImagesCount32(nCameraID, out pDroppedCount); }


        public static POAErrors POAGetSensorModeCount(int nCameraID, out int pModeCount)
        { return IntPtr.Size == 8 ? POAGetSensorModeCount64(nCameraID, out pModeCount) : POAGetSensorModeCount32(nCameraID, out pModeCount); }


        public static POAErrors POAGetSensorModeInfo(int nCameraID, int index, out POASensorModeInfo pSenModeInfo)
        { return IntPtr.Size == 8 ? POAGetSensorModeInfo64(nCameraID, index, out pSenModeInfo) : POAGetSensorModeInfo32(nCameraID, index, out pSenModeInfo); }


        public static POAErrors POASetSensorMode(int nCameraID, int modeIndex)
        { return IntPtr.Size == 8 ? POASetSensorMode64(nCameraID, modeIndex) : POASetSensorMode32(nCameraID, modeIndex); }


        public static POAErrors POAGetSensorMode(int nCameraID, out int pModeIndex)
        { return IntPtr.Size == 8 ? POAGetSensorMode64(nCameraID, out pModeIndex) : POAGetSensorMode32(nCameraID, out pModeIndex); }


        public static POAErrors POASetUserCustomID(int nCameraID, string pCustomID)
        { return IntPtr.Size == 8 ? POASetUserCustomID64(nCameraID, Marshal.StringToHGlobalAnsi(pCustomID), pCustomID.Length) : POASetUserCustomID32(nCameraID, Marshal.StringToHGlobalAnsi(pCustomID), pCustomID.Length); }


        public static POAErrors POAGetGainOffset(int nCameraID, out int pOffsetHighestDR, out int pOffsetUnityGain, out int pGainLowestRN, out int pOffsetLowestRN, out int pHCGain)
        { return IntPtr.Size == 8 ? POAGetGainOffset64(nCameraID, out pOffsetHighestDR, out pOffsetUnityGain, out pGainLowestRN, out pOffsetLowestRN, out pHCGain) : POAGetGainOffset32(nCameraID, out pOffsetHighestDR, out pOffsetUnityGain, out pGainLowestRN, out pOffsetLowestRN, out pHCGain); }


        public static POAErrors POAGetGainsAndOffsets(int nCameraID, out int pGainHighestDR, out int pHCGain, out int pUnityGain, out int pGainLowestRN, out int pOffsetHighestDR, out int pOffsetHCGain, out int pOffsetUnityGain, out int pOffsetLowestRN)
        { return IntPtr.Size == 8 ? POAGetGainsAndOffsets64(nCameraID, out pGainHighestDR, out pHCGain, out pUnityGain, out pGainLowestRN, out pOffsetHighestDR, out pOffsetHCGain, out pOffsetUnityGain, out pOffsetLowestRN) : POAGetGainsAndOffsets32(nCameraID, out pGainHighestDR, out pHCGain, out pUnityGain, out pGainLowestRN, out pOffsetHighestDR, out pOffsetHCGain, out pOffsetUnityGain, out pOffsetLowestRN); }


        public static string POAGetErrorString(POAErrors err)
        { return IntPtr.Size == 8 ? Marshal.PtrToStringAnsi(POAGetErrorString64(err)) : Marshal.PtrToStringAnsi(POAGetErrorString32(err)); }


        public static int POAGetAPIVersion()
        { return IntPtr.Size == 8 ? POAGetAPIVersion64() : POAGetAPIVersion32(); }


        public static string POAGetSDKVersion()
        { return IntPtr.Size == 8 ? Marshal.PtrToStringAnsi(POAGetSDKVersion64()) : Marshal.PtrToStringAnsi(POAGetSDKVersion32()); }
  
    }
}
