import java.util.Hashtable;
import java.util.HashMap;
import java.util.Map;
import java.lang.reflect.Method;
import java.nio.ByteBuffer;
import com.sun.jna.*;
import com.sun.jna.ptr.*;
import com.sun.jna.win32.*;
import com.sun.jna.Structure.FieldOrder;

/*
    Version: 59.29030.20250722

    We use JNA (https://github.com/java-native-access/jna) to call into the toupcam.dll/so/dylib API, the java class toupcam is a thin wrapper class to the native api.
    So the manual en.html(English) and hans.html(Simplified Chinese) are also applicable for programming with toupcam.java.
    See them in the 'doc' directory:
       (1) en.html, English
       (2) hans.html, Simplified Chinese
*/

/*
    Please distinguish between camera ID (camId) and camera SN:
        (a) SN is unique and persistent, fixed inside the camera and remains unchanged, and does not change with connection or system restart.
        (b) Camera ID (camId) may change due to connection or system restart. Enumerate the cameras to get the camera ID, and then call the Open function to pass in the camId parameter to open the camera.
*/

public class toupcam implements AutoCloseable {
    public final static long FLAG_CMOS                    = 0x00000001L;   /* cmos sensor */
    public final static long FLAG_CCD_PROGRESSIVE         = 0x00000002L;   /* progressive ccd sensor */
    public final static long FLAG_CCD_INTERLACED          = 0x00000004L;   /* interlaced ccd sensor */
    public final static long FLAG_ROI_HARDWARE            = 0x00000008L;   /* support hardware ROI */
    public final static long FLAG_MONO                    = 0x00000010L;   /* monochromatic */
    public final static long FLAG_BINSKIP_SUPPORTED       = 0x00000020L;   /* support bin/skip mode */
    public final static long FLAG_USB30                   = 0x00000040L;   /* usb3.0 */
    public final static long FLAG_TEC                     = 0x00000080L;   /* Thermoelectric Cooler */
    public final static long FLAG_USB30_OVER_USB20        = 0x00000100L;   /* usb3.0 camera connected to usb2.0 port */
    public final static long FLAG_ST4                     = 0x00000200L;   /* ST4 */
    public final static long FLAG_GETTEMPERATURE          = 0x00000400L;   /* support to get the temperature of the sensor */
    public final static long FLAG_HIGH_FULLWELL           = 0x00000800L;   /* high fullwell capacity */
    public final static long FLAG_RAW10                   = 0x00001000L;   /* pixel format, RAW 10bits */
    public final static long FLAG_RAW12                   = 0x00002000L;   /* pixel format, RAW 12bits */
    public final static long FLAG_RAW14                   = 0x00004000L;   /* pixel format, RAW 14bits */
    public final static long FLAG_RAW16                   = 0x00008000L;   /* pixel format, RAW 16bits */
    public final static long FLAG_FAN                     = 0x00010000L;   /* cooling fan */
    public final static long FLAG_TEC_ONOFF               = 0x00020000L;   /* Thermoelectric Cooler can be turn on or off, support to set the target temperature of TEC */
    public final static long FLAG_ISP                     = 0x00040000L;   /* ISP (Image Signal Processing) chip */
    public final static long FLAG_TRIGGER_SOFTWARE        = 0x00080000L;   /* support software trigger */
    public final static long FLAG_TRIGGER_EXTERNAL        = 0x00100000L;   /* support external trigger */
    public final static long FLAG_TRIGGER_SINGLE          = 0x00200000L;   /* only support trigger single: one trigger, one image */
    public final static long FLAG_BLACKLEVEL              = 0x00400000L;   /* support set and get the black level */
    public final static long FLAG_AUTO_FOCUS              = 0x00800000L;   /* support auto focus */
    public final static long FLAG_BUFFER                  = 0x01000000L;   /* frame buffer */
    public final static long FLAG_DDR                     = 0x02000000L;   /* use very large capacity DDR (Double Data Rate SDRAM) for frame buffer. The capacity is not less than one full frame */
    public final static long FLAG_CG                      = 0x04000000L;   /* support Conversion Gain mode: HCG, LCG */
    public final static long FLAG_YUV411                  = 0x08000000L;   /* pixel format, yuv411 */
    public final static long FLAG_VUYY                    = 0x10000000L;   /* pixel format, yuv422, VUYY */
    public final static long FLAG_YUV444                  = 0x20000000L;   /* pixel format, yuv444 */
    public final static long FLAG_RGB888                  = 0x40000000L;   /* pixel format, RGB888 */
    public final static long FLAG_RAW8                    = 0x80000000L;   /* pixel format, RAW 8 bits */
    public final static long FLAG_GMCY8                   = 0x0000000100000000L;  /* pixel format, GMCY, 8 bits */
    public final static long FLAG_GMCY12                  = 0x0000000200000000L;  /* pixel format, GMCY, 12 bits */
    public final static long FLAG_UYVY                    = 0x0000000400000000L;  /* pixel format, yuv422, UYVY */
    public final static long FLAG_CGHDR                   = 0x0000000800000000L;  /* Conversion Gain: HCG, LCG, HDR */
    public final static long FLAG_GLOBALSHUTTER           = 0x0000001000000000L;  /* global shutter */
    public final static long FLAG_FOCUSMOTOR              = 0x0000002000000000L;  /* support focus motor */
    public final static long FLAG_PRECISE_FRAMERATE       = 0x0000004000000000L;  /* support precise framerate & bandwidth, see OPTION_PRECISE_FRAMERATE & OPTION_BANDWIDTH */
    public final static long FLAG_HEAT                    = 0x0000008000000000L;  /* heat to prevent fogging up */
    public final static long FLAG_LOW_NOISE               = 0x0000010000000000L;  /* support low noise mode (Higher signal noise ratio, lower frame rate) */
    public final static long FLAG_LEVELRANGE_HARDWARE     = 0x0000020000000000L;  /* hardware level range, put(get)_LevelRangeV2 */
    public final static long FLAG_EVENT_HARDWARE          = 0x0000040000000000L;  /* hardware event, such as exposure start & stop */
    public final static long FLAG_LIGHTSOURCE             = 0x0000080000000000L;  /* embedded light source */
    public final static long FLAG_FILTERWHEEL             = 0x0000100000000000L;  /* astro filter wheel */
    public final static long FLAG_GIGE                    = 0x0000200000000000L;  /* 1 Gigabit GigE */
    public final static long FLAG_10GIGE                  = 0x0000400000000000L;  /* 10 Gigabit GigE */
    public final static long FLAG_5GIGE                   = 0x0000800000000000L;  /* 5 Gigabit GigE */
    public final static long FLAG_25GIGE                  = 0x0001000000000000L;  /* 2.5 Gigabit GigE */
    public final static long FLAG_AUTOFOCUSER             = 0x0002000000000000L;  /* astro auto focuser */
    public final static long FLAG_LIGHT_SOURCE            = 0x0004000000000000L;  /* stand alone light source */
    public final static long FLAG_CAMERALINK              = 0x0008000000000000L;  /* camera link */
    public final static long FLAG_CXP                     = 0x0010000000000000L;  /* CXP: CoaXPress */
    public final static long FLAG_RAW12PACK               = 0x0020000000000000L;  /* pixel format, RAW 12bits packed */
    public final static long FLAG_SELFTRIGGER             = 0x0040000000000000L;  /* self trigger */
    public final static long FLAG_RAW11                   = 0x0080000000000000L;  /* pixel format, RAW 11bits */
    public final static long FLAG_GHOPTO                  = 0x0100000000000000L;  /* ghopto sensor */
    public final static long FLAG_RAW10PACK               = 0x0200000000000000L;  /* pixel format, RAW 10bits packed */
    public final static long FLAG_USB32                   = 0x0400000000000000L;  /* usb3.2 */
    public final static long FLAG_USB32_OVER_USB30        = 0x0800000000000000L;  /* usb3.2 camera connected to usb3.0 port */
    
    public final static int EVENT_EXPOSURE                = 0x0001; /* exposure time or gain changed */
    public final static int EVENT_TEMPTINT                = 0x0002; /* white balance changed, Temp/Tint mode */
    public final static int EVENT_CHROME                  = 0x0003; /* reversed, do not use it */
    public final static int EVENT_IMAGE                   = 0x0004; /* live image arrived, use PullImageXXXX to get this image */
    public final static int EVENT_STILLIMAGE              = 0x0005; /* snap (still) frame arrived, use PullStillImageXXXX to get this frame */
    public final static int EVENT_WBGAIN                  = 0x0006; /* white balance changed, RGB Gain mode */
    public final static int EVENT_TRIGGERFAIL             = 0x0007; /* trigger failed */
    public final static int EVENT_BLACK                   = 0x0008; /* black balance changed */
    public final static int EVENT_FFC                     = 0x0009; /* flat field correction status changed */
    public final static int EVENT_DFC                     = 0x000a; /* dark field correction status changed */
    public final static int EVENT_ROI                     = 0x000b; /* roi changed */
    public final static int EVENT_LEVELRANGE              = 0x000c; /* level range changed */
    public final static int EVENT_AUTOEXPO_CONV           = 0x000d; /* auto exposure convergence */
    public final static int EVENT_AUTOEXPO_CONVFAIL       = 0x000e; /* auto exposure once mode convergence failed */
    public final static int EVENT_FPNC                    = 0x000f; /* fix pattern noise correction status changed */
    public final static int EVENT_ERROR                   = 0x0080; /* generic error */
    public final static int EVENT_DISCONNECTED            = 0x0081; /* camera disconnected */
    public final static int EVENT_NOFRAMETIMEOUT          = 0x0082; /* no frame timeout error */
    public final static int EVENT_MOTORPOS                = 0x0084; /* focus motor positon */
    public final static int EVENT_NOPACKETTIMEOUT         = 0x0085; /* no packet timeout */
    public final static int EVENT_EXPO_START              = 0x4000; /* hardware event: exposure start */
    public final static int EVENT_EXPO_STOP               = 0x4001; /* hardware event: exposure stop */
    public final static int EVENT_TRIGGER_ALLOW           = 0x4002; /* hardware event: next trigger allow */
    public final static int EVENT_HEARTBEAT               = 0x4003; /* hardware event: heartbeat, can be used to monitor whether the camera is alive */
    public final static int EVENT_TRIGGER_IN              = 0x4004; /* hardware event: trigger in */
    public final static int EVENT_FACTORY                 = 0x8001; /* restore factory settings */
    
    /* [RW] = Read/Write; [RO] = Read Only; [WO] = Write Only */
    public final static int OPTION_NOFRAME_TIMEOUT        = 0x01;       /* [RW] no frame timeout: 0 => disable, positive value (>= NOFRAME_TIMEOUT_MIN) => timeout milliseconds. default: disable */
    public final static int OPTION_THREAD_PRIORITY        = 0x02;       /* [RW] set the priority of the internal thread which grab data from the usb device.
                                                                             Win: iValue: 0 => THREAD_PRIORITY_NORMAL; 1 => THREAD_PRIORITY_ABOVE_NORMAL; 2 => THREAD_PRIORITY_HIGHEST; 3 => THREAD_PRIORITY_TIME_CRITICAL; default: 1; see: https://docs.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadpriority
                                                                             Linux & macOS: The high 16 bits for the scheduling policy, and the low 16 bits for the priority; see: https://linux.die.net/man/3/pthread_setschedparam
                                                                        */
    public final static int OPTION_RAW                    = 0x04;       /* [RW]
                                                                           0: RGB mode
                                                                           1: RAW mode, read the CMOS or CCD raw data
                                                                          -1: RAW mode, the difference from 1 is the execution of FFC, DFC, FPNC, black balance, and white balance
                                                                          default value: 0
                                                                        */
    public final static int OPTION_HISTOGRAM              = 0x05;       /* [RW] 0 = only one, 1 = continue mode */
    public final static int OPTION_BITDEPTH               = 0x06;       /* [RW] 0 = 8 bits mode, 1 = 16 bits mode */
    public final static int OPTION_FAN                    = 0x07;       /* [RW] 0 = turn off the cooling fan, [1, max] = fan speed, set to "-1" means to use default fan speed */
    public final static int OPTION_TEC                    = 0x08;       /* [RW] 0 = turn off the thermoelectric cooler, 1 = turn on the thermoelectric cooler */
    public final static int OPTION_LINEAR                 = 0x09;       /* [RW] 0 = turn off the builtin linear tone mapping, 1 = turn on the builtin linear tone mapping, default value: 1 */
    public final static int OPTION_CURVE                  = 0x0a;       /* [RW] 0 = turn off the builtin curve tone mapping, 1 = turn on the builtin polynomial curve tone mapping, 2 = logarithmic curve tone mapping, default value: 2 */
    public final static int OPTION_TRIGGER                = 0x0b;       /* [RW] 0 = video mode, 1 = software or simulated trigger mode, 2 = external trigger mode, 3 = external + software trigger, default value = 0 */
    public final static int OPTION_RGB                    = 0x0c;       /* [RW] 0 => RGB24; 1 => enable RGB48 format when bitdepth > 8; 2 => RGB32; 3 => 8 Bits Grey (only for mono camera); 4 => 16 Bits Grey (only for mono camera when bitdepth > 8); 5 => 64(RGB64) */
    public final static int OPTION_COLORMATIX             = 0x0d;       /* [RW] enable or disable the builtin color matrix, default value: 1 */
    public final static int OPTION_WBGAIN                 = 0x0e;       /* [RW] enable or disable the builtin white balance gain, default value: 1 */
    public final static int OPTION_TECTARGET              = 0x0f;       /* [RW] get or set the target temperature of the thermoelectric cooler, in 0.1 degree Celsius. For example, 125 means 12.5 degree Celsius, -35 means -3.5 degree Celsius. Set "-2730" or below means using the default for that model */
    public final static int OPTION_AUTOEXP_POLICY         = 0x10;       /* [RW] auto exposure policy:
                                                                              0: Exposure Only
                                                                              1: Exposure Preferred
                                                                              2: Gain Only
                                                                              3: Gain Preferred
                                                                           default value: 1
                                                                        */
    public final static int OPTION_FRAMERATE              = 0x11;       /* [RW] limit the frame rate, the default value 0 means no limit */
    public final static int OPTION_DEMOSAIC               = 0x12;       /* [RW] demosaic method for both video and still image: BILINEAR = 0, VNG(Variable Number of Gradients) = 1, PPG(Patterned Pixel Grouping) = 2, AHD(Adaptive Homogeneity Directed) = 3, EA(Edge Aware) = 4, see https://en.wikipedia.org/wiki/Demosaicing
                                                                             In terms of CPU usage, EA is the lowest, followed by BILINEAR, and the others are higher.
                                                                             default value: 0
                                                                        */
    public final static int OPTION_DEMOSAIC_VIDEO         = 0x13;       /* [RW] demosaic method for video */
    public final static int OPTION_DEMOSAIC_STILL         = 0x14;       /* [RW] demosaic method for still image */
    public final static int OPTION_BLACKLEVEL             = 0x15;       /* [RW] black level */
    public final static int OPTION_MULTITHREAD            = 0x16;       /* [RW] multithread image processing */
    public final static int OPTION_BINNING                = 0x17;       /* [RW] binning
                                                                               0x01: (no binning)
                                                                               n: (saturating add, n*n), 0x02(2*2), 0x03(3*3), 0x04(4*4), 0x05(5*5), 0x06(6*6), 0x07(7*7), 0x08(8*8). The Bitdepth of the data remains unchanged.
                                                                               0x40 | n: (unsaturated add, n*n, works only in RAW mode), 0x42(2*2), 0x43(3*3), 0x44(4*4), 0x45(5*5), 0x46(6*6), 0x47(7*7), 0x48(8*8). The Bitdepth of the data is increased. For example, the original data with bitdepth of 12 will increase the bitdepth by 2 bits and become 14 after 2*2 binning.
                                                                               0x80 | n: (average, n*n), 0x82(2*2), 0x83(3*3), 0x84(4*4), 0x85(5*5), 0x86(6*6), 0x87(7*7), 0x88(8*8). The Bitdepth of the data remains unchanged.
                                                                           The final image size is rounded down to an even number, such as 640/3 to get 212
                                                                        */
    public final static int OPTION_ROTATE                 = 0x18;       /* [RW] rotate clockwise: 0, 90, 180, 270 */
    public final static int OPTION_CG                     = 0x19;       /* [RW] Conversion Gain:
                                                                               0 = LCG
                                                                               1 = HCG
                                                                               2 = HDR (for camera with flag FLAG_CGHDR)
                                                                               2 = MCG (for camera with flag FLAG_GHOPTO)
                                                                        */
    public final static int OPTION_PIXEL_FORMAT           = 0x1a;       /* [RW] pixel format */
    public final static int OPTION_FFC                    = 0x1b;       /* [RW] flat field correction
                                                                                set:
                                                                                    0: disable
                                                                                    1: enable
                                                                                    -1: reset
                                                                                    (0xff000000 | n): set the average number to n, [1~255]
                                                                                get:
                                                                                    (val & 0xff): 0 => disable, 1 => enable, 2 => inited
                                                                                    ((val & 0xff00) >> 8): sequence
                                                                                    ((val & 0xff0000) >> 16): average number
                                                                        */
    public final static int OPTION_DDR_DEPTH              = 0x1c;       /* [RW] the number of the frames that DDR can cache
                                                                          1: DDR cache only one frame
                                                                          0: Auto:
                                                                              => one for video mode when auto exposure is enabled
                                                                              => full capacity for others
                                                                          1: DDR can cache frames to full capacity
                                                                        */
    public final static int OPTION_DFC                    = 0x1d;       /* [RW] dark field correction
                                                                                set:
                                                                                    0: disable
                                                                                    1: enable
                                                                                    -1: reset
                                                                                    (0xff000000 | n): set the average number to n, [1~255]
                                                                                get:
                                                                                    (val & 0xff): 0 => disable, 1 => enable, 2 => inited
                                                                                    ((val & 0xff00) >> 8): sequence
                                                                                    ((val & 0xff0000) >> 16): average number
                                                                        */
    public final static int OPTION_SHARPENING             = 0x1e;       /* [RW] Sharpening: (threshold << 24) | (radius << 16) | strength)
                                                                            strength: [0, 500], default: 0 (disable)
                                                                            radius: [1, 10]
                                                                            threshold: [0, 255]
                                                                        */
    public final static int OPTION_FACTORY                = 0x1f;       /* [WO] restore the factory settings */
    public final static int OPTION_TEC_VOLTAGE            = 0x20;       /* [RO] get the current TEC voltage in 0.1V, 59 mean 5.9V */
    public final static int OPTION_TEC_VOLTAGE_MAX        = 0x21;       /* [RW] TEC maximum voltage in 0.1V */
    public final static int OPTION_DEVICE_RESET           = 0x22;       /* [WO] reset usb device, simulate a replug */
    public final static int OPTION_UPSIDE_DOWN            = 0x23;       /* [RW] upsize down:
                                                                            1: yes
                                                                            0: no
                                                                            default: 1 (win), 0 (linux/macos)
                                                                        */
    public final static int OPTION_MOTORPOS               = 0x24;       /* [RW] focus motor positon */
    public final static int OPTION_AFMODE                 = 0x25;       /* [RW] auto focus mode, see ToupcamAFMode */
    public final static int OPTION_AFSTATUS               = 0x27;       /* [RO] auto focus status, see ToupcamAFStaus */
    public final static int OPTION_TESTPATTERN            = 0x28;       /* [RW] test pattern:
                                                                            0: off
                                                                            3: monochrome diagonal stripes
                                                                            5: monochrome vertical stripes
                                                                            7: monochrome horizontal stripes
                                                                            9: chromatic diagonal stripes
                                                                        */
    public final static int OPTION_AUTOEXP_THRESHOLD      = 0x29;       /* [RW] threshold of auto exposure, default value: 5, range = [2, 15] */
    public final static int OPTION_BYTEORDER              = 0x2a;       /* [RW] Byte order, BGR or RGB: 0 => RGB, 1 => BGR, default value: 1(Win), 0(macOS, Linux, Android) */
    public final static int OPTION_NOPACKET_TIMEOUT       = 0x2b;       /* [RW] no packet timeout: 0 => disable, positive value (>= NOPACKET_TIMEOUT_MIN) => timeout milliseconds. default: disable */
    public final static int OPTION_MAX_PRECISE_FRAMERATE  = 0x2c;       /* [RO] get the precise frame rate maximum value in 0.1 fps, such as 115 means 11.5 fps. E_NOTIMPL means not supported */
    public final static int OPTION_PRECISE_FRAMERATE      = 0x2d;       /* [RW] precise frame rate current value in 0.1 fps. use OPTION_MAX_PRECISE_FRAMERATE, OPTION_MIN_PRECISE_FRAMERATE to get the range. if the set value is out of range, E_INVALIDARG will be returned */
    public final static int OPTION_BANDWIDTH              = 0x2e;       /* [RW] bandwidth, [1-100]% */
    public final static int OPTION_RELOAD                 = 0x2f;       /* [RW] reload the last frame in trigger mode */
    public final static int OPTION_CALLBACK_THREAD        = 0x30;       /* [RW] dedicated thread for callback: 0 => disable, 1 => enable, default: 0 */
    public final static int OPTION_FRONTEND_DEQUE_LENGTH  = 0x31;       /* [RW] frontend (raw) frame buffer deque length, range: [2, 1024], default: 4
                                                                           All the memory will be pre-allocated when the camera starts, so, please attention to memory usage
                                                                        */
    public final static int OPTION_FRAME_DEQUE_LENGTH     = 0x31;       /* [RW] alias of OPTION_FRONTEND_DEQUE_LENGTH */
    public final static int OPTION_MIN_PRECISE_FRAMERATE  = 0x32;       /* [RO] get the precise frame rate minimum value in 0.1 fps, such as 15 means 1.5 fps */
    public final static int OPTION_SEQUENCER_ONOFF        = 0x33;       /* [RW] sequencer trigger: on/off */
    public final static int OPTION_SEQUENCER_NUMBER       = 0x34;       /* [RW] sequencer trigger: number, range = [1, 255] */
    public final static int OPTION_SEQUENCER_EXPOTIME     = 0x01000000; /* [RW] sequencer trigger: exposure time, iOption = OPTION_SEQUENCER_EXPOTIME | index, iValue = exposure time
                                                                             For example, to set the exposure time of the third group to 50ms, call:
                                                                                put_Option(OPTION_SEQUENCER_EXPOTIME | 3, 50000)
                                                                        */
    public final static int OPTION_SEQUENCER_EXPOGAIN     = 0x02000000; /* [RW] sequencer trigger: exposure gain, iOption = OPTION_SEQUENCER_EXPOGAIN | index, iValue = gain */
    public final static int OPTION_DENOISE                = 0x35;       /* [RW] denoise, strength range: [0, 100], 0 means disable */
    public final static int OPTION_HEAT_MAX               = 0x36;       /* [RO] get maximum level: heat to prevent fogging up */
    public final static int OPTION_HEAT                   = 0x37;       /* [RW] heat to prevent fogging up */
    public final static int OPTION_LOW_NOISE              = 0x38;       /* [RW] low noise mode (Higher signal noise ratio, lower frame rate): 1 => enable */
    public final static int OPTION_POWER                  = 0x39;       /* [RO] get power consumption, unit: milliwatt */
    public final static int OPTION_GLOBAL_RESET_MODE      = 0x3a;       /* [RW] global reset mode */
    public final static int OPTION_OPEN_ERRORCODE         = 0x3b;       /* [RO] get the open camera error code */
    public final static int OPTION_FLUSH                  = 0x3d;       /* [WO]
                                                                           1 = hard flush, discard frames cached by camera DDR (if any)
                                                                           2 = soft flush, discard frames cached by toupcam.dll (if any)
                                                                           3 = both flush
                                                                           Toupcam_Flush means 'both flush'
                                                                           return the number of soft flushed frames if successful, HRESULT if failed
                                                                        */
    public final static int OPTION_NUMBER_DROP_FRAME      = 0x3e;       /* [RO] get the number of frames that have been grabbed from the USB but dropped by the software */
    public final static int OPTION_DUMP_CFG               = 0x3f;       /* [RW] 0 = when camera is stopped, do not dump configuration automatically
                                                                           1 = when camera is stopped, dump configuration automatically
                                                                           -1 = explicitly dump configuration once
                                                                           default: 1
                                                                        */
    public final static int OPTION_DEFECT_PIXEL           = 0x40;       /* [RW] Defect Pixel Correction: 0 => disable, 1 => enable; default: 1 */
    public final static int OPTION_BACKEND_DEQUE_LENGTH   = 0x41;       /* [RW] backend (pipelined) frame buffer deque length (Only available in pull mode), range: [2, 1024], default: 3
                                                                           All the memory will be pre-allocated when the camera starts, so, please attention to memory usage
                                                                        */
    public final static int OPTION_LIGHTSOURCE_MAX        = 0x42;       /* [RO] get the light source range, [0 ~ max] */
    public final static int OPTION_LIGHTSOURCE            = 0x43;       /* [RW] light source */
    public final static int OPTION_HEARTBEAT              = 0x44;       /* [RW] Heartbeat interval in millisecond, range = [HEARTBEAT_MIN, HEARTBEAT_MAX], 0 = disable, default: disable */
    public final static int OPTION_FRONTEND_DEQUE_CURRENT = 0x45;       /* [RO] get the current number in frontend deque */
    public final static int OPTION_BACKEND_DEQUE_CURRENT  = 0x46;       /* [RO] get the current number in backend deque */
    public final static int OPTION_EVENT_HARDWARE         = 0x04000000; /* [RW] enable or disable hardware event: 0 => disable, 1 => enable; default: disable
                                                                               (1) iOption = OPTION_EVENT_HARDWARE, master switch for notification of all hardware events
                                                                               (2) iOption = OPTION_EVENT_HARDWARE | (event type), a specific type of sub-switch
                                                                           Only if both the master switch and the sub-switch of a particular type remain on are actually enabled for that type of event notification.
                                                                        */
    public final static int OPTION_PACKET_NUMBER          = 0x47;       /* [RO] get the received packet number */
    public final static int OPTION_FILTERWHEEL_SLOT       = 0x48;       /* [RW] filter wheel slot number */
    public final static int OPTION_FILTERWHEEL_POSITION   = 0x49;       /* [RW] filter wheel position:
                                                                              set:
                                                                                  -1: calibrate
                                                                                  val & 0xff: position between 0 and N-1, where N is the number of filter slots
                                                                                  (val >> 8) & 0x1: direction, 0 => clockwise spinning, 1 => auto direction spinning
                                                                              get:
                                                                                  -1: in motion
                                                                                  val: position arrived
                                                                        */
    public final static int OPTION_AUTOEXPOSURE_PERCENT   = 0x4a;       /* [RW] auto exposure percent to average:
                                                                              1~99: peak percent average
                                                                              0 or 100: full roi average, means "disabled"
                                                                        */
    public final static int OPTION_ANTI_SHUTTER_EFFECT    = 0x4b;       /* [RW] anti shutter effect: 1 => enable, 0 => disable; default: 0 */
    public final static int OPTION_CHAMBER_HT             = 0x4c;       /* [RO] get chamber humidity & temperature:
                                                                              high 16 bits: humidity, in 0.1%, such as: 325 means humidity is 32.5%
                                                                              low 16 bits: temperature, in 0.1 degrees Celsius, such as: 32 means 3.2 degrees Celsius
                                                                        */
    public final static int OPTION_ENV_HT                 = 0x4d;       /* [RO] get environment humidity & temperature */
    public final static int OPTION_EXPOSURE_PRE_DELAY     = 0x4e;       /* [RW] exposure signal pre-delay, microsecond */
    public final static int OPTION_EXPOSURE_POST_DELAY    = 0x4f;       /* [RW] exposure signal post-delay, microsecond */
    public final static int OPTION_AUTOEXPO_CONV          = 0x50;       /* [RO] get auto exposure convergence status: 1(YES) or 0(NO), -1(PENDING) */
    public final static int OPTION_AUTOEXPO_TRIGGER       = 0x51;       /* [RW] auto exposure on trigger mode: 0 => disable, 1 => enable; default: 0 */
    public final static int OPTION_LINE_PRE_DELAY         = 0x52;       /* [RW] specified line signal pre-delay, microsecond */
    public final static int OPTION_LINE_POST_DELAY        = 0x53;       /* [RW] specified line signal post-delay, microsecond */
    public final static int OPTION_TEC_VOLTAGE_MAX_RANGE  = 0x54;       /* [RO] get the tec maximum voltage range:
                                                                              high 16 bits: max
                                                                              low 16 bits: min
                                                                        */
    public final static int OPTION_HIGH_FULLWELL          = 0x55;       /* [RW] high fullwell capacity: 0 => disable, 1 => enable */
    public final static int OPTION_DYNAMIC_DEFECT         = 0x56;       /* [RW] dynamic defect pixel correction:
                                                                              dead pixel ratio, t1: (high 16 bits): [0, 100], means: [0.0, 1.0]
                                                                              hot pixel ratio, t2: (low 16 bits): [0, 100], means: [0.0, 1.0]
                                                                        */
    public final static int OPTION_HDR_KB                 = 0x57;       /* [RW] HDR synthesize
                                                                              K (high 16 bits): [1, 25500]
                                                                              B (low 16 bits): [0, 65535]
                                                                              0xffffffff => set to default
                                                                        */
    public final static int OPTION_HDR_THRESHOLD          = 0x58;       /* [RW] HDR synthesize
                                                                              threshold: [1, 4094]
                                                                              0xffffffff => set to default
                                                                        */
    public final static int OPTION_GIGETIMEOUT            = 0x5a;       /* [RW] For GigE cameras, the application periodically sends heartbeat signals to the camera to keep the connection to the camera alive.
                                                                           If the camera doesn't receive heartbeat signals within the time period specified by the heartbeat timeout counter, the camera resets the connection.
                                                                           When the application is stopped by the debugger, the application cannot send the heartbeat signals
                                                                               0 => auto: when the camera is opened, enable if no debugger is present or disable if debugger is present
                                                                               1 => enable
                                                                               2 => disable
                                                                               default: auto
                                                                        */
    public final static int OPTION_EEPROM_SIZE            = 0x5b;       /* [RO] get EEPROM size */
    public final static int OPTION_OVERCLOCK_MAX          = 0x5c;       /* [RO] get overclock range: [0, max] */
    public final static int OPTION_OVERCLOCK              = 0x5d;       /* [RW] overclock, default: 0 */
    public final static int OPTION_RESET_SENSOR           = 0x5e;       /* [WO] reset sensor */
    public final static int OPTION_ISP                    = 0x5f;       /* [RW] Enable hardware ISP: 0 => auto (disable in RAW mode, otherwise enable), 1 => enable, -1 => disable; default: 0 */
    public final static int OPTION_AUTOEXP_EXPOTIME_DAMP  = 0x60;       /* [RW] Auto exposure damping coefficient: step (thousandths). The larger the damping coefficient, the smoother and slower the exposure time changes */
    public final static int OPTION_AUTOEXP_GAIN_DAMP      = 0x61;       /* [RW] Auto exposure damping coefficient: step (thousandths). The larger the damping coefficient, the smoother and slower the gain changes */
    public final static int OPTION_MOTOR_NUMBER           = 0x62;       /* [RW] range: [1, 20] */
    public final static int OPTION_MOTOR_POS              = 0x10000000; /* [RW] range: [1, 702] */
    public final static int OPTION_PSEUDO_COLOR_START     = 0x63;       /* [RW] Pseudo: start color, BGR format */
    public final static int OPTION_PSEUDO_COLOR_END       = 0x64;       /* [RW] Pseudo: end color, BGR format */
    public final static int OPTION_PSEUDO_COLOR_ENABLE    = 0x65;       /* [RW] Pseudo: -1 => custom: use startcolor & endcolor to generate the colormap
                                                                                0 => disable
                                                                                1 => spot
                                                                                2 => spring
                                                                                3 => summer
                                                                                4 => autumn
                                                                                5 => winter
                                                                                6 => bone
                                                                                7 => jet
                                                                                8 => rainbow
                                                                                9 => deepgreen
                                                                                10 => ocean
                                                                                11 => cool
                                                                                12 => hsv
                                                                                13 => pink
                                                                                14 => hot
                                                                                15 => parula
                                                                                16 => magma
                                                                                17 => inferno
                                                                                18 => plasma
                                                                                19 => viridis
                                                                                20 => cividis
                                                                                21 => twilight
                                                                                22 => twilight_shifted
                                                                                23 => turbo
                                                                                24 => red
                                                                                25 => green
                                                                                26 => blue
                                                                        */
    public final static int OPTION_LOW_POWERCONSUMPTION   = 0x66;       /* [RW] Low Power Consumption: 0 => disable, 1 => enable */
    public final static int OPTION_FPNC                   = 0x67;       /* [RW] Fix Pattern Noise Correction
                                                                                set:
                                                                                    0: disable
                                                                                    1: enable
                                                                                    -1: reset
                                                                                    (0xff000000 | n): set the average number to n, [1~255]
                                                                                get:
                                                                                    (val & 0xff): 0 => disable, 1 => enable, 2 => inited
                                                                                    ((val & 0xff00) >> 8): sequence
                                                                                    ((val & 0xff0000) >> 16): average number
                                                                        */
    public final static int OPTION_OVEREXP_POLICY          = 0x68;      /* [RW] Auto exposure over exposure policy: when overexposed,
                                                                                0 => directly reduce the exposure time/gain to the minimum value; or
                                                                                1 => reduce exposure time/gain in proportion to current and target brightness; or
                                                                                n(n>1) => first adjust the exposure time to (maximum automatic exposure time * maximum automatic exposure gain) * n / 1000, and then adjust according to the strategy of 1
                                                                            The advantage of policy 0 is that the convergence speed is faster, but there is black screen.
                                                                            Policy 1 avoids the black screen, but the convergence speed is slower.
                                                                            Default: 0
                                                                        */
    public final static int OPTION_READOUT_MODE           = 0x69;       /* [RW] Readout mode: 0 = IWR (Integrate While Read), 1 = ITR (Integrate Then Read)
                                                                            The working modes of the detector readout circuit can be divided into two types: ITR and IWR. Using the IWR readout mode can greatly increase the frame rate. In the ITR mode, the integration of the (n+1)th frame starts after all the data of the nth frame are read out, while in the IWR mode, the data of the nth frame is read out at the same time when the (n+1)th frame is integrated
                                                                        */
    public final static int OPTION_TAILLIGHT              = 0x6a;       /* [RW] Turn on/off tail Led light: 0 => off, 1 => on; default: on */
    public final static int OPTION_LENSSTATE              = 0x6b;       /* [RW] Load/Save lens state to EEPROM: 0 => load, 1 => save */
    public final static int OPTION_AWB_CONTINUOUS         = 0x6c;       /* [RW] Auto White Balance: continuous mode
                                                                            0:  disable (default)
                                                                            n>0: every n millisecond(s)
                                                                            n<0: every -n frame
                                                                        */
    public final static int OPTION_TECTARGET_RANGE        = 0x6d;       /* [RO] TEC target range: min(low 16 bits) = (short)(val & 0xffff), max(high 16 bits) = (short)((val >> 16) & 0xffff) */
    public final static int OPTION_CDS                    = 0x6e;       /* [RW] Correlated Double Sampling: 0~100 */
    public final static int OPTION_LOW_POWER_EXPOTIME     = 0x6f;       /* [RW] Low Power Consumption: Enable if exposure time is greater than the set value */
    public final static int OPTION_ZERO_OFFSET            = 0x70;       /* [RW] Sensor output offset to zero: 0 => disable, 1 => eanble; default: 0 */
    public final static int OPTION_GVCP_TIMEOUT           = 0x71;       /* [RW] GVCP Timeout: millisecond, range = [3, 75], default: 15
                                                                             Unless in very special circumstances, generally no modification is required, just use the default value
                                                                        */
    public final static int OPTION_GVCP_RETRY             = 0x72;       /* [RW] GVCP Retry: range = [2, 8], default: 4
                                                                             Unless in very special circumstances, generally no modification is required, just use the default value
                                                                        */
    public final static int OPTION_GVSP_WAIT_PERCENT      = 0x73;       /* [RW] GVSP wait percent: range = [0, 100], default = (trigger mode: 100, realtime: 0, other: 1) */
    public final static int OPTION_RESET_SEQ_TIMESTAMP    = 0x74;       /* [WO] Reset to 0: 1 => seq; 2 => timestamp; 3 => both */
    public final static int OPTION_TRIGGER_CANCEL_MODE    = 0x75;       /* [RW] Trigger cancel mode: 0 => no frame, 1 => output frame; default: 0 */
    public final static int OPTION_MECHANICALSHUTTER      = 0x76;       /* [RW] Mechanical shutter: 0 => open, 1 => close; default: 0 */
    public final static int OPTION_LINE_TIME              = 0x77;       /* [RO] Line-time of sensor in nanosecond */
    public final static int OPTION_ZERO_PADDING           = 0x78;       /* [RW] Zero padding: 0 => high, 1 => low; default: 0 */
    public final static int OPTION_UPTIME                 = 0x79;       /* [RO] device uptime in millisecond */
    public final static int OPTION_BITRANGE               = 0x7a;       /* [RW] Bit range: [0, 8] */
    public final static int OPTION_MODE_SEQ_TIMESTAMP     = 0x7b;       /* [RW] Mode of seq & timestamp: 0 => reset to 0 automatically; 1 => never reset automatically; default: 0 */
    public final static int OPTION_TIMED_TRIGGER_NUM      = 0x7c;       /* [RW] Timed trigger number */
    public final static int OPTION_TIMED_TRIGGER_LOW      = 0x20000000; /* [RW] Timed trigger: lower 32 bits of 64-bit integer, nanosecond since epoch (00:00:00 UTC on Thursday, 1 January 1970, see https://en.wikipedia.org/wiki/Unix_time) */
    public final static int OPTION_TIMED_TRIGGER_HIGH     = 0x40000000; /* [RW] Timed trigger: high 32 bits. The lower 32 bits must be set first, followed by the higher 32 bits */
    public final static int OPTION_AUTOEXP_THLD_TRIGGER   = 0x7d;       /* [RW] trigger threshold of auto exposure */
    public final static int OPTION_LANE                   = 0x7e;       /* [RW] */
    public final static int OPTION_VOLTAGEBIAS            = 0x7f;       /* [RW] Voltage bias */
    public final static int OPTION_VOLTAGEBIAS_RANGE      = 0x80;       /* [RO] Voltage bias range: min (low 16bits), max (high 16bits) */
    public final static int OPTION_READ_TIME              = 0x81;       /* [RO] */
    public final static int OPTION_ALL_EXPO_TIME          = 0x82;       /* [RO] */
    public final static int OPTION_CAMERA_PERIOD          = 0x83;       /* [RO] */
    public final static int OPTION_FRONTEND_FULL          = 0x84;       /* [RO] get the number of frontend deque full */
    public final static int OPTION_BACKEND_FULL           = 0x85;       /* [RO] get the number of backend deque full */
    public final static int OPTION_GPS                    = 0x86;       /* [RO] gps status: 0 => not supported; -1 => gps device offline; 1 => gps device online */
    public final static int OPTION_LINE_LENGTH            = 0x87;       /* [RW] Line length in pixel clock */
    public final static int OPTION_SCAN_DIRECTION         = 0x88;       /* [RW] Scan direction: 0(forward), 1(reverse), 2(alternate) */
    public final static int OPTION_BLACKLEVEL_AUTOADJUST  = 0x89;       /* [RW] Black level automatic adjustment function: 0: off, 1: on
                                                                                This setting turn on/off black level auto adjust function by OB(Optical Black) level.
                                                                                In case of long exposure and so on, OB level is offset by leak or any other reason.
                                                                                Because of it, if the adjustment becomes a problem, this setting is introduced for one of the solution.
                                                                        */
    public final static int OPTION_DIGITAL_GAIN           = 0x1001;     /* [RW] digital gain */

    public final static int PIXELFORMAT_RAW8              = 0x00;
    public final static int PIXELFORMAT_RAW10             = 0x01;
    public final static int PIXELFORMAT_RAW12             = 0x02;
    public final static int PIXELFORMAT_RAW14             = 0x03;
    public final static int PIXELFORMAT_RAW16             = 0x04;
    public final static int PIXELFORMAT_YUV411            = 0x05;
    public final static int PIXELFORMAT_VUYY              = 0x06;
    public final static int PIXELFORMAT_YUV444            = 0x07;
    public final static int PIXELFORMAT_RGB888            = 0x08;
    public final static int PIXELFORMAT_GMCY8             = 0x09;   /* map to RGGB 8 bits */
    public final static int PIXELFORMAT_GMCY12            = 0x0a;   /* map to RGGB 12 bits */
    public final static int PIXELFORMAT_UYVY              = 0x0b;
    public final static int PIXELFORMAT_RAW12PACK         = 0x0c;
    public final static int PIXELFORMAT_RAW11             = 0x0d;
    public final static int PIXELFORMAT_HDR8HL            = 0x0e;   /* HDR, Bitdepth: 8, Conversion Gain: High + Low */
    public final static int PIXELFORMAT_HDR10HL           = 0x0f;   /* HDR, Bitdepth: 10, Conversion Gain: High + Low */
    public final static int PIXELFORMAT_HDR11HL           = 0x10;   /* HDR, Bitdepth: 11, Conversion Gain: High + Low */
    public final static int PIXELFORMAT_HDR12HL           = 0x11;   /* HDR, Bitdepth: 12, Conversion Gain: High + Low */
    public final static int PIXELFORMAT_HDR14HL           = 0x12;   /* HDR, Bitdepth: 14, Conversion Gain: High + Low */
    public final static int PIXELFORMAT_RAW10PACK         = 0x13;
    
    public final static int FRAMEINFO_FLAG_SEQ                = 0x00000001; /* frame sequence number */
    public final static int FRAMEINFO_FLAG_TIMESTAMP          = 0x00000002; /* timestamp */
    public final static int FRAMEINFO_FLAG_EXPOTIME           = 0x00000004; /* exposure time */
    public final static int FRAMEINFO_FLAG_EXPOGAIN           = 0x00000008; /* exposure gain */
    public final static int FRAMEINFO_FLAG_BLACKLEVEL         = 0x00000010; /* black level */
    public final static int FRAMEINFO_FLAG_SHUTTERSEQ         = 0x00000020; /* sequence shutter counter */
    public final static int FRAMEINFO_FLAG_GPS                = 0x00000040; /* GPS */
    public final static int FRAMEINFO_FLAG_AUTOFOCUS          = 0x00000080; /* auto focus: uLum & uFV */
    public final static int FRAMEINFO_FLAG_COUNT              = 0x00000100; /* timecount, framecount, tricount */
    public final static int FRAMEINFO_FLAG_MECHANICALSHUTTER  = 0x00000200; /* Mechanical shutter: closed */
    public final static int FRAMEINFO_FLAG_STILL              = 0x00008000; /* still image */
    public final static int FRAMEINFO_FLAG_CG                 = 0x00010000; /* Conversion Gain: High */
    
    public final static int IOCONTROLTYPE_GET_SUPPORTEDMODE            = 0x01; /* 0x01 => Input, 0x02 => Output, (0x01 | 0x02) => support both Input and Output */
    public final static int IOCONTROLTYPE_GET_GPIODIR                  = 0x03; /* 0x00 => Input, 0x01 => Output */
    public final static int IOCONTROLTYPE_SET_GPIODIR                  = 0x04;
    public final static int IOCONTROLTYPE_GET_FORMAT                   = 0x05; /*
                                                                                  0x00 => not connected
                                                                                  0x01 => Tri-state: Tri-state mode (Not driven)
                                                                                  0x02 => TTL: TTL level signals
                                                                                  0x03 => LVDS: LVDS level signals
                                                                                  0x04 => RS422: RS422 level signals
                                                                                  0x05 => Opto-coupled
                                                                               */
    public final static int IOCONTROLTYPE_SET_FORMAT                   = 0x06;
    public final static int IOCONTROLTYPE_GET_OUTPUTINVERTER           = 0x07; /* boolean, only support output signal */
    public final static int IOCONTROLTYPE_SET_OUTPUTINVERTER           = 0x08;
    public final static int IOCONTROLTYPE_GET_INPUTACTIVATION          = 0x09; /* 0x00 => Rising edge, 0x01 => Negative, 0x02 => Level high, 0x03 => Level low */
    public final static int IOCONTROLTYPE_SET_INPUTACTIVATION          = 0x0a;
    public final static int IOCONTROLTYPE_GET_DEBOUNCERTIME            = 0x0b; /* debouncer time in microseconds, range: [0, 20000] */
    public final static int IOCONTROLTYPE_SET_DEBOUNCERTIME            = 0x0c;
    public final static int IOCONTROLTYPE_GET_TRIGGERSOURCE            = 0x0d; /*
                                                                                  0x00 => Opto-isolated input
                                                                                  0x01 => GPIO0
                                                                                  0x02 => GPIO1
                                                                                  0x03 => Counter
                                                                                  0x04 => PWM
                                                                                  0x05 => Software
                                                                               */
    public final static int IOCONTROLTYPE_SET_TRIGGERSOURCE            = 0x0e;
    public final static int IOCONTROLTYPE_GET_TRIGGERDELAY             = 0x0f; /* Trigger delay time in microseconds, range: [0, 5000000] */
    public final static int IOCONTROLTYPE_SET_TRIGGERDELAY             = 0x10;
    public final static int IOCONTROLTYPE_GET_BURSTCOUNTER             = 0x11; /* Burst Counter, range: [1 ~ 65535] */
    public final static int IOCONTROLTYPE_SET_BURSTCOUNTER             = 0x12;
    public final static int IOCONTROLTYPE_GET_COUNTERSOURCE            = 0x13; /* 0x00 => Opto-isolated input, 0x01 => GPIO0, 0x02=> GPIO1 */
    public final static int IOCONTROLTYPE_SET_COUNTERSOURCE            = 0x14;
    public final static int IOCONTROLTYPE_GET_COUNTERVALUE             = 0x15; /* Counter Value, range: [1 ~ 65535] */
    public final static int IOCONTROLTYPE_SET_COUNTERVALUE             = 0x16;
    public final static int IOCONTROLTYPE_SET_RESETCOUNTER             = 0x18;
    public final static int IOCONTROLTYPE_GET_PWM_FREQ                 = 0x19; /* PWM Frequency */
    public final static int IOCONTROLTYPE_SET_PWM_FREQ                 = 0x1a;
    public final static int IOCONTROLTYPE_GET_PWM_DUTYRATIO            = 0x1b; /* PWM Duty Ratio */
    public final static int IOCONTROLTYPE_SET_PWM_DUTYRATIO            = 0x1c;
    public final static int IOCONTROLTYPE_GET_PWMSOURCE                = 0x1d; /* PWM Source: 0x00 => Opto-isolated input, 0x01 => GPIO0, 0x02=> GPIO1 */
    public final static int IOCONTROLTYPE_SET_PWMSOURCE                = 0x1e;
    public final static int IOCONTROLTYPE_GET_OUTPUTMODE               = 0x1f; /*
                                                                                  0x00 => Frame Trigger Wait
                                                                                  0x01 => Exposure Active
                                                                                  0x02 => Strobe
                                                                                  0x03 => User output
                                                                                  0x04 => Counter Output
                                                                                  0x05 => Timer Output
                                                                               */
    public final static int IOCONTROLTYPE_SET_OUTPUTMODE               = 0x20;
    public final static int IOCONTROLTYPE_GET_STROBEDELAYMODE          = 0x21; /* boolean, 1 => delay, 0 => pre-delay; compared to exposure active signal */
    public final static int IOCONTROLTYPE_SET_STROBEDELAYMODE          = 0x22;
    public final static int IOCONTROLTYPE_GET_STROBEDELAYTIME          = 0x23; /* Strobe delay or pre-delay time in microseconds, range: [0, 5000000] */
    public final static int IOCONTROLTYPE_SET_STROBEDELAYTIME          = 0x24;
    public final static int IOCONTROLTYPE_GET_STROBEDURATION           = 0x25; /* Strobe duration time in microseconds, range: [0, 5000000] */
    public final static int IOCONTROLTYPE_SET_STROBEDURATION           = 0x26;
    public final static int IOCONTROLTYPE_GET_USERVALUE                = 0x27; /*
                                                                                  bit0 => Opto-isolated output
                                                                                  bit1 => GPIO0 output
                                                                                  bit2 => GPIO1 output
                                                                               */
    public final static int IOCONTROLTYPE_SET_USERVALUE                = 0x28;
    public final static int IOCONTROLTYPE_GET_UART_ENABLE              = 0x29; /* enable: 1=> on; 0=> off */
    public final static int IOCONTROLTYPE_SET_UART_ENABLE              = 0x2a;
    public final static int IOCONTROLTYPE_GET_UART_BAUDRATE            = 0x2b; /* baud rate: 0 => 9600; 1 => 19200; 2 => 38400; 3 => 57600; 4 => 115200 */
    public final static int IOCONTROLTYPE_SET_UART_BAUDRATE            = 0x2c;
    public final static int IOCONTROLTYPE_GET_UART_LINEMODE            = 0x2d; /* line mode: 0 => TX(GPIO_0)/RX(GPIO_1); 1 => TX(GPIO_1)/RX(GPIO_0) */
    public final static int IOCONTROLTYPE_SET_UART_LINEMODE            = 0x2e;
    public final static int IOCONTROLTYPE_GET_EXPO_ACTIVE_MODE         = 0x2f; /* exposure time signal: 0 => specified line, 1 => common exposure time */
    public final static int IOCONTROLTYPE_SET_EXPO_ACTIVE_MODE         = 0x30;
    public final static int IOCONTROLTYPE_GET_EXPO_START_LINE          = 0x31; /* exposure start line, default: 0 */
    public final static int IOCONTROLTYPE_SET_EXPO_START_LINE          = 0x32;
    public final static int IOCONTROLTYPE_GET_EXPO_END_LINE            = 0x33; /* exposure end line, default: 0
                                                                                  end line must be no less than start line
                                                                               */
    public final static int IOCONTROLTYPE_SET_EXPO_END_LINE            = 0x34;
    public final static int IOCONTROLTYPE_GET_EXEVT_ACTIVE_MODE        = 0x35; /* exposure event: 0 => specified line, 1 => common exposure time */
    public final static int IOCONTROLTYPE_SET_EXEVT_ACTIVE_MODE        = 0x36;
    public final static int IOCONTROLTYPE_GET_OUTPUTCOUNTERVALUE       = 0x37; /* Output Counter Value, range: [0 ~ 65535] */
    public final static int IOCONTROLTYPE_SET_OUTPUTCOUNTERVALUE       = 0x38;
    public final static int IOCONTROLTYPE_SET_OUTPUT_PAUSE             = 0x3a; /* Output pause: 1 => puase, 0 => unpause */
    public final static int IOCONTROLTYPE_GET_INPUT_STATE              = 0x3b; /* Input state: 0 (low level) or 1 (high level) */
    public final static int IOCONTROLTYPE_GET_USER_PULSE_HIGH          = 0x3d; /* User pulse high level time: us */
    public final static int IOCONTROLTYPE_SET_USER_PULSE_HIGH          = 0x3e;
    public final static int IOCONTROLTYPE_GET_USER_PULSE_LOW           = 0x3f; /* User pulse low level time: us */
    public final static int IOCONTROLTYPE_SET_USER_PULSE_LOW           = 0x40;
    public final static int IOCONTROLTYPE_GET_USER_PULSE_NUMBER        = 0x41; /* User pulse number: default 0 */
    public final static int IOCONTROLTYPE_SET_USER_PULSE_NUMBER        = 0x42;
    public final static int IOCONTROLTYPE_GET_EXTERNAL_TRIGGER_NUMBER  = 0x43; /* External trigger number */
    public final static int IOCONTROLTYPE_GET_DEBOUNCER_TRIGGER_NUMBER = 0x45; /* Trigger signal number after debounce */
    public final static int IOCONTROLTYPE_GET_EFFECTIVE_TRIGGER_NUMBER = 0x47; /* Effective trigger signal number */
    
    public final static int IOCONTROL_DELAYTIME_MAX                    = 5 * 1000 * 1000;
    
    /* AAF: Astro Auto Focuser */
    public final static int AAF_SETPOSITION     = 0x01;
    public final static int AAF_GETPOSITION     = 0x02;
    public final static int AAF_SETZERO         = 0x03;
    public final static int AAF_SETDIRECTION    = 0x05;
    public final static int AAF_GETDIRECTION    = 0x06;
    public final static int AAF_SETMAXINCREMENT = 0x07;
    public final static int AAF_GETMAXINCREMENT = 0x08;
    public final static int AAF_SETFINE         = 0x09;
    public final static int AAF_GETFINE         = 0x0a;
    public final static int AAF_SETCOARSE       = 0x0b;
    public final static int AAF_GETCOARSE       = 0x0c;
    public final static int AAF_SETBUZZER       = 0x0d;
    public final static int AAF_GETBUZZER       = 0x0e;
    public final static int AAF_SETBACKLASH     = 0x0f;
    public final static int AAF_GETBACKLASH     = 0x10;
    public final static int AAF_GETAMBIENTTEMP  = 0x12;
    public final static int AAF_GETTEMP         = 0x14;  /* in 0.1 degrees Celsius, such as: 32 means 3.2 degrees Celsius */
    public final static int AAF_ISMOVING        = 0x16;
    public final static int AAF_HALT            = 0x17;
    public final static int AAF_SETMAXSTEP      = 0x1b;
    public final static int AAF_GETMAXSTEP      = 0x1c;
    public final static int AAF_GETSTEPSIZE     = 0x1e;
    public final static int AAF_RANGEMIN        = 0xfd;  /* Range: min value */
    public final static int AAF_RANGEMAX        = 0xfe;  /* Range: max value */
    public final static int AAF_RANGEDEF        = 0xff;  /* Range: default value */
    
    /* hardware level range mode */
    public final static int LEVELRANGE_MANUAL        = 0x0000;   /* manual */
    public final static int LEVELRANGE_ONCE          = 0x0001;   /* once */
    public final static int LEVELRANGE_CONTINUE      = 0x0002;   /* continue */
    public final static int LEVELRANGE_DISABLE       = 0x0003;   /* disable */
    public final static int LEVELRANGE_ROI           = 0xffff;   /* update roi rect only */
    
    public final static int EXPOGAIN_DEF              = 100;      /* exposure gain, default value */
    public final static int EXPOGAIN_MIN              = 100;      /* exposure gain, minimum value */
    public final static int TEMP_DEF                  = 6503;     /* color temperature, default value */
    public final static int TEMP_MIN                  = 2000;     /* color temperature, minimum value */
    public final static int TEMP_MAX                  = 15000;    /* color temperature, maximum value */
    public final static int TINT_DEF                  = 1000;     /* tint */
    public final static int TINT_MIN                  = 200;      /* tint */
    public final static int TINT_MAX                  = 2500;     /* tint */
    public final static int HUE_DEF                   = 0;        /* hue */
    public final static int HUE_MIN                   = -180;     /* hue */
    public final static int HUE_MAX                   = 180;      /* hue */
    public final static int SATURATION_DEF            = 128;      /* saturation */
    public final static int SATURATION_MIN            = 0;        /* saturation */
    public final static int SATURATION_MAX            = 255;      /* saturation */
    public final static int BRIGHTNESS_DEF            = 0;        /* brightness */
    public final static int BRIGHTNESS_MIN            = -255;     /* brightness */
    public final static int BRIGHTNESS_MAX            = 255;      /* brightness */
    public final static int CONTRAST_DEF              = 0;        /* contrast */
    public final static int CONTRAST_MIN              = -255;     /* contrast */
    public final static int CONTRAST_MAX              = 255;      /* contrast */
    public final static int GAMMA_DEF                 = 100;      /* gamma */
    public final static int GAMMA_MIN                 = 20;       /* gamma */
    public final static int GAMMA_MAX                 = 180;      /* gamma */
    public final static int AETARGET_DEF              = 120;      /* target of auto exposure */
    public final static int AETARGET_MIN              = 16;       /* target of auto exposure */
    public final static int AETARGET_MAX              = 220;      /* target of auto exposure */
    public final static int WBGAIN_DEF                = 0;        /* white balance gain */
    public final static int WBGAIN_MIN                = -127;     /* white balance gain */
    public final static int WBGAIN_MAX                = 127;      /* white balance gain */
    public final static int BLACKLEVEL_MIN            = 0;        /* minimum black level */
    public final static int BLACKLEVEL8_MAX           = 31;       /* maximum black level for bitdepth = 8 */
    public final static int BLACKLEVEL10_MAX          = 31 * 4;   /* maximum black level for bitdepth = 10 */
    public final static int BLACKLEVEL11_MAX          = 31 * 8;   /* maximum black level for bitdepth = 11 */
    public final static int BLACKLEVEL12_MAX          = 31 * 16;  /* maximum black level for bitdepth = 12 */
    public final static int BLACKLEVEL14_MAX          = 31 * 64;  /* maximum black level for bitdepth = 14 */
    public final static int BLACKLEVEL16_MAX          = 31 * 256; /* maximum black level for bitdepth = 16 */
    public final static int SHARPENING_STRENGTH_DEF   = 0;        /* sharpening strength */
    public final static int SHARPENING_STRENGTH_MIN   = 0;        /* sharpening strength */
    public final static int SHARPENING_STRENGTH_MAX   = 500;      /* sharpening strength */
    public final static int SHARPENING_RADIUS_DEF     = 2;        /* sharpening radius */
    public final static int SHARPENING_RADIUS_MIN     = 1;        /* sharpening radius */
    public final static int SHARPENING_RADIUS_MAX     = 10;       /* sharpening radius */
    public final static int SHARPENING_THRESHOLD_DEF  = 0;        /* sharpening threshold */
    public final static int SHARPENING_THRESHOLD_MIN  = 0;        /* sharpening threshold */
    public final static int SHARPENING_THRESHOLD_MAX  = 255;      /* sharpening threshold */
    public final static int AUTOEXPO_THRESHOLD_DEF    = 5;        /* auto exposure threshold */
    public final static int AUTOEXPO_THRESHOLD_MIN    = 2;        /* auto exposure threshold */
    public final static int AUTOEXPO_THRESHOLD_MAX    = 15;       /* auto exposure threshold */
    public final static int AUTOEXPO_THLD_TRIGGER_DEF = 5;        /* auto exposure trigger threshold */
    public final static int AUTOEXPO_THLD_TRIGGER_MIN = 2;        /* auto exposure trigger threshold */
    public final static int AUTOEXPO_THLD_TRIGGER_MAX = 64;       /* auto exposure trigger threshold */
    public final static int AUTOEXPO_DAMP_DEF         = 0;        /* auto exposure damping coefficient: thousandths */
    public final static int AUTOEXPO_DAMP_MIN         = 0;        /* auto exposure damping coefficient: thousandths */
    public final static int AUTOEXPO_DAMP_MAX         = 1000;     /* auto exposure damping coefficient: thousandths */
    public final static int BANDWIDTH_DEF             = 100;      /* bandwidth */
    public final static int BANDWIDTH_MIN             = 1;        /* bandwidth */
    public final static int BANDWIDTH_MAX             = 100;      /* bandwidth */
    public final static int DENOISE_DEF               = 0;        /* denoise */
    public final static int DENOISE_MIN               = 0;        /* denoise */
    public final static int DENOISE_MAX               = 100;      /* denoise */
    public final static int HEARTBEAT_MIN             = 100;      /* millisecond */
    public final static int HEARTBEAT_MAX             = 10000;    /* millisecond */
    public final static int AE_PERCENT_MIN            = 0;        /* auto exposure percent; 0 or 100 => full roi average, means "disabled" */
    public final static int AE_PERCENT_MAX            = 100;
    public final static int AE_PERCENT_DEF            = 1;        /* auto exposure percent: enabled, percentage = 1% */
    public final static int NOPACKET_TIMEOUT_MIN      = 500;      /* no packet timeout minimum: 500ms */
    public final static int NOFRAME_TIMEOUT_MIN       = 500;      /* no frame timeout minimum: 500ms */
    public final static int DYNAMIC_DEFECT_T1_MIN     = 0;        /* dynamic defect pixel correction, dead pixel ratio: the smaller the dead ratio is, the more stringent the conditions for processing dead pixels are, and fewer pixels will be processed */
    public final static int DYNAMIC_DEFECT_T1_MAX     = 100;      /* means: 1.0 */
    public final static int DYNAMIC_DEFECT_T1_DEF     = 90;       /* means: 0.9 */
    public final static int DYNAMIC_DEFECT_T2_MIN     = 0;        /* dynamic defect pixel correction, hot pixel ratio: the smaller the hot ratio is, the more stringent the conditions for processing hot pixels are, and fewer pixels will be processed */
    public final static int DYNAMIC_DEFECT_T2_MAX     = 100;
    public final static int DYNAMIC_DEFECT_T2_DEF     = 90;
    public final static int HDR_K_MIN                 = 1;        /* HDR synthesize */
    public final static int HDR_K_MAX                 = 25500;
    public final static int HDR_B_MIN                 = 0;
    public final static int HDR_B_MAX                 = 65535;
    public final static int HDR_THRESHOLD_MIN         = 0;
    public final static int HDR_THRESHOLD_MAX         = 4094;
    public final static int CDS_MIN                   = 0;        /* Correlated Double Sampling */
    public final static int CDS_MAX                   = 100;
    
    public final static int FLASH_SIZE               = 0x00;    /* query total size */
    public final static int FLASH_EBLOCK             = 0x01;    /* query erase block size */
    public final static int FLASH_RWBLOCK            = 0x02;    /* query read/write block size */
    public final static int FLASH_STATUS             = 0x03;    /* query status */
    public final static int FLASH_READ               = 0x04;    /* read */
    public final static int FLASH_WRITE              = 0x05;    /* write */
    public final static int FLASH_ERASE              = 0x06;    /* erase */

    static public int TDIBWIDTHBYTES(int bits) {
        return ((bits + 31) & (~31)) / 8;
    }

    public static class HRESULTException extends Exception {
        /* HRESULT: error code */
        public final static int S_OK            = 0x00000000; /* Success */
        public final static int S_FALSE         = 0x00000001; /* Success with noop */ /* Remark: Different from S_OK, such as internal values and user-set values have coincided, equivalent to noop */
        public final static int E_UNEXPECTED    = 0x8000ffff; /* Catastrophic failure */ /* Remark: Generally indicates that the conditions are not met, such as calling put_Option setting some options that do not support modification when the camera is running, and so on */
        public final static int E_NOTIMPL       = 0x80004001; /* Not supported or not implemented */ /* Remark: This feature is not supported on this model of camera */
        public final static int E_NOINTERFACE   = 0x80004002;
        public final static int E_ACCESSDENIED  = 0x80070005; /* Permission denied */ /* Remark: The program on Linux does not have permission to open the USB device, please enable udev rules file or run as root */
        public final static int E_OUTOFMEMORY   = 0x8007000e; /* Out of memory */
        public final static int E_INVALIDARG    = 0x80070057; /* One or more arguments are not valid */
        public final static int E_POINTER       = 0x80004003; /* Pointer that is not valid */ /* Remark: Pointer is NULL */
        public final static int E_FAIL          = 0x80004005; /* Generic failure */
        public final static int E_WRONG_THREAD  = 0x8001010e; /* Call function in the wrong thread */
        public final static int E_GEN_FAILURE   = 0x8007001f; /* Device not functioning */ /* Remark: It is generally caused by hardware errors, such as cable problems, USB port problems, poor contact, camera hardware damage, etc */
        public final static int E_BUSY          = 0x800700aa; /* The requested resource is in use */ /* Remark: The camera is already in use, such as duplicated opening/starting the camera, or being used by other application, etc */
        public final static int E_PENDING       = 0x8000000a; /* The data necessary to complete this operation is not yet available */ /* Remark: No data is available at this time */
        public final static int E_TIMEOUT       = 0x8001011f; /* This operation returned because the timeout period expired */
        public final static int E_UNREACH       = 0x80072743; /* Network is unreachable */
        
        private final int _hresult;
        
        public HRESULTException(int hresult) {
            _hresult = hresult;
        }
        
        public int getHRESULT() {
            return _hresult;
        }
        
        @Override
        public String toString() {
            return toString(_hresult);
        }

        public static String toString(int hresult) {
            switch (hresult) {
                case E_INVALIDARG:
                    return "One or more arguments are not valid";
                case E_NOTIMPL: /* Remark: This feature is not supported on this model of camera */
                    return "Not supported or not implemented";
                case E_POINTER: /* Remark: Pointer is NULL */
                    return "Pointer that is not valid";
                case E_UNEXPECTED:/* Remark: Generally indicates that the conditions are not met, such as calling put_Option setting some options that do not support modification when the camera is running, and so on */
                    return "Catastrophic failure";
                case E_ACCESSDENIED: /* Remark: The program on Linux does not have permission to open the USB device, please enable udev rules file or run as root */
                    return "Permission denied";
                case E_OUTOFMEMORY:
                    return "Out of memory";
                case E_WRONG_THREAD:
                    return "Call function in the wrong thread";
                case E_GEN_FAILURE: /* Remark: It is generally caused by hardware errors, such as cable problems, USB port problems, poor contact, camera hardware damage, etc */
                    return "Device not functioning";
                case E_BUSY: /* Remark: The camera is already in use, such as duplicated opening/starting the camera, or being used by other application, etc */
                    return "The requested resource is in use";
                case E_PENDING:/* Remark: No data is available at this time */
                    return "The data necessary to complete this operation is not yet available";
                case E_TIMEOUT:
                    return "This operation returned because the timeout period expired";
                case E_UNREACH:
                    return "Network is unreachable";
                default:
                    return "Unspecified failure";
            }
        }
    }

    public static String PixelFormatName(int pixelFormat) {
        return _lib.Toupcam_get_PixelFormatName(pixelFormat);
    }

    private static int errCheck(int hresult) throws HRESULTException {
        if (hresult < 0)
            throw new HRESULTException(hresult);
        return hresult;
    }
    
    public static class Resolution {
        public int width;
        public int height;
    }
    
    public static class ModelV2 {
        public String name;         /* model name */
        public long flag;           /* FLAG_xxx, 64 bits */
        public int maxspeed;        /* number of speed level, same as Toupcam_get_MaxSpeed(), the speed range = [0, maxspeed], closed interval */
        public int preview;         /* number of preview resolution, same as get_ResolutionNumber() */
        public int still;           /* number of still resolution, same as get_StillResolutionNumber() */
        public int maxfanspeed;     /* maximum fan speed, fan speed range = [0, max], closed interval */
        public int ioctrol;         /* number of input/output control */
        public float xpixsz;        /* physical pixel size in micrometer */
        public float ypixsz;        /* physical pixel size in micrometer */
        public Resolution[] res;
    }
    
    public static class DeviceV2 {
        public String displayname;  /* display name */
        public String id;           /* unique and opaque id of a connected camera */
        public ModelV2 model;
    }
    
    @FieldOrder({ "width", "height", "flag", "seq", "timestamp", "shutterseq", "expotime", "expogain", "blacklevel" })
    public static class FrameInfoV3 extends Structure {
        public int  width;
        public int  height;
        public int  flag;           /* FRAMEINFO_FLAG_xxxx */
        public int  seq;            /* frame sequence number */
        public long timestamp;      /* microsecond */
        public int shutterseq;      /* sequence shutter counter */
        public int  expotime;       /* expotime */
        public short expogain;      /* expogain */
        public short blacklevel;    /* black level */
    }

    @FieldOrder({ "utcstart", "utcend", "longitude", "latitude", "altitude", "satellite", "reserved" })
    public static class Gps extends Structure {
        public long  utcstart;    /* exposure start time: nanosecond since epoch (00:00:00 UTC on Thursday, 1 January 1970, see https://en.wikipedia.org/wiki/Unix_time) */
        public long  utcend;      /* exposure end time */
        public int   longitude;   /* millionth of a degree, 0.000001 degree */
        public int   latitude;
        public int   altitude;    /* millimeter */
        public short satellite;   /* number of satellite */
        public short reserved;    /* not used */
    }

    @FieldOrder({ "v3", "reserved", "uLum", "uFV", "timecount", "framecount", "tricount", "gps" })
    public static class FrameInfoV4 extends Structure {
        public FrameInfoV3 v3;
        public int reserved; /* not used */
        public int uLum;
        public long uFV;
        public long timecount;
        public int framecount;
        public int tricount;
        public Gps gps;
    };

    @Deprecated
    @FieldOrder({ "width", "height", "flag", "seq", "timestamp" })
    public static class FrameInfoV2 extends Structure {
        public int  width;
        public int  height;
        public int  flag;           /* FRAMEINFO_FLAG_xxxx */
        public int  seq;            /* frame sequence number */
        public long timestamp;      /* microsecond */
    }

    @FieldOrder({ "sensingLeft", "sensingTop", "sensingWidth", "sensingHeight", "hThreshold", "lThreshold", "expoTime", "expoGain", "hCount", "lCount", "reserved" })
    public static class SelfTrigger extends Structure {
        public int sensingLeft, sensingTop, sensingWidth, sensingHeight; /* Sensing Area */
        public int hThreshold, lThreshold; /* threshold High side, threshold Low side */
        public int expoTime; /* Exposure Time */
        public short expoGain; /* Exposure Gain */
        public short hCount, lCount; /* Count threshold High side, Count threshold Low side, thousandths of Sensing Area */
        public short reserved;
    };

    public static class LensInfo
    {
        public short lensID;
        public byte  lensType;
        public byte  statusAfmf;      /* LENS_AF = 0x00,  LENS_MF = 0x80 */

        public short maxFocalLength;
        public short curFocalLength;
        public short minFocalLength;

        public short farFM;           /* focus motor, absolute value */
        public short curFM;           /* current focus motor */
        public short nearFM;

        public short maxFocusDistance;
        public short minFocusDistance;

        public byte curAM;
        public byte maxAM;            /* maximum Aperture, mimimum F# */
        public byte minAM;            /* mimimum Aperture, maximum F# */
        public byte posAM;            /* used for set aperture motor to posAM, it is an index */
        public int posFM;             /* used for set focus motor to posFM */

        public String[] arrayFN;
        public String lensName;
    };

    public final static int AFMode_CALIBRATE = 0x0; /* lens calibration mode */
    public final static int AFMode_MANUAL = 0x1;    /* manual focus mode */
    public final static int AFMode_ONCE = 0x2;      /* onepush focus mode */
    public final static int AFMode_AUTO = 0x3;      /* autofocus mode */
    public final static int AFMode_NONE = 0x4;      /* no active selection of focus mode */
    public final static int AFMode_IDLE = 0x5;

    public final static int AFStatus_NA = 0x0;              /* Not available */
    public final static int AFStatus_PEAKPOINT = 0x1;       /* Focus completed, find the focus position */
    public final static int AFStatus_DEFOCUS = 0x2;         /* End of focus, defocus */
    public final static int AFStatus_NEAR = 0x3;            /* Focusing ended, object too close */
    public final static int AFStatus_FAR = 0x4;             /* Focusing ended, object too far */
    public final static int AFStatus_ROICHANGED = 0x5;      /* Focusing ends, roi changes */
    public final static int AFStatus_SCENECHANGED = 0x6;    /* Focusing ends, scene changes */
    public final static int AFStatus_MODECHANGED = 0x7;     /* The end of focusing and the change in focusing mode is usually determined by the user moderator */
    public final static int AFStatus_UNFINISH = 0x8;        /* The focus is not complete. At the beginning of focusing, it will be set as incomplete */

    @FieldOrder({ "AF_Mode", "AF_Status", "AF_LensAP_Update_Flag" })
    public static class AFState extends Structure {
        public int AF_Mode;
        public int AF_Status;
        public byte AF_LensAP_Update_Flag;  /* mark for whether the lens aperture is calibrated */
    };

    @FieldOrder({ "imax", "imin", "idef", "imaxabs", "iminabs", "zoneh", "zonev" })
    public static class FocusMotor extends Structure {
        public int imax;            /* maximum auto focus sensor board positon */
        public int imin;            /* minimum auto focus sensor board positon */
        public int idef;            /* conjugate calibration positon */
        public int imaxabs;         /* maximum absolute auto focus sensor board positon, micrometer */
        public int iminabs;         /* maximum absolute auto focus sensor board positon, micrometer */
        public int zoneh;           /* zone horizontal */
        public int zonev;           /* zone vertical */
    }
    
    @FieldOrder({ "left", "top", "right", "bottom" })
    private static class RECT extends Structure {
        public int left, top, right, bottom;
    }
    
    private interface CLib {
        Pointer Toupcam_Version();
        int Toupcam_EnumV2(Pointer ptr);
        int Toupcam_EnumWithName(Pointer ptr);
        Pointer Toupcam_OpenByIndex(int index);
        void Toupcam_Close(Pointer h);
        int Toupcam_PullImageV4(Pointer h, Pointer pImageData, int bStill, int bits, int rowPitch, FrameInfoV4 pInfo);
        int Toupcam_WaitImageV4(Pointer h, int nWaitMS, Pointer pImageData, int bStill, int bits, int rowPitch, FrameInfoV4 pInfo);
        @Deprecated int Toupcam_PullImageV3(Pointer h, Pointer pImageData, int bStill, int bits, int rowPitch, FrameInfoV3 pInfo);
        @Deprecated int Toupcam_WaitImageV3(Pointer h, int nWaitMS, Pointer pImageData, int bStill, int bits, int rowPitch, FrameInfoV3 pInfo);
        @Deprecated int Toupcam_PullImageV2(Pointer h, Pointer pImageData, int bits, FrameInfoV2 pInfo);
        @Deprecated int Toupcam_PullStillImageV2(Pointer h, Pointer pImageData, int bits, FrameInfoV2 pInfo);
        @Deprecated int Toupcam_PullImageWithRowPitchV2(Pointer h, Pointer pImageData, int bits, int rowPitch, FrameInfoV2 pInfo);
        @Deprecated int Toupcam_PullStillImageWithRowPitchV2(Pointer h, Pointer pImageData, int bits, int rowPitch, FrameInfoV2 pInfo);
        int Toupcam_Stop(Pointer h);
        int Toupcam_Pause(Pointer h, int bPause);
        int Toupcam_Snap(Pointer h, int nResolutionIndex);
        int Toupcam_SnapN(Pointer h, int nResolutionIndex, int nNumber);
        int Toupcam_SnapR(Pointer h, int nResolutionIndex, int nNumber);
        int Toupcam_Trigger(Pointer h, short nNumber);
        int Toupcam_TriggerSyncV4(Pointer h, int nWaitMS, Pointer pImageData, int bits, int rowPitch, FrameInfoV4 pInfo);
        @Deprecated int Toupcam_TriggerSync(Pointer h, int nWaitMS, Pointer pImageData, int bits, int rowPitch, FrameInfoV3 pInfo);
        int Toupcam_put_Size(Pointer h, int nWidth, int nHeight);
        int Toupcam_get_Size(Pointer h, IntByReference nWidth, IntByReference nHeight);
        int Toupcam_put_eSize(Pointer h, int nResolutionIndex);
        int Toupcam_get_eSize(Pointer h, IntByReference nResolutionIndex);
        int Toupcam_get_FinalSize(Pointer h, IntByReference nWidth, IntByReference nHeight);
        int Toupcam_get_ResolutionNumber(Pointer h);
        int Toupcam_get_Resolution(Pointer h, int nResolutionIndex, IntByReference pWidth, IntByReference pHeight);
        int Toupcam_get_ResolutionRatio(Pointer h, int nResolutionIndex, IntByReference pNumerator, IntByReference pDenominator);
        int Toupcam_get_Field(Pointer h);
        int Toupcam_get_RawFormat(Pointer h, IntByReference nFourCC, IntByReference bitdepth);
        int Toupcam_put_RealTime(Pointer h, int val);
        int Toupcam_get_RealTime(Pointer h, IntByReference val);
        int Toupcam_Flush(Pointer h);
        int Toupcam_get_Temperature(Pointer h, ShortByReference pTemperature);
        int Toupcam_put_Temperature(Pointer h, short nTemperature);
        int Toupcam_get_Roi(Pointer h, IntByReference pxOffset, IntByReference pyOffset, IntByReference pxWidth, IntByReference pyHeight);
        int Toupcam_put_Roi(Pointer h, int xOffset, int yOffset, int xWidth, int yHeight);
        int Toupcam_put_RoiN(Pointer h, int[] xOffset, int[] yOffset, int[] xWidth, int[] yHeight, int Num);
        int Toupcam_put_XY(Pointer h, int x, int y);
        int Toupcam_get_AutoExpoEnable(Pointer h, IntByReference bAutoExposure);
        int Toupcam_put_AutoExpoEnable(Pointer h, int bAutoExposure);
        int Toupcam_get_AutoExpoTarget(Pointer h, ShortByReference Target);
        int Toupcam_put_AutoExpoTarget(Pointer h, short Target);
        int Toupcam_put_AutoExpoRange(Pointer h, int maxTime, int minTime, short maxGain, short minGain);
        int Toupcam_get_AutoExpoRange(Pointer h, IntByReference maxTime, IntByReference minTime, ShortByReference maxGain, ShortByReference minGain);
        int Toupcam_put_MaxAutoExpoTimeAGain(Pointer h, int maxTime, short maxGain);
        int Toupcam_get_MaxAutoExpoTimeAGain(Pointer h, IntByReference maxTime, ShortByReference maxGain);
        int Toupcam_put_MinAutoExpoTimeAGain(Pointer h, int minTime, short minGain);
        int Toupcam_get_MinAutoExpoTimeAGain(Pointer h, IntByReference minTime, ShortByReference minGain);
        int Toupcam_get_ExpoTime(Pointer h, IntByReference Time)/* in microseconds */;
        int Toupcam_put_ExpoTime(Pointer h, int Time)/* inmicroseconds */;
        int Toupcam_get_RealExpoTime(Pointer h, IntByReference Time); /* actual exposure time */
        int Toupcam_get_ExpTimeRange(Pointer h, IntByReference nMin, IntByReference nMax, IntByReference nDef);
        int Toupcam_get_ExpoAGain(Pointer h, ShortByReference Gain);/* percent, such as 300 */
        int Toupcam_put_ExpoAGain(Pointer h, short Gain);/* percent */
        int Toupcam_get_ExpoAGainRange(Pointer h, ShortByReference nMin, ShortByReference nMax, ShortByReference nDef);
        int Toupcam_put_LevelRange(Pointer h, short[] aLow, short[] aHigh);
        int Toupcam_get_LevelRange(Pointer h, short[] aLow, short[] aHigh);
        int Toupcam_put_LevelRangeV2(Pointer h, short mode, RECT pRoiRect, short[] aLow, short[] aHigh);
        int Toupcam_get_LevelRangeV2(Pointer h, ShortByReference pMode, RECT pRoiRect, short[] aLow, short[] aHigh);
        int Toupcam_put_Hue(Pointer h, int Hue);
        int Toupcam_get_Hue(Pointer h, IntByReference Hue);
        int Toupcam_put_Saturation(Pointer h, int Saturation);
        int Toupcam_get_Saturation(Pointer h, IntByReference Saturation);
        int Toupcam_put_Brightness(Pointer h, int Brightness);
        int Toupcam_get_Brightness(Pointer h, IntByReference Brightness);
        int Toupcam_get_Contrast(Pointer h, IntByReference Contrast);
        int Toupcam_put_Contrast(Pointer h, int Contrast);
        int Toupcam_get_Gamma(Pointer h, IntByReference Gamma);
        int Toupcam_put_Gamma(Pointer h, int Gamma);
        int Toupcam_get_Chrome(Pointer h, IntByReference bChrome);    /* monochromatic mode */
        int Toupcam_put_Chrome(Pointer h, int bChrome);
        int Toupcam_get_VFlip(Pointer h, IntByReference bVFlip);  /* vertical flip */
        int Toupcam_put_VFlip(Pointer h, int bVFlip);
        int Toupcam_get_HFlip(Pointer h, IntByReference bHFlip);
        int Toupcam_put_HFlip(Pointer h, int bHFlip);  /* horizontal flip */
        int Toupcam_get_Negative(Pointer h, IntByReference bNegative);
        int Toupcam_put_Negative(Pointer h, int bNegative);
        int Toupcam_put_Speed(Pointer h, short nSpeed);
        int Toupcam_get_Speed(Pointer h, ShortByReference pSpeed);
        int Toupcam_get_MaxSpeed(Pointer h);/* get the maximum speed, "Frame Speed Level", speed range = [0, max] */
        int Toupcam_get_MaxBitDepth(Pointer h);/* get the max bitdepth of this camera, such as 8, 10, 12, 14, 16 */
        int Toupcam_get_FanMaxSpeed(Pointer h);/* get the maximum fan speed, the fan speed range = [0, max], closed interval */
        int Toupcam_put_HZ(Pointer h, int nHZ);
        int Toupcam_get_HZ(Pointer h, IntByReference nHZ);
        int Toupcam_put_Mode(Pointer h, int bSkip); /* skip or bin */
        int Toupcam_get_Mode(Pointer h, IntByReference bSkip);
        int Toupcam_put_TempTint(Pointer h, int nTemp, int nTint);
        int Toupcam_get_TempTint(Pointer h, IntByReference nTemp, IntByReference nTint);
        int Toupcam_put_WhiteBalanceGain(Pointer h, int[] aGain);
        int Toupcam_get_WhiteBalanceGain(Pointer h, int[] aGain);
        int Toupcam_put_BlackBalance(Pointer h, short[] aSub);
        int Toupcam_get_BlackBalance(Pointer h, short[] aSub);
        int Toupcam_put_AWBAuxRect(Pointer h, RECT pAuxRect);
        int Toupcam_get_AWBAuxRect(Pointer h, RECT pAuxRect);
        int Toupcam_put_AEAuxRect(Pointer h, RECT pAuxRect);
        int Toupcam_get_AEAuxRect(Pointer h, RECT pAuxRect);
        int Toupcam_put_ABBAuxRect(Pointer h, RECT pAuxRect);
        int Toupcam_get_ABBAuxRect(Pointer h, RECT pAuxRect);
        int Toupcam_get_MonoMode(Pointer h);
        int Toupcam_get_StillResolutionNumber(Pointer h);
        int Toupcam_get_StillResolution(Pointer h, int nResolutionIndex, IntByReference pWidth, IntByReference pHeight);
        int Toupcam_get_Revision(Pointer h, ShortByReference pRevision);
        int Toupcam_get_SerialNumber(Pointer h, Pointer sn);
        int Toupcam_get_FwVersion(Pointer h, Pointer fwver);
        int Toupcam_get_HwVersion(Pointer h, Pointer hwver);
        int Toupcam_get_FpgaVersion(Pointer h, Pointer fpgaver);
        int Toupcam_get_ProductionDate(Pointer h, Pointer pdate);
        int Toupcam_get_PixelSize(Pointer h, int nResolutionIndex, FloatByReference x, FloatByReference y);
        int Toupcam_AwbOnce(Pointer h, Pointer funTT, Pointer ctxTT);
        int Toupcam_AwbInit(Pointer h, Pointer funWB, Pointer ctxWB);
        int Toupcam_LevelRangeAuto(Pointer h);
        int Toupcam_AbbOnce(Pointer h, Pointer funBB, Pointer ctxBB);
        int Toupcam_put_LEDState(Pointer h, short iLed, short iState, short iPeriod);
        int Toupcam_write_EEPROM(Pointer h, int addr, Pointer pBuffer, int nBufferLen);
        int Toupcam_read_EEPROM(Pointer h, int addr, Pointer pBuffer, int nBufferLen);
        int Toupcam_rwc_Flash(Pointer h, int action, int addr, int len, Pointer pData);
        int Toupcam_write_Pipe(Pointer h, int pipeId, Pointer pBuffer, int nBufferLen);
        int Toupcam_read_Pipe(Pointer h, int pipeId, Pointer pBuffer, int nBufferLen);
        int Toupcam_feed_Pipe(Pointer h, int pipeId);
        int Toupcam_write_UART(Pointer h, Pointer pBuffer, int nBufferLen);
        int Toupcam_read_UART(Pointer h, Pointer pBuffer, int nBufferLen);
        int Toupcam_put_Option(Pointer h, int iOption, int iValue);
        int Toupcam_get_Option(Pointer h, int iOption, IntByReference iValue);
        int Toupcam_get_PixelFormatSupport(Pointer h, byte cmd, IntByReference pixelFormat);
        String Toupcam_get_PixelFormatName(int val);
        /* Hardware Binning
         * Value: 1x1, 2x2, etc
         * Method: Average, Add
         */
        int Toupcam_put_Binning(Pointer h, String Value, String Method);
        int Toupcam_get_Binning(Pointer h, PointerByReference Value, PointerByReference Method);
        int Toupcam_get_BinningNumber(Pointer h);
        int Toupcam_get_BinningValue(Pointer h, int index, PointerByReference Value);
        int Toupcam_get_BinningMethod(Pointer h, int index, PointerByReference Value);
        int Toupcam_put_SelfTrigger(Pointer h, SelfTrigger pSt);
        int Toupcam_get_SelfTrigger(Pointer h, SelfTrigger pSt);
        int Toupcam_put_Linear(Pointer h, byte[] v8, short[] v16);
        int Toupcam_put_Curve(Pointer h, byte[] v8, short[] v16);
        int Toupcam_put_ColorMatrix(Pointer h, double[] v);
        int Toupcam_put_InitWBGain(Pointer h, short[] v);
        int Toupcam_get_FrameRate(Pointer h, IntByReference nFrame, IntByReference nTime, IntByReference nTotalFrame);
        int Toupcam_FfcOnce(Pointer h);
        int Toupcam_DfcOnce(Pointer h);
        int Toupcam_FpncOnce(Pointer h);
        int Toupcam_IoControl(Pointer h, int ioLine, int eType, int outVal, IntByReference inVal);
        int Toupcam_AAF(Pointer h, int action, int outVal, IntByReference inVal);
        int Toupcam_get_LensInfo(Pointer h, Pointer pInfo);
        int Toupcam_get_AFState(Pointer h, Pointer pState);
        int Toupcam_put_AFMode(Pointer h, int mode);
        int Toupcam_put_AFRoi(Pointer h, int xOffset, int yOffset, int xWidth, int yHeight);
        int Toupcam_put_AFAperture(Pointer h, int iAperture);
        int Toupcam_put_AFFMPos(Pointer h, int iFMPos);
        int Toupcam_get_FocusMotor(Pointer h, FocusMotor pFocusMotor);

        int Toupcam_TriggerSyncV4Array(Pointer h, int nWaitMS, byte[] pImageData, int bits, int rowPitch, FrameInfoV4 pInfo);
        int Toupcam_PullImageV4Array(Pointer h, byte[] pImageData, int bStill, int bits, int rowPitch, FrameInfoV4 pInfo);
        int Toupcam_WaitImageV4Array(Pointer h, int nWaitMS, byte[] pImageData, int bStill, int bits, int rowPitch, FrameInfoV4 pInfo);
        @Deprecated int Toupcam_TriggerSyncArray(Pointer h, int nWaitMS, byte[] pImageData, int bits, int rowPitch, FrameInfoV3 pInfo);
        @Deprecated int Toupcam_PullImageV3Array(Pointer h, byte[] pImageData, int bStill, int bits, int rowPitch, FrameInfoV3 pInfo);
        @Deprecated int Toupcam_WaitImageV3Array(Pointer h, int nWaitMS, byte[] pImageData, int bStill, int bits, int rowPitch, FrameInfoV3 pInfo);
        @Deprecated int Toupcam_PullImageV2Array(Pointer h, byte[] pImageData, int bits, FrameInfoV2 pInfo);
        @Deprecated int Toupcam_PullStillImageV2Array(Pointer h, byte[] pImageData, int bits, FrameInfoV2 pInfo);
        @Deprecated int Toupcam_PullImageWithRowPitchV2Array(Pointer h, byte[] pImageData, int bits, int rowPitch, FrameInfoV2 pInfo);
        @Deprecated int Toupcam_PullStillImageWithRowPitchV2Array(Pointer h, byte[] pImageData, int bits, int rowPitch, FrameInfoV2 pInfo);
        int Toupcam_write_EEPROMArray(Pointer h, int addr, byte[] pBuffer, int nBufferLen);
        int Toupcam_read_EEPROMArray(Pointer h, int addr, byte[] pBuffer, int nBufferLen);
        int Toupcam_rwc_FlashArray(Pointer h, int action, int addr, int len, byte[] pData);
        int Toupcam_write_PipeArray(Pointer h, int pipeId, byte[] pBuffer, int nBufferLen);
        int Toupcam_read_PipeArray(Pointer h, int pipeId, byte[] pBuffer, int nBufferLen);
        int Toupcam_write_UARTArray(Pointer h, byte[] pBuffer, int nBufferLen);
        int Toupcam_read_UARTArray(Pointer h, byte[] pBuffer, int nBufferLen);
        Pointer Toupcam_get_Model(short idVendor, short idProduct);
        int Toupcam_Gain2TempTint(int[] gain, IntByReference temp, IntByReference tint);
        void Toupcam_TempTint2Gain(int temp, int tint, int[] gain);
    }

    private interface WinLibrary extends CLib, StdCallLibrary {
        WinLibrary INSTANCE = (WinLibrary)Native.load("toupcam", WinLibrary.class, options_);
        
        interface EVENT_CALLBACK extends StdCallCallback {
            void invoke(int nEvent, Pointer ctxEvent);
        }
        
        Pointer Toupcam_Open(WString camId);
        int Toupcam_Replug(WString camId);
        int Toupcam_StartPullModeWithCallback(Pointer h, EVENT_CALLBACK funEvent, Pointer ctxEvent);
        int Toupcam_StartPullModeWithWndMsg(Pointer h, Pointer hWnd, int nMsg);
        int Toupcam_FfcImport(Pointer h, WString filePath);
        int Toupcam_FfcExport(Pointer h, WString filePath);
        int Toupcam_DfcImport(Pointer h, WString filePath);
        int Toupcam_DfcExport(Pointer h, WString filePath);
        int Toupcam_FpncImport(Pointer h, WString filePath);
        int Toupcam_FpncExport(Pointer h, WString filePath);

        interface PROGRESS_CALLBACK extends StdCallCallback {
            void invoke(int percent, Pointer ctxProgress);
        }
        interface HOTPLUG_CALLBACK extends StdCallCallback {
            void invoke(Pointer ctxHotplug);
        }
        int Toupcam_Update(WString camId, WString filePath, PROGRESS_CALLBACK funProgress, Pointer ctxProgress);
        int Toupcam_GigeEnable(HOTPLUG_CALLBACK funHotplug, Pointer ctxHotplug);
        int Toupcam_PciEnable(HOTPLUG_CALLBACK funHotplug, Pointer ctxHotplug);
        int Toupcam_CtiEnable(HOTPLUG_CALLBACK funHotplug, Pointer ctxHotplug, WString[] ctiPath);

        interface HISTOGRAM_CALLBACK extends StdCallCallback {
            void invoke(Pointer aHist, int nFlag, Pointer ctxHistogram);
        }
        int Toupcam_GetHistogramV2(Pointer h, HISTOGRAM_CALLBACK funHistogram, Pointer ctxHistogram);
     }
    
    private interface CLibrary extends CLib, Library {
        CLibrary INSTANCE = (CLibrary)Native.load("toupcam", CLibrary.class, options_);
        
        interface EVENT_CALLBACK extends Callback {
            void invoke(int nEvent, Pointer ctxEvent);
        }
        
        Pointer Toupcam_Open(String camId);
        int Toupcam_Replug(String camId);
        int Toupcam_StartPullModeWithCallback(Pointer h, EVENT_CALLBACK funEvent, Pointer ctxEvent);
        int Toupcam_FfcImport(Pointer h, String filePath);
        int Toupcam_FfcExport(Pointer h, String filePath);
        int Toupcam_DfcImport(Pointer h, String filePath);
        int Toupcam_DfcExport(Pointer h, String filePath);
        int Toupcam_FpncImport(Pointer h, String filePath);
        int Toupcam_FpncExport(Pointer h, String filePath);
        
        interface HOTPLUG_CALLBACK extends Callback {
            void invoke(Pointer ctxHotPlug);
        }
        void Toupcam_HotPlug(HOTPLUG_CALLBACK funHotPlug, Pointer ctxHotPlug);

        interface PROGRESS_CALLBACK extends Callback {
            void invoke(int percent, Pointer ctxProgress);
        }
        int Toupcam_Update(String camId, String filePath, PROGRESS_CALLBACK funProgress, Pointer ctxProgress);
        int Toupcam_GigeEnable(HOTPLUG_CALLBACK funHotplug, Pointer ctxHotplug);
        int Toupcam_PciEnable(HOTPLUG_CALLBACK funHotplug, Pointer ctxHotplug);
        int Toupcam_CtiEnable(HOTPLUG_CALLBACK funHotplug, Pointer ctxHotplug, String[] ctiPath);

        interface HISTOGRAM_CALLBACK extends Callback {
            void invoke(Pointer aHist, int nFlag, Pointer ctxHistogram);
        }
        int Toupcam_GetHistogramV2(Pointer h, HISTOGRAM_CALLBACK funHistogram, Pointer ctxHistogram);
    }
    
    public interface IEventCallback {
        void onEvent(int nEvent);
    }
    
    public interface IHotplugCallback {
        void onHotplug();
    }

    public interface IProgressCallback {
        void onProgress(int percent);
    }

    public interface IHistogramCallback {
        void onHistogram(int[] array);
    }
    
    private final static Map options_ = new HashMap() {
        {
            put(Library.OPTION_FUNCTION_MAPPER, new FunctionMapper() {
                HashMap<String, String> funcmap_ = new HashMap() {
                    {
                        put("Toupcam_TriggerSyncV4Array", "Toupcam_TriggerSyncV4");
                        put("Toupcam_PullImageV4Array", "Toupcam_PullImageV4");
                        put("Toupcam_WaitImageV4Array", "Toupcam_WaitImageV4");
                        put("Toupcam_TriggerSyncArray", "Toupcam_TriggerSync");
                        put("Toupcam_PullImageV3Array", "Toupcam_PullImageV3");
                        put("Toupcam_WaitImageV3Array", "Toupcam_WaitImageV3");
                        put("Toupcam_PullImageV2Array", "Toupcam_PullImageV2");
                        put("Toupcam_PullStillImageV2Array", "Toupcam_PullStillImageV2");
                        put("Toupcam_PullImageWithRowPitchV2Array", "Toupcam_PullImageWithRowPitchV2");
                        put("Toupcam_PullStillImageWithRowPitchV2Array", "Toupcam_PullStillImageWithRowPitchV2");
                        put("Toupcam_write_EEPROMArray", "Toupcam_write_EEPROM");
                        put("Toupcam_read_EEPROMArray", "Toupcam_read_EEPROM");
                        put("Toupcam_rwc_FlashArray", "Toupcam_rwcFlash");
                        put("Toupcam_write_PipeArray", "Toupcam_write_Pipe");
                        put("Toupcam_read_PipeArray", "Toupcam_read_Pipe");
                        put("Toupcam_write_UARTArray", "Toupcam_write_UART");
                        put("Toupcam_read_UARTArray", "Toupcam_read_UART");
                    }
                };
                
                @Override
                public String getFunctionName(NativeLibrary library, Method method) {
                    String name = method.getName();
                    String str = funcmap_.get(name);
                    if (str != null)
                        return str;
                    else
                        return name;
                }
            });
        }
    };
    
    private final static CLib _lib = Platform.isWindows() ?  WinLibrary.INSTANCE : CLibrary.INSTANCE;
    private final static Hashtable _hash = new Hashtable();
    private static int _clsid = 0;
    private static IHotplugCallback _hotplug = null;
    private static CLibrary.HOTPLUG_CALLBACK _hotplugcallback = null;
    private int _objid = 0;
    private Pointer _handle = null;
    private Callback _callback = null;
    private IEventCallback _cb = null;
    private IHistogramCallback _histogram = null;
    private Callback _histogramcallback = null;
    
    static public int MAKEFOURCC(int a, int b, int c, int d) {
        return ((int)(byte)(a) | ((int)(byte)(b) << 8) | ((int)(byte)(c) << 16) | ((int)(byte)(d) << 24));
    }
    
    /*
        the object of toupcam must be obtained by static mothod Open or OpenByIndex, it cannot be obtained by obj = new toupcam (The constructor is private on purpose)
    */
    private toupcam(Pointer h) {
        _handle = h;
        synchronized (_hash) {
            _objid = _clsid++;
        }
        _hash.put(_objid, this);
    }
    
    @Override
    public void close() {
        if (_handle != null) {
            _lib.Toupcam_Close(_handle);
            _handle = null;
        }
        
        _callback = null;
        _cb = null;
        _hash.remove(_objid);
    }
    
    /* get the version of this dll/so/dylib, which is: 59.29030.20250722 */
    public static String Version() {
        if (Platform.isWindows())
            return _lib.Toupcam_Version().getWideString(0);
        else
            return _lib.Toupcam_Version().getString(0);
    }
    
    /*
        This function is only available on macOS and Linux, it's unnecessary on Windows & Android. To process the device plug in / pull out:
            (1) On Windows, please refer to the MSDN
                (a) Device Management, https://docs.microsoft.com/en-us/windows/win32/devio/device-management
                (b) Detecting Media Insertion or Removal, https://docs.microsoft.com/en-us/windows/win32/devio/detecting-media-insertion-or-removal
            (2) On Android, please refer to https://developer.android.com/guide/topics/connectivity/usb/host
            (3) On Linux / macOS, please call this function to register the callback function.
                When the device is inserted or pulled out, you will be notified by the callback funcion, and then call Toupcam_EnumV2(...) again to enum the cameras.
            (4) On macOS, IONotificationPortCreate series APIs can also be used as an alternative.
    */
    public static void HotPlug(IHotplugCallback cb) throws HRESULTException {
        if (Platform.isWindows() || Platform.isAndroid())
            errCheck(HRESULTException.E_NOTIMPL);
        else
        {
            _hotplug = cb;
            if (_hotplug == null)
                _hotplugcallback = null;
            else
            {
                _hotplugcallback = new CLibrary.HOTPLUG_CALLBACK() {
                    @Override
                    public void invoke(Pointer ctxHotPlug) {
                        if (_hotplug != null)
                            _hotplug.onHotplug();
                    }
                };
            }
            ((CLibrary)_lib).Toupcam_HotPlug(_hotplugcallback, new Pointer(0));
        }
    }

    private static ModelV2 toModelV2(Pointer qtr)
    {
        ModelV2 model = new ModelV2();
        long qoffset = 0;
        if (Platform.isWindows())
            model.name = qtr.getPointer(0).getWideString(0);
        else
            model.name = qtr.getPointer(0).getString(0);
        qoffset += Native.POINTER_SIZE;
        if (Platform.isWindows() && (4 == Native.POINTER_SIZE))   /* 32bits windows */
            qoffset += 4; //skip 4 bytes, different from the linux version
        model.flag = qtr.getLong(qoffset);
        qoffset += 8;
        model.maxspeed = qtr.getInt(qoffset);
        qoffset += 4;
        model.preview = qtr.getInt(qoffset);
        qoffset += 4;
        model.still = qtr.getInt(qoffset);
        qoffset += 4;
        model.maxfanspeed = qtr.getInt(qoffset);
        qoffset += 4;
        model.ioctrol = qtr.getInt(qoffset);
        qoffset += 4;
        model.xpixsz = qtr.getFloat(qoffset);
        qoffset += 4;
        model.ypixsz = qtr.getFloat(qoffset);
        qoffset += 4;
        model.res = new Resolution[model.preview];
        for (int j = 0; j < model.preview; ++j) {
            model.res[j] = new Resolution();
            model.res[j].width = qtr.getInt(qoffset);
            qoffset += 4;
            model.res[j].height = qtr.getInt(qoffset);
            qoffset += 4;
        }
        return model;
    }
    
    private static DeviceV2[] Ptr2Device(Memory ptr, int cnt) {
        DeviceV2[] arr = new DeviceV2[(cnt > 0) ? cnt : 0];
        if (cnt > 0) {
            long poffset = 0;
            for (int i = 0; i < cnt; ++i) {
                arr[i] = new DeviceV2();
                if (Platform.isWindows())
                {
                    arr[i].displayname = ptr.getWideString(poffset);
                    poffset += 128;
                    arr[i].id = ptr.getWideString(poffset);
                    poffset += 128;
                }
                else
                {
                    arr[i].displayname = ptr.getString(poffset);
                    poffset += 64;
                    arr[i].id = ptr.getString(poffset);
                    poffset += 64;
                }
                
                Pointer qtr = ptr.getPointer(poffset);
                poffset += Native.POINTER_SIZE;
                arr[i].model = toModelV2(qtr);
            }
        }
        
        return arr;
    }
    
    /* enumerate cameras that are currently connected to the computer */
    public static DeviceV2[] EnumV2() {
        Memory ptr = new Memory(512 * 128);
        return Ptr2Device(ptr, _lib.Toupcam_EnumV2(ptr));
    }
    
    public static DeviceV2[] EnumWithName() {
        Memory ptr = new Memory(512 * 128);
        return Ptr2Device(ptr, _lib.Toupcam_EnumWithName(ptr));
    }

    private static void OnHotplugCallback(Pointer ctxHotplug) {
        Object o = _hash.get((int)Pointer.nativeValue(ctxHotplug));
        if (o instanceof IHotplugCallback) {
            ((IHotplugCallback)o).onHotplug();
        }
    }

    /* Initialize support for GigE cameras. If online/offline notifications are not required, the callback function can be set to null */
    public static void GigeEnable(IHotplugCallback cb) throws HRESULTException {
        int id = _clsid++;
        _hash.put(id, cb);
        if (Platform.isWindows()) {
            WinLibrary.HOTPLUG_CALLBACK p = new WinLibrary.HOTPLUG_CALLBACK() {
                @Override
                public void invoke(Pointer ctxHotplug) {
                    OnHotplugCallback(ctxHotplug);
                }
            };
            Native.setCallbackThreadInitializer(p, new CallbackThreadInitializer(false, false, "hotplugCallback"));
            errCheck(((WinLibrary)_lib).Toupcam_GigeEnable(p, new Pointer(id)));
        }
        else {
            CLibrary.HOTPLUG_CALLBACK p = new CLibrary.HOTPLUG_CALLBACK() {
                @Override
                public void invoke(Pointer ctxHotplug) {
                    OnHotplugCallback(ctxHotplug);
                }
            };
            Native.setCallbackThreadInitializer(p, new CallbackThreadInitializer(false, false, "hotplugCallback"));
            errCheck(((CLibrary)_lib).Toupcam_GigeEnable(p, new Pointer(id)));
        }
    }
    
    /* Initialize support for PCIe cameras. If online/offline notifications are not required, the callback function can be set to null */
    public static void PciEnable(IHotplugCallback cb) throws HRESULTException {
        int id = _clsid++;
        _hash.put(id, cb);
        if (Platform.isWindows()) {
            WinLibrary.HOTPLUG_CALLBACK p = new WinLibrary.HOTPLUG_CALLBACK() {
                @Override
                public void invoke(Pointer ctxHotplug) {
                    OnHotplugCallback(ctxHotplug);
                }
            };
            Native.setCallbackThreadInitializer(p, new CallbackThreadInitializer(false, false, "hotplugCallback"));
            errCheck(((WinLibrary)_lib).Toupcam_PciEnable(p, new Pointer(id)));
        }
        else {
            CLibrary.HOTPLUG_CALLBACK p = new CLibrary.HOTPLUG_CALLBACK() {
                @Override
                public void invoke(Pointer ctxHotplug) {
                    OnHotplugCallback(ctxHotplug);
                }
            };
            Native.setCallbackThreadInitializer(p, new CallbackThreadInitializer(false, false, "hotplugCallback"));
            errCheck(((CLibrary)_lib).Toupcam_PciEnable(p, new Pointer(id)));
        }
    }

    /* Initialize support for *.cti cameras. If online/offline notifications are not required, the callback function can be set to null
    * (1) ctiPath = null means all *.cti in GENICAM_GENTL64_PATH/GENICAM_GENTL32_PATH
    * or
    * (2) ctiPath[] = {
                "/full/path/to/file.cti"
                ...
            }
    */
    public static void CtiEnable(IHotplugCallback cb, String[] ctiPath) throws HRESULTException {
        int id = _clsid++;
        _hash.put(id, cb);
        if (Platform.isWindows()) {
            WString[] newPath = new WString[ctiPath.length + 1];
            for (int i = 0; i < ctiPath.length; ++i) {
                newPath[i] = (ctiPath[i] != null) ? new WString(ctiPath[i]) : null;
            }
            WinLibrary.HOTPLUG_CALLBACK p = new WinLibrary.HOTPLUG_CALLBACK() {
                @Override
                public void invoke(Pointer ctxHotplug) {
                    OnHotplugCallback(ctxHotplug);
                }
            };
            Native.setCallbackThreadInitializer(p, new CallbackThreadInitializer(false, false, "hotplugCallback"));
            errCheck(((WinLibrary)_lib).Toupcam_CtiEnable(p, new Pointer(id), newPath));
        }
        else {
            String[] newPath = new String[ctiPath.length + 1];
            for (int i = 0; i < ctiPath.length; ++i) {
                newPath[i] = ctiPath[i];
            }
            CLibrary.HOTPLUG_CALLBACK p = new CLibrary.HOTPLUG_CALLBACK() {
                @Override
                public void invoke(Pointer ctxHotplug) {
                    OnHotplugCallback(ctxHotplug);
                }
            };
            Native.setCallbackThreadInitializer(p, new CallbackThreadInitializer(false, false, "hotplugCallback"));
            errCheck(((CLibrary)_lib).Toupcam_CtiEnable(p, new Pointer(id), newPath));
        }
    }

    /*
        the object of toupcam must be obtained by static mothod Open or OpenByIndex, it cannot be obtained by obj = new toupcam (The constructor is private on purpose)
    */
    // camId: enumerated by EnumV2, null means the first enumerated camera
    // For USB, GigE or PCIe camera, the camId can also be specified as (case sensitive):
    //    (a) "sn:xxxxxxxxxxxx" (such as sn:ZP250212241204105), or
    //    (b) "name:xxxxxx" (such as name:Camera1)
    //Moreover, for GigE camera, the camId can also be specified as (case sensitive):
    //    (a) "ip:xxx.xxx.xxx.xxx" (such as ip:192.168.1.100), or
    //    (b) "mac:xxxxxxxxxxxx" (such as mac:d05f64ffff23)
    public static toupcam Open(String camId) {
        Pointer tmphandle = null;
        if (Platform.isWindows()) {
            if (camId == null)
                tmphandle = ((WinLibrary)_lib).Toupcam_Open(null);
            else
                tmphandle = ((WinLibrary)_lib).Toupcam_Open(new WString(camId));
        }
        else {
            tmphandle = ((CLibrary)_lib).Toupcam_Open(camId);
        }
        if (tmphandle == null)
            return null;
        return new toupcam(tmphandle);
    }
    
    /*
        the object of toupcam must be obtained by static mothod Open or OpenByIndex, it cannot be obtained by obj = new toupcam (The constructor is private on purpose)
    */
    /*
        the same with Open, but use the index as the parameter. such as:
        index == 0, open the first camera,
        index == 1, open the second camera,
        etc
    */
    public static toupcam OpenByIndex(int index) {
        Pointer tmphandle = _lib.Toupcam_OpenByIndex(index);
        if (tmphandle == null)
            return null;
        return new toupcam(tmphandle);
    }
    
    public int getResolutionNumber() throws HRESULTException {
        return errCheck(_lib.Toupcam_get_ResolutionNumber(_handle));
    }
    
    public int getStillResolutionNumber() throws HRESULTException {
        return errCheck(_lib.Toupcam_get_StillResolutionNumber(_handle));
    }
    
    /*
        false:    color mode
        true:     mono mode, such as EXCCD00300KMA and UHCCD01400KMA
    */
    public boolean getMonoMode() throws HRESULTException {
        return (0 == errCheck(_lib.Toupcam_get_MonoMode(_handle)));
    }
    
    /* get the maximum speed, "Frame Speed Level" */
    public int getMaxSpeed() throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_MaxSpeed(_handle));
        return p.getValue();
    }
    
    /* get the max bitdepth of this camera, such as 8, 10, 12, 14, 16 */
    public int getMaxBitDepth() throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_MaxBitDepth(_handle));
        return p.getValue();
    }
    
    /* get the maximum fan speed, the fan speed range = [0, max], closed interval */
    public int getFanMaxSpeed() throws HRESULTException {
        return errCheck(_lib.Toupcam_get_FanMaxSpeed(_handle));
    }
    
    /* get the revision */
    public short getRevision() throws HRESULTException {
        ShortByReference p = new ShortByReference();
        errCheck(_lib.Toupcam_get_Revision(_handle, p));
        return p.getValue();
    }
    
    /* get the serial number which is always 32 chars which is zero-terminated such as "TP110826145730ABCD1234FEDC56787" */
    public String getSerialNumber() throws HRESULTException {
        Memory p = new Memory(32);
        errCheck(_lib.Toupcam_get_SerialNumber(_handle, p));
        return p.getString(0);
    }
    
    /* get the camera firmware version, such as: 3.2.1.20140922 */
    public String getFwVersion() throws HRESULTException {
        Memory p = new Memory(16);
        errCheck(_lib.Toupcam_get_FwVersion(_handle, p));
        return p.getString(0);
    }
    
    /* get the camera hardware version, such as: 3.2.1.20140922 */
    public String getHwVersion() throws HRESULTException {
        Memory p = new Memory(16);
        errCheck(_lib.Toupcam_get_HwVersion(_handle, p));
        return p.getString(0);
    }
    
    /* such as: 20150327 */
    public String getProductionDate() throws HRESULTException {
        Memory p = new Memory(16);
        errCheck(_lib.Toupcam_get_ProductionDate(_handle, p));
        return p.getString(0);
    }
    
    /* such as: 1.3 */
    public String getFpgaVersion() throws HRESULTException {
        Memory p = new Memory(16);
        errCheck(_lib.Toupcam_get_FpgaVersion(_handle, p));
        return p.getString(0);
    }
    
    public int getField() throws HRESULTException {
        return errCheck(_lib.Toupcam_get_Field(_handle));
    }
    
    private static void OnEventCallback(int nEvent, Pointer ctxEvent) {
        Object o = _hash.get((int)Pointer.nativeValue(ctxEvent));
        if (o instanceof toupcam) {
            toupcam t = (toupcam)o;
            if (t._cb != null)
                t._cb.onEvent(nEvent);
        }
    }
    
    public void StartPullModeWithCallback(IEventCallback cb) throws HRESULTException {
        _cb = cb;
        if (Platform.isWindows()) {
            _callback = new WinLibrary.EVENT_CALLBACK() {
                @Override
                public void invoke(int nEvent, Pointer ctxEvent) {
                    OnEventCallback(nEvent, ctxEvent);
                }
            };
            Native.setCallbackThreadInitializer(_callback, new CallbackThreadInitializer(false, false, "eventCallback"));
            errCheck(((WinLibrary)_lib).Toupcam_StartPullModeWithCallback(_handle, (WinLibrary.EVENT_CALLBACK)_callback, new Pointer(_objid)));
        }
        else {
            _callback = new CLibrary.EVENT_CALLBACK() {
                @Override
                public void invoke(int nEvent, Pointer ctxEvent) {
                    OnEventCallback(nEvent, ctxEvent);
                }
            };
            Native.setCallbackThreadInitializer(_callback, new CallbackThreadInitializer(false, false, "eventCallback"));
            errCheck(((CLibrary)_lib).Toupcam_StartPullModeWithCallback(_handle, (CLibrary.EVENT_CALLBACK)_callback, new Pointer(_objid)));
        }
    }

    public void StartPullModeWithWndMsg(Pointer hWnd, int nMsg) throws HRESULTException {
        if (Platform.isWindows())
            errCheck(((WinLibrary)_lib).Toupcam_StartPullModeWithWndMsg(_handle, hWnd, nMsg));
        else
            errCheck(HRESULTException.E_NOTIMPL);
    }

    @Deprecated
    public void PullImageV2(ByteBuffer pImageData, int bits, FrameInfoV2 pInfo) throws HRESULTException {
        if (pImageData.isDirect())
            errCheck(_lib.Toupcam_PullImageV2(_handle, Native.getDirectBufferPointer(pImageData), bits, pInfo));
        else if (pImageData.hasArray())
            PullImageV2(pImageData.array(), bits, pInfo);
        else
            errCheck(HRESULTException.E_INVALIDARG);
    }

    @Deprecated
    public void PullImageV2(byte[] pImageData, int bits, FrameInfoV2 pInfo) throws HRESULTException {
        errCheck(_lib.Toupcam_PullImageV2Array(_handle, pImageData, bits, pInfo));
    }

    @Deprecated
    public void PullStillImageV2(ByteBuffer pImageData, int bits, FrameInfoV2 pInfo) throws HRESULTException {
        if (pImageData.isDirect())
            errCheck(_lib.Toupcam_PullStillImageV2(_handle, Native.getDirectBufferPointer(pImageData), bits, pInfo));
        else if (pImageData.hasArray())
            PullStillImageV2(pImageData.array(), bits, pInfo);
        else
            errCheck(HRESULTException.E_INVALIDARG);
    }

    @Deprecated
    public void PullStillImageV2(byte[] pImageData, int bits, FrameInfoV2 pInfo) throws HRESULTException {
        errCheck(_lib.Toupcam_PullStillImageV2Array(_handle, pImageData, bits, pInfo));
    }
    
    /*
       nWaitMS: The timeout interval, in milliseconds. If a non-zero value is specified, the function either successfully fetches the image or waits for a timeout.
                If nWaitMS is zero, the function does not wait when there are no images to fetch; It always returns immediately; this is equal to PullImage.
       bStill: to pull still image, set to 1, otherwise 0
       bits: 24 (RGB24), 32 (RGB32), 48 (RGB48), 8 (Grey), 16 (Grey), 64 (RGB64).
             In RAW mode, this parameter is ignored.
             bits = 0 means using default bits base on OPTION_RGB.
             When bits and OPTION_RGB are inconsistent, format conversion will have to be performed, resulting in loss of efficiency.
             See the following bits and OPTION_RGB correspondence table:
               ----------------------------------------------------------------------------------------------------------------------
               | OPTION_RGB         |   0 (RGB24)   |   1 (RGB48)   |   2 (RGB32)   |   3 (Grey8)   |  4 (Grey16)   |   5 (RGB64)   |
               |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
               | bits = 0           |      24       |       48      |      32       |       8       |       16      |       64      |
               |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
               | bits = 24          |      24       |       NA      | Convert to 24 | Convert to 24 |       NA      |       NA      |
               |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
               | bits = 32          | Convert to 32 |       NA      |       32      | Convert to 32 |       NA      |       NA      |
               |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
               | bits = 48          |      NA       |       48      |       NA      |       NA      | Convert to 48 | Convert to 48 |
               |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
               | bits = 8           | Convert to 8  |       NA      | Convert to 8  |       8       |       NA      |       NA      |
               |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
               | bits = 16          |      NA       | Convert to 16 |       NA      |       NA      |       16      | Convert to 16 |
               |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
               | bits = 64          |      NA       | Convert to 64 |       NA      |       NA      | Convert to 64 |       64      |
               |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|

       rowPitch: The distance from one row to the next row. rowPitch = 0 means using the default row pitch. rowPitch = -1 means zero padding, see below:
               ----------------------------------------------------------------------------------------------
               | format                             | 0 means default row pitch     | -1 means zero padding |
               |------------------------------------|-------------------------------|-----------------------|
               | RGB       | RGB24                  | TDIBWIDTHBYTES(24 * Width)    | Width * 3             |
               |           | RGB32                  | Width * 4                     | Width * 4             |
               |           | RGB48                  | TDIBWIDTHBYTES(48 * Width)    | Width * 6             |
               |           | GREY8                  | TDIBWIDTHBYTES(8 * Width)     | Width                 |
               |           | GREY16                 | TDIBWIDTHBYTES(16 * Width)    | Width * 2             |
               |           | RGB64                  | Width * 8                     | Width * 8             |
               |-----------|------------------------|-------------------------------|-----------------------|
               | RAW       | 8bits Mode             | Width                         | Width                 |
               |           | 10/12/14/16bits Mode   | Width * 2                     | Width * 2             |
               |-----------|------------------------|-------------------------------|-----------------------|
    */
    public void PullImage(ByteBuffer pImageData, int bStill, int bits, int rowPitch, FrameInfoV4 pInfo) throws HRESULTException {
        if (pImageData.isDirect())
            errCheck(_lib.Toupcam_PullImageV4(_handle, Native.getDirectBufferPointer(pImageData), bStill, bits, rowPitch, pInfo));
        else if (pImageData.hasArray())
            PullImage(pImageData.array(), bStill, bits, rowPitch, pInfo);
        else
            errCheck(HRESULTException.E_INVALIDARG);
    }

    public void PullImage(byte[] pImageData, int bStill, int bits, int rowPitch, FrameInfoV4 pInfo) throws HRESULTException {
        errCheck(_lib.Toupcam_PullImageV4Array(_handle, pImageData, bStill, bits, rowPitch, pInfo));
    }

    @Deprecated
    public void PullImageV3(ByteBuffer pImageData, int bStill, int bits, int rowPitch, FrameInfoV3 pInfo) throws HRESULTException {
        if (pImageData.isDirect())
            errCheck(_lib.Toupcam_PullImageV3(_handle, Native.getDirectBufferPointer(pImageData), bStill, bits, rowPitch, pInfo));
        else if (pImageData.hasArray())
            PullImageV3(pImageData.array(), bStill, bits, rowPitch, pInfo);
        else
            errCheck(HRESULTException.E_INVALIDARG);
    }

    @Deprecated
    public void PullImageV3(byte[] pImageData, int bStill, int bits, int rowPitch, FrameInfoV3 pInfo) throws HRESULTException {
        errCheck(_lib.Toupcam_PullImageV3Array(_handle, pImageData, bStill, bits, rowPitch, pInfo));
    }

    public void WaitImage(int nWaitMS, ByteBuffer pImageData, int bStill, int bits, int rowPitch, FrameInfoV4 pInfo) throws HRESULTException {
        if (pImageData.isDirect())
            errCheck(_lib.Toupcam_WaitImageV4(_handle, nWaitMS, Native.getDirectBufferPointer(pImageData), bStill, bits, rowPitch, pInfo));
        else if (pImageData.hasArray())
            WaitImage(nWaitMS, pImageData.array(), bStill, bits, rowPitch, pInfo);
        else
            errCheck(HRESULTException.E_INVALIDARG);
    }

    public void WaitImage(int nWaitMS, byte[] pImageData, int bStill, int bits, int rowPitch, FrameInfoV4 pInfo) throws HRESULTException {
        errCheck(_lib.Toupcam_WaitImageV4Array(_handle, nWaitMS, pImageData, bStill, bits, rowPitch, pInfo));
    }

    @Deprecated
    public void WaitImageV3(int nWaitMS, ByteBuffer pImageData, int bStill, int bits, int rowPitch, FrameInfoV3 pInfo) throws HRESULTException {
        if (pImageData.isDirect())
            errCheck(_lib.Toupcam_WaitImageV3(_handle, nWaitMS, Native.getDirectBufferPointer(pImageData), bStill, bits, rowPitch, pInfo));
        else if (pImageData.hasArray())
            WaitImageV3(nWaitMS, pImageData.array(), bStill, bits, rowPitch, pInfo);
        else
            errCheck(HRESULTException.E_INVALIDARG);
    }

    @Deprecated
    public void WaitImageV3(int nWaitMS, byte[] pImageData, int bStill, int bits, int rowPitch, FrameInfoV3 pInfo) throws HRESULTException {
        errCheck(_lib.Toupcam_WaitImageV3Array(_handle, nWaitMS, pImageData, bStill, bits, rowPitch, pInfo));
    }

    @Deprecated
    public void PullImageWithRowPitchV2(ByteBuffer pImageData, int bits, int rowPitch, FrameInfoV2 pInfo) throws HRESULTException {
        if (pImageData.isDirect())
            errCheck(_lib.Toupcam_PullImageWithRowPitchV2(_handle, Native.getDirectBufferPointer(pImageData), bits, rowPitch, pInfo));
        else if (pImageData.hasArray())
            PullImageWithRowPitchV2(pImageData.array(), bits, rowPitch, pInfo);
        else
            errCheck(HRESULTException.E_INVALIDARG);
    }

    @Deprecated
    public void PullImageWithRowPitchV2(byte[] pImageData, int bits, int rowPitch, FrameInfoV2 pInfo) throws HRESULTException {
        errCheck(_lib.Toupcam_PullImageWithRowPitchV2Array(_handle, pImageData, bits, rowPitch, pInfo));
    }

    @Deprecated
    public void PullStillImageWithRowPitchV2(ByteBuffer pImageData, int bits, int rowPitch, FrameInfoV2 pInfo) throws HRESULTException {
        if (pImageData.isDirect())
            errCheck(_lib.Toupcam_PullStillImageWithRowPitchV2(_handle, Native.getDirectBufferPointer(pImageData), bits, rowPitch, pInfo));
        else if (pImageData.hasArray())
            PullStillImageWithRowPitchV2(pImageData.array(), bits, rowPitch, pInfo);
        else
            errCheck(HRESULTException.E_INVALIDARG);
    }

    @Deprecated
    public void PullStillImageWithRowPitchV2(byte[] pImageData, int bits, int rowPitch, FrameInfoV2 pInfo) throws HRESULTException {
        errCheck(_lib.Toupcam_PullStillImageWithRowPitchV2Array(_handle, pImageData, bits, rowPitch, pInfo));
    }
    
    public void Stop() throws HRESULTException {
        errCheck(_lib.Toupcam_Stop(_handle));
        
        _callback = null;
        _cb = null;
    }
    
    /* 1 => pause, 0 => continue */
    public void Pause(boolean bPause) throws HRESULTException {
        errCheck(_lib.Toupcam_Pause(_handle, bPause ? 1 : 0));
    }
    
    /* nResolutionIndex = 0xffffffff means use the cureent preview resolution */
    public void Snap(int nResolutionIndex) throws HRESULTException {
        errCheck(_lib.Toupcam_Snap(_handle, nResolutionIndex));
    }
    
    /* multiple still image snap, nResolutionIndex = 0xffffffff means use the cureent preview resolution */
    public void SnapN(int nResolutionIndex, int nNumber) throws HRESULTException {
        errCheck(_lib.Toupcam_SnapN(_handle, nResolutionIndex, nNumber));
    }
    
    /* multiple RAW still image snap, nResolutionIndex = 0xffffffff means use the cureent preview resolution */
    public void SnapR(int nResolutionIndex, int nNumber) throws HRESULTException {
        errCheck(_lib.Toupcam_SnapR(_handle, nResolutionIndex, nNumber));
    }
    
    /*
        soft trigger:
        nNumber:    0xffff:     trigger continuously
                    0:          cancel trigger
                    others:     number of images to be triggered
    */
    public void Trigger(short nNumber) throws HRESULTException {
        errCheck(_lib.Toupcam_Trigger(_handle, nNumber));
    }
    
    /*
      trigger synchronously
      nWaitMS:      0:              by default, exposure * 102% + 4000 milliseconds
                    0xffffffff:     wait infinite
                    other:          milliseconds to wait
    */
    public void TriggerSync(int nWaitMS, ByteBuffer pImageData, int bits, int rowPitch, FrameInfoV3 pInfo) throws HRESULTException {
        if (pImageData.isDirect())
            errCheck(_lib.Toupcam_TriggerSync(_handle, nWaitMS, Native.getDirectBufferPointer(pImageData), bits, rowPitch, pInfo));
        else if (pImageData.hasArray())
            TriggerSync(nWaitMS, pImageData.array(), bits, rowPitch, pInfo);
        else
            errCheck(HRESULTException.E_INVALIDARG);
    }

    public void TriggerSync(int nWaitMS, byte[] pImageData, int bits, int rowPitch, FrameInfoV4 pInfo) throws HRESULTException {
        errCheck(_lib.Toupcam_TriggerSyncV4Array(_handle, nWaitMS, pImageData, bits, rowPitch, pInfo));
    }

    @Deprecated
    public void TriggerSync(int nWaitMS, ByteBuffer pImageData, int bits, int rowPitch, FrameInfoV4 pInfo) throws HRESULTException {
        if (pImageData.isDirect())
            errCheck(_lib.Toupcam_TriggerSyncV4(_handle, nWaitMS, Native.getDirectBufferPointer(pImageData), bits, rowPitch, pInfo));
        else if (pImageData.hasArray())
            TriggerSync(nWaitMS, pImageData.array(), bits, rowPitch, pInfo);
        else
            errCheck(HRESULTException.E_INVALIDARG);
    }

    @Deprecated
    public void TriggerSync(int nWaitMS, byte[] pImageData, int bits, int rowPitch, FrameInfoV3 pInfo) throws HRESULTException {
        errCheck(_lib.Toupcam_TriggerSyncArray(_handle, nWaitMS, pImageData, bits, rowPitch, pInfo));
    }
    
    public void put_Size(int nWidth, int nHeight) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Size(_handle, nWidth, nHeight));
    }
    
    /* width, height */
    public int[] get_Size() throws HRESULTException {
        IntByReference p = new IntByReference();
        IntByReference q = new IntByReference();
        errCheck(_lib.Toupcam_get_Size(_handle, p, q));
        return new int[] { p.getValue(), q.getValue() };
    }
    
    /*
        put_Size, put_eSize, can be used to set the video output resolution BEFORE Start.
        put_Size use width and height parameters, put_eSize use the index parameter.
        for example, UCMOS03100KPA support the following resolutions:
            index 0:    2048,   1536
            index 1:    1024,   768
            index 2:    680,    510
        so, we can use put_Size(h, 1024, 768) or put_eSize(h, 1). Both have the same effect.
    */
    public void put_eSize(int nResolutionIndex) throws HRESULTException {
        errCheck(_lib.Toupcam_put_eSize(_handle, nResolutionIndex));
    }
    
    public int get_eSize() throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_eSize(_handle, p));
        return p.getValue();
    }
    
    /*
        final size after ROI, rotate, binning
    */
    public int[] get_FinalSize() throws HRESULTException {
        IntByReference p = new IntByReference();
        IntByReference q = new IntByReference();
        errCheck(_lib.Toupcam_get_FinalSize(_handle, p, q));
        return new int[] { p.getValue(), q.getValue() };
    }
    
    /* width, height */
    public int[] get_Resolution(int nResolutionIndex) throws HRESULTException {
        IntByReference p = new IntByReference();
        IntByReference q = new IntByReference();
        errCheck(_lib.Toupcam_get_Resolution(_handle, nResolutionIndex, p, q));
        return new int[] { p.getValue(), q.getValue() };
    }
    
    /*
        get the sensor pixel size, such as: 2.4um x 2.4um
    */
    public float[] get_PixelSize(int nResolutionIndex) throws HRESULTException {
        FloatByReference p = new FloatByReference();
        FloatByReference q = new FloatByReference();
        errCheck(_lib.Toupcam_get_PixelSize(_handle, nResolutionIndex, p, q));
        return new float[] { p.getValue(), q.getValue() };
    }
    
    /*
        numerator/denominator, such as: 1/1, 1/2, 1/3
    */
    public int[] get_ResolutionRatio(int nResolutionIndex) throws HRESULTException {
        IntByReference p = new IntByReference();
        IntByReference q = new IntByReference();
        errCheck(_lib.Toupcam_get_ResolutionRatio(_handle, nResolutionIndex, p, q));
        return new int[] { p.getValue(), q.getValue() };
    }
    
    /*
        see: http://www.fourcc.org
        FourCC:
            MAKEFOURCC('G', 'B', 'R', 'G'), see http://www.siliconimaging.com/RGB%20Bayer.htm
            MAKEFOURCC('R', 'G', 'G', 'B')
            MAKEFOURCC('B', 'G', 'G', 'R')
            MAKEFOURCC('G', 'R', 'B', 'G')
            MAKEFOURCC('Y', 'Y', 'Y', 'Y'), monochromatic sensor
            MAKEFOURCC('Y', '4', '1', '1'), yuv411
            MAKEFOURCC('V', 'U', 'Y', 'Y'), yuv422
            MAKEFOURCC('U', 'Y', 'V', 'Y'), yuv422
            MAKEFOURCC('Y', '4', '4', '4'), yuv444
            MAKEFOURCC('R', 'G', 'B', '8'), RGB888
    */
    public int[] get_RawFormat() throws HRESULTException {
        IntByReference p = new IntByReference();
        IntByReference q = new IntByReference();
        errCheck(_lib.Toupcam_get_RawFormat(_handle, p, q));
        return new int[] { p.getValue(), q.getValue() };
    }
    
    /*  0: no realtime
              stop grab frame when frame buffer deque is full, until the frames in the queue are pulled away and the queue is not full
        1: realtime
              use minimum frame buffer. When new frame arrive, drop all the pending frame regardless of whether the frame buffer is full.
              If DDR present, also limit the DDR frame buffer to only one frame.
        2: soft realtime
              Drop the oldest frame when the queue is full and then enqueue the new frame
        default: 0
    */
    public void put_RealTime(int val) throws HRESULTException {
        errCheck(_lib.Toupcam_put_RealTime(_handle, val));
    }
    
    public int get_RealTime() throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_RealTime(_handle, p));
        return p.getValue();
    }
    
    /* Flush is obsolete, recommend using put_Option(OPTION_FLUSH, 3) */
    public void Flush() throws HRESULTException {
        errCheck(_lib.Toupcam_Flush(_handle));
    }
    
    /*
        ------------------------------------------------------------------|
        | Parameter               |   Range       |   Default             |
        |-----------------------------------------------------------------|
        | Auto Exposure Target    |   16~235      |   120                 |
        | Exposure Gain           |   100~        |   100                 |
        | Temp                    |   1000~25000  |   6503                |
        | Tint                    |   100~2500    |   1000                |
        | LevelRange              |   0~255       |   Low = 0, High = 255 |
        | Contrast                |   -255~255    |   0                   |
        | Hue                     |   -180~180    |   0                   |
        | Saturation              |   0~255       |   128                 |
        | Brightness              |   -255~255    |   0                   |
        | Gamma                   |   20~180      |   100                 |
        | WBGain                  |   -127~127    |   0                   |
        ------------------------------------------------------------------|
    */
    
    /*
    * bAutoExposure:
    *   0: disable auto exposure
    *   1: auto exposure continue mode
    *   2: auto exposure once mode
    */
    public int get_AutoExpoEnable() throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_AutoExpoEnable(_handle, p));
        return p.getValue();
    }
    
    public void put_AutoExpoEnable(int bAutoExposure) throws HRESULTException {
        errCheck(_lib.Toupcam_put_AutoExpoEnable(_handle, bAutoExposure));
    }
    
    public short get_AutoExpoTarget() throws HRESULTException {
        ShortByReference p = new ShortByReference();
        errCheck(_lib.Toupcam_get_AutoExpoTarget(_handle, p));
        return p.getValue();
    }
    
    public void put_AutoExpoTarget(short Target) throws HRESULTException {
        errCheck(_lib.Toupcam_put_AutoExpoTarget(_handle, Target));
    }
    
    
    public void put_AutoExpoRange(int maxTime, int minTime, short maxGain, short minGain) throws HRESULTException {
        errCheck(_lib.Toupcam_put_AutoExpoRange(_handle, maxTime, minTime, maxGain, minGain));
    }
    
    public int[] get_AutoExpoRange() throws HRESULTException {
        IntByReference maxTime = new IntByReference();
        IntByReference minTime = new IntByReference();
        ShortByReference maxGain = new ShortByReference();
        ShortByReference minGain = new ShortByReference();
        errCheck(_lib.Toupcam_get_AutoExpoRange(_handle, maxTime, minTime, maxGain, minGain));
        return new int[] { maxTime.getValue(), minTime.getValue(), maxGain.getValue(), minGain.getValue() };
    }
    
    public void put_MaxAutoExpoTimeAGain(int maxTime, short maxGain) throws HRESULTException {
        errCheck(_lib.Toupcam_put_MaxAutoExpoTimeAGain(_handle, maxTime, maxGain));
    }
    
    public int[] get_MaxAutoExpoTimeAGain() throws HRESULTException {
        IntByReference p = new IntByReference();
        ShortByReference q = new ShortByReference();
        errCheck(_lib.Toupcam_get_MaxAutoExpoTimeAGain(_handle, p, q));
        return new int[] { p.getValue(), q.getValue() };
    }
    
    public void put_MinAutoExpoTimeAGain(int minTime, short minGain) throws HRESULTException {
        errCheck(_lib.Toupcam_put_MinAutoExpoTimeAGain(_handle, minTime, minGain));
    }
    
    public int[] get_MinAutoExpoTimeAGain() throws HRESULTException {
        IntByReference p = new IntByReference();
        ShortByReference q = new ShortByReference();
        errCheck(_lib.Toupcam_get_MinAutoExpoTimeAGain(_handle, p, q));
        return new int[] { p.getValue(), q.getValue() };
    }
    
    /* in microseconds */
    public int get_ExpoTime() throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_ExpoTime(_handle, p));
        return p.getValue();
    }
    
    /* actual exposure time */
    public int get_RealExpoTime() throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_RealExpoTime(_handle, p));
        return p.getValue();
    }
    
    /* in microseconds */
    public void put_ExpoTime(int Time) throws HRESULTException {
        errCheck(_lib.Toupcam_put_ExpoTime(_handle, Time));
    }
    
    /* min, max, default */
    public int[] get_ExpTimeRange() throws HRESULTException {
        IntByReference p = new IntByReference();
        IntByReference q = new IntByReference();
        IntByReference r = new IntByReference();
        errCheck(_lib.Toupcam_get_ExpTimeRange(_handle, p, q, r));
        return new int[] { p.getValue(), q.getValue(), r.getValue() };
    }
    
    /* percent, such as 300 */
    public short get_ExpoAGain() throws HRESULTException{
        ShortByReference p = new ShortByReference();
        errCheck(_lib.Toupcam_get_ExpoAGain(_handle, p));
        return p.getValue();
    }
    
    /* percent */
    public void put_ExpoAGain(short Gain) throws HRESULTException {
        errCheck(_lib.Toupcam_put_ExpoAGain(_handle, Gain));
    }
    
    /* min, max, default */
    public short[] get_ExpoAGainRange() throws HRESULTException {
        ShortByReference p = new ShortByReference();
        ShortByReference q = new ShortByReference();
        ShortByReference r = new ShortByReference();
        errCheck(_lib.Toupcam_get_ExpoAGainRange(_handle, p, q, r));
        return new short[] { p.getValue(), q.getValue(), r.getValue() };
    }
    
    public void put_LevelRange(short[] aLow, short[] aHigh) throws HRESULTException {
        if (aLow.length != 4 || aHigh.length != 4)
            errCheck(HRESULTException.E_INVALIDARG);
        errCheck(_lib.Toupcam_put_LevelRange(_handle, aLow, aHigh));
    }
    
    public void get_LevelRange(short[] aLow, short[] aHigh) throws HRESULTException {
        if (aLow.length != 4 || aHigh.length != 4)
            errCheck(HRESULTException.E_INVALIDARG);
        errCheck(_lib.Toupcam_get_LevelRange(_handle, aLow, aHigh));
    }

    public void put_LevelRangeV2(short mode, int roiX, int roiY, int roiWidth, int roiHeight, short[] aLow, short[] aHigh) throws HRESULTException {
        if (aLow.length != 4 || aHigh.length != 4)
            errCheck(HRESULTException.E_INVALIDARG);
        RECT rc = new RECT();
        rc.left = roiX;
        rc.right = roiX + roiWidth;
        rc.top = roiY;
        rc.bottom = roiY + roiHeight;
        errCheck(_lib.Toupcam_put_LevelRangeV2(_handle, mode, rc, aLow, aHigh));
    }

    public int[] get_LevelRangeV2(short[] aLow, short[] aHigh) throws HRESULTException {
        if (aLow.length != 4 || aHigh.length != 4)
            errCheck(HRESULTException.E_INVALIDARG);
        ShortByReference pMode = new ShortByReference();
        RECT rc = new RECT();
        errCheck(_lib.Toupcam_get_LevelRangeV2(_handle, pMode, rc, aLow, aHigh));
        return new int[] { pMode.getValue(), rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top };
    }

    public void put_Hue(int Hue) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Hue(_handle, Hue));
    }
    
    public int get_Hue() throws HRESULTException {
        IntByReference p = new IntByReference(HUE_DEF);
        errCheck(_lib.Toupcam_get_Hue(_handle, p));
        return p.getValue();
    }
    
    public void put_Saturation(int Saturation) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Saturation(_handle, Saturation));
    }
    
    public int get_Saturation() throws HRESULTException {
        IntByReference p = new IntByReference(SATURATION_DEF);
        errCheck(_lib.Toupcam_get_Saturation(_handle, p));
        return p.getValue();
    }
    
    public void put_Brightness(int Brightness) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Brightness(_handle, Brightness));
    }
    
    public int get_Brightness() throws HRESULTException {
        IntByReference p = new IntByReference(BRIGHTNESS_DEF);
        errCheck(_lib.Toupcam_get_Brightness(_handle, p));
        return p.getValue();
    }
    
    public int get_Contrast() throws HRESULTException {
        IntByReference p = new IntByReference(CONTRAST_DEF);
        errCheck(_lib.Toupcam_get_Contrast(_handle, p));
        return p.getValue();
    }
    
    public void put_Contrast(int Contrast) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Contrast(_handle, Contrast));
    }
    
    public int get_Gamma() throws HRESULTException {
        IntByReference p = new IntByReference(GAMMA_DEF);
        errCheck(_lib.Toupcam_get_Gamma(_handle, p));
        return p.getValue();
    }
    
    public void put_Gamma(int Gamma) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Gamma(_handle, Gamma));
    }
    
    /* monochromatic mode */
    public boolean get_Chrome() throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_Chrome(_handle, p));
        return (p.getValue() != 0);
    }
    
    public void put_Chrome(boolean bChrome) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Chrome(_handle, bChrome ? 1 : 0));
    }
    
    /* vertical flip */
    public boolean get_VFlip() throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_VFlip(_handle, p));
        return (p.getValue() != 0);
    }
    
    public void put_VFlip(boolean bVFlip) throws HRESULTException {
        errCheck(_lib.Toupcam_put_VFlip(_handle, bVFlip ? 1 : 0));
    }
    
    public boolean get_HFlip() throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_HFlip(_handle, p));
        return (p.getValue() != 0);
    }
    
    /* horizontal flip */
    public void put_HFlip(boolean bHFlip) throws HRESULTException {
        errCheck(_lib.Toupcam_put_HFlip(_handle, bHFlip ? 1 : 0));
    }
    
    /* negative film */
    public boolean get_Negative() throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_Negative(_handle, p));
        return (p.getValue() != 0);
    }
    
    /* negative film */
    public void put_Negative(boolean bNegative) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Negative(_handle, bNegative ? 1 : 0));
    }
    
    public void put_Speed(short nSpeed) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Speed(_handle, nSpeed));
    }
    
    public short get_Speed() throws HRESULTException {
        ShortByReference p = new ShortByReference();
        errCheck(_lib.Toupcam_get_Speed(_handle, p));
        return p.getValue();
    }
    
    /* power supply:
            0 => 60HZ AC
            1 => 50Hz AC
            2 => DC
    */
    public void put_HZ(int nHZ) throws HRESULTException {
        errCheck(_lib.Toupcam_put_HZ(_handle, nHZ));
    }
    
    public int get_HZ() throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_HZ(_handle, p));
        return p.getValue();
    }
    
    /* skip or bin */
    public void put_Mode(boolean bSkip) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Mode(_handle, bSkip ? 1 : 0));
    }
    
    public boolean get_Mode() throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_Mode(_handle, p));
        return (p.getValue() != 0);
    }
    
    /* White Balance, Temp/Tint mode */
    public void put_TempTint(int nTemp, int nTint) throws HRESULTException {
        errCheck(_lib.Toupcam_put_TempTint(_handle, nTemp, nTint));
    }
    
    /* White Balance, Temp/Tint mode */
    public int[] get_TempTint() throws HRESULTException {
        IntByReference p = new IntByReference();
        IntByReference q = new IntByReference();
        errCheck(_lib.Toupcam_get_TempTint(_handle, p, q));
        return new int[] { p.getValue(), q.getValue() };
    }
    
    /* White Balance, RGB Gain Mode */
    public void put_WhiteBalanceGain(int[] aGain) throws HRESULTException {
        if (aGain.length != 3)
            errCheck(HRESULTException.E_INVALIDARG);
        errCheck(_lib.Toupcam_put_WhiteBalanceGain(_handle, aGain));
    }
    
    /* White Balance, RGB Gain Mode */
    public void get_WhiteBalanceGain(int[] aGain) throws HRESULTException {
        if (aGain.length != 3)
            errCheck(HRESULTException.E_INVALIDARG);
        errCheck(_lib.Toupcam_get_WhiteBalanceGain(_handle, aGain));
    }
    
    public void put_AWBAuxRect(int X, int Y, int Width, int Height) throws HRESULTException {
        RECT rc = new RECT();
        rc.left = X;
        rc.right = X + Width;
        rc.top = Y;
        rc.bottom = Y + Height;
        errCheck(_lib.Toupcam_put_AWBAuxRect(_handle, rc));
    }
    
    /* left, top, width, height */
    public int[] get_AWBAuxRect() throws HRESULTException {
        RECT rc = new RECT();
        errCheck(_lib.Toupcam_get_AWBAuxRect(_handle, rc));
        return new int[] { rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top };
    }
    
    public void put_AEAuxRect(int X, int Y, int Width, int Height) throws HRESULTException {
        RECT rc = new RECT();
        rc.left = X;
        rc.right = X + Width;
        rc.top = Y;
        rc.bottom = Y + Height;
        errCheck(_lib.Toupcam_put_AEAuxRect(_handle, rc));
    }
    
    /* left, top, width, height */
    public int[] get_AEAuxRect() throws HRESULTException {
        RECT rc = new RECT();
        errCheck(_lib.Toupcam_get_AEAuxRect(_handle, rc));
        return new int[] { rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top };
    }
    
    public void put_BlackBalance(short[] aSub) throws HRESULTException {
        if (aSub.length != 3)
            errCheck(HRESULTException.E_INVALIDARG);
        errCheck(_lib.Toupcam_put_BlackBalance(_handle, aSub));
    }
    
    public short[] get_BlackBalance() throws HRESULTException {
        short[] p = new short[3];
        errCheck(_lib.Toupcam_get_BlackBalance(_handle, p));
        return p;
    }
    
    public void put_ABBAuxRect(int X, int Y, int Width, int Height) throws HRESULTException {
        RECT rc = new RECT();
        rc.left = X;
        rc.right = X + Width;
        rc.top = Y;
        rc.bottom = Y + Height;
        errCheck(_lib.Toupcam_put_ABBAuxRect(_handle, rc));
    }
    
    /* left, top, width, height */
    public int[] get_ABBAuxRect() throws HRESULTException {
        RECT rc = new RECT();
        errCheck(_lib.Toupcam_get_ABBAuxRect(_handle, rc));
        return new int[] { rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top };
    }
    
    /* width, height */
    public int[] get_StillResolution(int nResolutionIndex) throws HRESULTException {
        IntByReference p = new IntByReference();
        IntByReference q = new IntByReference();
        errCheck(_lib.Toupcam_get_StillResolution(_handle, nResolutionIndex, p, q));
        return new int[] { p.getValue(), q.getValue() };
    }
    
    /* led state:
        iLed: Led index, (0, 1, 2, ...)
        iState: 1 => Ever bright; 2 => Flashing; other => Off
        iPeriod: Flashing Period (>= 500ms)
    */
    public void put_LEDState(short iLed, short iState, short iPeriod) throws HRESULTException {
        errCheck(_lib.Toupcam_put_LEDState(_handle, iLed, iState, iPeriod));
    }
    
    public void write_EEPROM(int addr, ByteBuffer pBuffer) throws HRESULTException {
        if (pBuffer.isDirect())
            errCheck(_lib.Toupcam_write_EEPROM(_handle, addr, Native.getDirectBufferPointer(pBuffer), pBuffer.remaining()));
        else if (pBuffer.hasArray())
            write_EEPROM(addr, pBuffer.array());
        else
            errCheck(HRESULTException.E_INVALIDARG);
    }
    
    public void write_EEPROM(int addr, byte[] pBuffer) throws HRESULTException {
        errCheck(_lib.Toupcam_write_EEPROMArray(_handle, addr, pBuffer, pBuffer.length));
    }
    
    public int read_EEPROM(int addr, ByteBuffer pBuffer) throws HRESULTException {
        if (pBuffer.isDirect())
            return errCheck(_lib.Toupcam_read_EEPROM(_handle, addr, Native.getDirectBufferPointer(pBuffer), pBuffer.remaining()));
        else if (pBuffer.hasArray())
            return read_EEPROM(addr, pBuffer.array());
        else
            return errCheck(HRESULTException.E_INVALIDARG);
    }
    
    public int read_EEPROM(int addr, byte[] pBuffer) throws HRESULTException {
        return errCheck(_lib.Toupcam_read_EEPROMArray(_handle, addr, pBuffer, pBuffer.length));
    }

    /* Flash:
      action = FLASH_XXXX: read, write, erase, query total size, query read/write block size, query erase block size
      addr = address
      see democpp
    */
    public int rwc_Flash(int action, int addr, ByteBuffer pBuffer) throws HRESULTException {
        if (pBuffer.isDirect())
            return errCheck(_lib.Toupcam_rwc_Flash(_handle, action, addr, pBuffer.remaining(), Native.getDirectBufferPointer(pBuffer)));
        else if (pBuffer.hasArray())
            return rwc_Flash(action, addr, pBuffer.array());
        else
            return errCheck(HRESULTException.E_INVALIDARG);
    }

    public int rwc_Flash(int action, int addr, byte[] pBuffer) throws HRESULTException {
        return errCheck(_lib.Toupcam_rwc_FlashArray(_handle, action, addr, pBuffer.length, pBuffer));
    }

    public void write_Pipe(int pipeId, ByteBuffer pBuffer) throws HRESULTException {
        if (pBuffer.isDirect())
            errCheck(_lib.Toupcam_write_Pipe(_handle, pipeId, Native.getDirectBufferPointer(pBuffer), pBuffer.remaining()));
        else if (pBuffer.hasArray())
            write_Pipe(pipeId, pBuffer.array());
        else
            errCheck(HRESULTException.E_INVALIDARG);
    }
    
    public void write_Pipe(int pipeId, byte[] pBuffer) throws HRESULTException {
        errCheck(_lib.Toupcam_write_PipeArray(_handle, pipeId, pBuffer, pBuffer.length));
    }
    
    public int read_Pipe(int pipeId, ByteBuffer pBuffer) throws HRESULTException {
        if (pBuffer.isDirect())
            return errCheck(_lib.Toupcam_read_Pipe(_handle, pipeId, Native.getDirectBufferPointer(pBuffer), pBuffer.remaining()));
        else if (pBuffer.hasArray())
            return read_Pipe(pipeId, pBuffer.array());
        else
            return errCheck(HRESULTException.E_INVALIDARG);
    }
    
    public int read_Pipe(int pipeId, byte[] pBuffer) throws HRESULTException {
        return errCheck(_lib.Toupcam_read_PipeArray(_handle, pipeId, pBuffer, pBuffer.length));
    }
    
    public void feed_Pipe(int pipeId) throws HRESULTException {
        errCheck(_lib.Toupcam_feed_Pipe(_handle, pipeId));
    }
    
    public void write_UART(ByteBuffer pBuffer) throws HRESULTException {
        if (pBuffer.isDirect())
            errCheck(_lib.Toupcam_write_UART(_handle, Native.getDirectBufferPointer(pBuffer), pBuffer.remaining()));
        else if (pBuffer.hasArray())
            write_UART(pBuffer.array());
        else
            errCheck(HRESULTException.E_INVALIDARG);
    }
    
    public void write_UART(byte[] pBuffer) throws HRESULTException {
        errCheck(_lib.Toupcam_write_UARTArray(_handle, pBuffer, pBuffer.length));
    }
    
    public int read_UART(ByteBuffer pBuffer) throws HRESULTException {
        if (pBuffer.isDirect())
            return errCheck(_lib.Toupcam_read_UART(_handle, Native.getDirectBufferPointer(pBuffer), pBuffer.remaining()));
        else if (pBuffer.hasArray())
            return read_UART(pBuffer.array());
        else
            return errCheck(HRESULTException.E_INVALIDARG);
    }
    
    public int read_UART(byte[] pBuffer) throws HRESULTException {
        return errCheck(_lib.Toupcam_read_UARTArray(_handle, pBuffer, pBuffer.length));
    }
    
    public void put_Option(int iOption, int iValue) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Option(_handle, iOption, iValue));
    }
    
    public int get_Option(int iOption) throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_Option(_handle, iOption, p));
        return p.getValue();
    }

    /*
     * cmd:
     * -1:       query the number
     * 0~number: query the nth pixel format
     * output:   PIXELFORMAT_xxxx
    */
    public int get_PixelFormatSupport(byte cmd) throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_get_PixelFormatSupport(_handle, cmd, p));
        return p.getValue();
    }

    /* Hardware Binning
     * Value: 1x1, 2x2, etc
     * Method: Average, Add, Skip
     */
    void put_Binning(Pointer h, String Value, String Method) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Binning(_handle, Value, Method));
    }

    String[] get_Binning(Pointer h) throws HRESULTException {
        PointerByReference pValue = new PointerByReference();
        PointerByReference pMethod = new PointerByReference();
        errCheck(_lib.Toupcam_get_Binning(_handle, pValue, pMethod));
        String[] arrString = new String[2];
        arrString[0] = pValue.getValue().getString(0);
        arrString[1] = pMethod.getValue().getString(0);
        return arrString;
    }

    int get_BinningNumber() throws HRESULTException {
        return _lib.Toupcam_get_BinningNumber(_handle);
    }

    String get_BinningValue(int index) throws HRESULTException {
        PointerByReference pValue = new PointerByReference();
        errCheck(_lib.Toupcam_get_BinningValue(_handle, index, pValue));
        return pValue.getValue().getString(0);
    }

    String get_BinningMethod(int index) throws HRESULTException {
        PointerByReference pMethod = new PointerByReference();
        errCheck(_lib.Toupcam_get_BinningMethod(_handle, index, pMethod));
        return pMethod.getValue().getString(0);
    }

    public void put_SelfTrigger(SelfTrigger pSt) throws HRESULTException {
        errCheck(_lib.Toupcam_put_SelfTrigger(_handle, pSt));
    }

    public SelfTrigger get_SelfTrigger() throws HRESULTException {
        SelfTrigger p = new SelfTrigger();
        errCheck(_lib.Toupcam_get_SelfTrigger(_handle, p));
        return p;
    }

    public void put_Linear(byte[] v8, short[] v16) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Linear(_handle, v8, v16));
    }
    
    public void put_Curve(byte[] v8, short[] v16) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Curve(_handle, v8, v16));
    }
    
    public void put_ColorMatrix(double[] v) throws HRESULTException {
        if (v.length != 9)
            errCheck(HRESULTException.E_INVALIDARG);
        errCheck(_lib.Toupcam_put_ColorMatrix(_handle, v));
    }
    
    public void put_InitWBGain(short[] v) throws HRESULTException {
        if (v.length != 3)
            errCheck(HRESULTException.E_INVALIDARG);
        errCheck(_lib.Toupcam_put_InitWBGain(_handle, v));
    }
    
    /* get the temperature of the sensor, in 0.1 degrees Celsius (32 means 3.2 degrees Celsius, -35 means -3.5 degree Celsius) */
    public short get_Temperature() throws HRESULTException {
        ShortByReference p = new ShortByReference();
        errCheck(_lib.Toupcam_get_Temperature(_handle, p));
        return p.getValue();
    }
    
    /* set the target temperature of the sensor or TEC, in 0.1 degrees Celsius (32 means 3.2 degrees Celsius, -35 means -3.5 degree Celsius)
        set "-2730" or below means using the default value of this model
    */
    public void put_Temperature(short nTemperature) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Temperature(_handle, nTemperature));
    }
    
    /* xOffset, yOffset, xWidth, yHeight: must be even numbers */
    public void put_Roi(int xOffset, int yOffset, int xWidth, int yHeight) throws HRESULTException {
        errCheck(_lib.Toupcam_put_Roi(_handle, xOffset, yOffset, xWidth, yHeight));
    }
    
    /* xOffset, yOffset, xWidth, yHeight */
    public int[] get_Roi() throws HRESULTException {
        IntByReference p = new IntByReference();
        IntByReference q = new IntByReference();
        IntByReference r = new IntByReference();
        IntByReference s = new IntByReference();
        errCheck(_lib.Toupcam_get_Roi(_handle, p, q, r, s));
        return new int[] { p.getValue(), q.getValue(), r.getValue(), s.getValue() };
    }

    public void put_RoiN(int[] xOffset, int[] yOffset, int[] xWidth, int[] yHeight) throws HRESULTException {
        if (xOffset.length != yOffset.length || xOffset.length != xWidth.length || xOffset.length != yHeight.length)
            throw new HRESULTException(HRESULTException.E_INVALIDARG);
        errCheck(_lib.Toupcam_put_RoiN(_handle, xOffset, yOffset, xWidth, yHeight, xOffset.length));
    }
    
    public void put_XY(int x, int y) throws HRESULTException {
        errCheck(_lib.Toupcam_put_XY(_handle, x, y));
    }
    
    /*
        get the actual frame rate of the camera at the most recent time (about a few seconds):
        return (Frame, Time, TotalFrame)
        framerate (fps) = Frame * 1000.0 / Time
    */
    public int[] get_FrameRate() throws HRESULTException {
        IntByReference p = new IntByReference();
        IntByReference q = new IntByReference();
        IntByReference r = new IntByReference();
        errCheck(_lib.Toupcam_get_FrameRate(_handle, p, q, r));
        return new int[] { p.getValue(), q.getValue(), r.getValue() };
    }
    
    public void LevelRangeAuto() throws HRESULTException {
        errCheck(_lib.Toupcam_LevelRangeAuto(_handle));
    }
    
    /* Auto White Balance "Once", Temp/Tint Mode */
    public void AwbOnce() throws HRESULTException {
        errCheck(_lib.Toupcam_AwbOnce(_handle, null, null));
    }
    
    public void AwbOnePush() throws HRESULTException {
        AwbOnce();
    }
    
    /* Auto White Balance "Once", RGB Gain Mode */
    public void AwbInit() throws HRESULTException {
        errCheck(_lib.Toupcam_AwbInit(_handle, null, null));
    }
    
    public void AbbOnce() throws HRESULTException {
        errCheck(_lib.Toupcam_AbbOnce(_handle, null, null));
    }
    
    public void AbbOnePush() throws HRESULTException {
        AbbOnce();
    }
    
    public void FfcOnce() throws HRESULTException {
        errCheck(_lib.Toupcam_FfcOnce(_handle));
    }
    
    public void FfcOnePush() throws HRESULTException {
        FfcOnce();
    }
    
    public void DfcOnce() throws HRESULTException {
        errCheck(_lib.Toupcam_DfcOnce(_handle));
    }
    
    public void DfcOnePush() throws HRESULTException {
        DfcOnce();
    }
    
    public void FpncOnce() throws HRESULTException {
        errCheck(_lib.Toupcam_FpncOnce(_handle));
    }
    
    public void FfcExport(String filePath) throws HRESULTException {
        if (Platform.isWindows())
            errCheck(((WinLibrary)_lib).Toupcam_FfcExport(_handle, new WString(filePath)));
        else
            errCheck(((CLibrary)_lib).Toupcam_FfcExport(_handle, filePath));
    }
    
    public void FfcImport(String filePath) throws HRESULTException {
        if (Platform.isWindows())
            errCheck(((WinLibrary)_lib).Toupcam_FfcImport(_handle, new WString(filePath)));
        else
            errCheck(((CLibrary)_lib).Toupcam_FfcImport(_handle, filePath));
    }
    
    public void DfcExport(String filePath) throws HRESULTException {
        if (Platform.isWindows())
            errCheck(((WinLibrary)_lib).Toupcam_DfcExport(_handle, new WString(filePath)));
        else
            errCheck(((CLibrary)_lib).Toupcam_DfcExport(_handle, filePath));
    }
    
    public void DfcImport(String filePath) throws HRESULTException {
        if (Platform.isWindows())
            errCheck(((WinLibrary)_lib).Toupcam_DfcImport(_handle, new WString(filePath)));
        else
            errCheck(((CLibrary)_lib).Toupcam_DfcImport(_handle, filePath));
    }
    
    public void FpncExport(String filePath) throws HRESULTException {
        if (Platform.isWindows())
            errCheck(((WinLibrary)_lib).Toupcam_FpncExport(_handle, new WString(filePath)));
        else
            errCheck(((CLibrary)_lib).Toupcam_FpncExport(_handle, filePath));
    }
    
    public void FpncImport(String filePath) throws HRESULTException {
        if (Platform.isWindows())
            errCheck(((WinLibrary)_lib).Toupcam_FpncImport(_handle, new WString(filePath)));
        else
            errCheck(((CLibrary)_lib).Toupcam_FpncImport(_handle, filePath));
    }
    
    public int IoControl(int ioLine, int eType, int outVal) throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_IoControl(_handle, ioLine, eType, outVal, p));
        return p.getValue();
    }
    
    public int AAF(int action, int outVal) throws HRESULTException {
        IntByReference p = new IntByReference();
        errCheck(_lib.Toupcam_AAF(_handle, action, outVal, p));
        return p.getValue();
    }

    private static LensInfo Ptr2LensInfo(Pointer qtr)
    {
        LensInfo pInfo = new LensInfo();
        long qoffset = 0;
        pInfo.lensID = qtr.getShort(qoffset);
        qoffset += 2;
        pInfo.lensType = qtr.getByte(qoffset);
        qoffset += 1;
        pInfo.statusAfmf = qtr.getByte(qoffset);     /* LENS_AF = 0x00,  LENS_MF = 0x80 */
        qoffset += 1;

        pInfo.maxFocalLength = qtr.getShort(qoffset);
        qoffset += 2;
        pInfo.curFocalLength = qtr.getShort(qoffset);
        qoffset += 2;
        pInfo.minFocalLength = qtr.getShort(qoffset);
        qoffset += 2;

        pInfo.farFM = qtr.getShort(qoffset);           /* focus motor, absolute value */
        qoffset += 2;
        pInfo.curFM = qtr.getShort(qoffset);           /* current focus motor */
        qoffset += 2;
        pInfo.nearFM = qtr.getShort(qoffset);
        qoffset += 2;

        pInfo.maxFocusDistance = qtr.getShort(qoffset);
        qoffset += 2;
        pInfo.minFocusDistance = qtr.getShort(qoffset);
        qoffset += 2;

        pInfo.curAM = qtr.getByte(qoffset);
        qoffset += 1;
        pInfo.maxAM = qtr.getByte(qoffset);            /* maximum Aperture, mimimum F# */
        qoffset += 1;
        pInfo.minAM = qtr.getByte(qoffset);            /* mimimum Aperture, maximum F# */
        qoffset += 1;
        pInfo.posAM = qtr.getByte(qoffset);            /* used for set aperture motor to posAM, it is an index */
        qoffset += 1;
        pInfo.posFM = qtr.getInt(qoffset);             /* used for set focus motor to posFM */
        qoffset += 4;

        int sizeFN = qtr.getInt(qoffset);
        qoffset += 4;
        if (sizeFN >= 0) {
            Pointer sp = qtr.getPointer(qoffset);
            if (sp != null) {
                pInfo.arrayFN = new String[sizeFN];
                for (int i = 0; i < sizeFN; ++i) {
                    if (sp.getPointer(Native.POINTER_SIZE * i) != null)
                        pInfo.arrayFN[i] = sp.getPointer(Native.POINTER_SIZE * i).getString(0);
                }
            }
        }
        
        qoffset += Native.POINTER_SIZE;
        if (qtr.getPointer(qoffset) != null)
            pInfo.lensName = qtr.getPointer(qoffset).getString(0);

        return pInfo;
    }

    public LensInfo get_LensInfo() throws HRESULTException {
        Memory ptr = new Memory(128);
        errCheck(_lib.Toupcam_get_LensInfo(_handle, ptr));
        return Ptr2LensInfo(ptr);
    }

    public AFState get_AFState() throws HRESULTException {
        Memory ptr = new Memory(16);
        errCheck(_lib.Toupcam_get_AFState(_handle, ptr));
        AFState pState = new AFState();
        pState.AF_Mode = ptr.getInt(0);
        pState.AF_Status = ptr.getInt(4);
        pState.AF_LensAP_Update_Flag = ptr.getByte(8);
        return pState;
    }

    public void put_AFMode(int mode) throws HRESULTException {
        errCheck(_lib.Toupcam_put_AFMode(_handle, mode));
    }

    public void put_AFRoi(int xOffset, int yOffset, int xWidth, int yHeight) throws HRESULTException {
        errCheck(_lib.Toupcam_put_AFRoi(_handle, xOffset, yOffset, xWidth, yHeight));
    }

    public void put_AFAperture(int iAperture) throws HRESULTException {
        errCheck(_lib.Toupcam_put_AFAperture(_handle, iAperture));
    }

    public void put_AFFMPos(int iFMPos) throws HRESULTException {
        errCheck(_lib.Toupcam_put_AFFMPos(_handle, iFMPos));
    }
    
    public FocusMotor get_FocusMotor() throws HRESULTException {
        FocusMotor pFocusMotor = new FocusMotor();
        errCheck(_lib.Toupcam_get_FocusMotor(_handle, pFocusMotor));
        return pFocusMotor;
    }

    private static void OnHistogramCallback(Pointer aHist, int nFlag, Pointer ctxHistogram) {
        Object o = _hash.get((int)Pointer.nativeValue(ctxHistogram));
        if (o instanceof toupcam) {
            toupcam t = (toupcam)o;
            if (t._histogram != null) {
                int arraySize = 1 << (nFlag & 0x0f);
                if ((nFlag & 0x00008000) == 0)
                    arraySize *= 3;
                t._histogram.onHistogram(aHist.getIntArray(0, arraySize));
            }
        }
    }

    public void GetHistogram(IHistogramCallback cb) throws HRESULTException {
        _histogram = cb;
        if (Platform.isWindows()) {
            _histogramcallback = new WinLibrary.HISTOGRAM_CALLBACK() {
                @Override
                public void invoke(Pointer aHist, int nFlag, Pointer ctxHistogram) {
                    OnHistogramCallback(aHist, nFlag, ctxHistogram);
                }
            };
            Native.setCallbackThreadInitializer(_histogramcallback, new CallbackThreadInitializer(false, false, "histogramcallback"));
            errCheck(((WinLibrary)_lib).Toupcam_GetHistogramV2(_handle, (WinLibrary.HISTOGRAM_CALLBACK)_histogramcallback, new Pointer(_objid)));
        }
        else {
            _histogramcallback = new CLibrary.HISTOGRAM_CALLBACK() {
                @Override
                public void invoke(Pointer aHist, int nFlag, Pointer ctxHistogram) {
                    OnHistogramCallback(aHist, nFlag, ctxHistogram);
                }
            };
            Native.setCallbackThreadInitializer(_histogramcallback, new CallbackThreadInitializer(false, false, "histogramcallback"));
            errCheck(((CLibrary)_lib).Toupcam_GetHistogramV2(_handle, (CLibrary.HISTOGRAM_CALLBACK)_histogramcallback, new Pointer(_objid)));
        }
    }
    
    /*
        simulate replug:
        return > 0, the number of device has been replug
        return = 0, no device found
        return E_ACCESSDENIED if without UAC Administrator privileges
        for each device found, it will take about 3 seconds
    */
    public static void Replug(String camId) throws HRESULTException {
        if (Platform.isWindows())
            errCheck(((WinLibrary)_lib).Toupcam_Replug(new WString(camId)));
        else
            errCheck(((CLibrary)_lib).Toupcam_Replug(camId));
    }

    private static void OnProgressCallback(int percent, Pointer ctxProgress) {
        Object o = _hash.get((int)Pointer.nativeValue(ctxProgress));
        if (o instanceof IProgressCallback) {
            ((IProgressCallback)o).onProgress(percent);
        }
    }

    /*
        firmware update:
            camId: camera ID
            filePath: ufw file full path
            cb: progress percent callback
        Please do not unplug the camera or lost power during the upgrade process, this is very very important.
        Once an unplugging or power outage occurs during the upgrade process, the camera will no longer be available and can only be returned to the factory for repair.
    */
    public static void Update(String camId, String filePath, IProgressCallback cb) throws HRESULTException {
        int id = _clsid++;
        _hash.put(id, cb);
        if (Platform.isWindows()) {
            WinLibrary.PROGRESS_CALLBACK p = new WinLibrary.PROGRESS_CALLBACK() {
                @Override
                public void invoke(int percent, Pointer ctxProgress) {
                    OnProgressCallback(percent, ctxProgress);
                }
            };
            Native.setCallbackThreadInitializer(p, new CallbackThreadInitializer(false, false, "progressCallback"));
            errCheck(((WinLibrary)_lib).Toupcam_Update(new WString(camId), new WString(filePath), p, new Pointer(id)));
        }
        else {
            CLibrary.PROGRESS_CALLBACK p = new CLibrary.PROGRESS_CALLBACK() {
                @Override
                public void invoke(int percent, Pointer ctxProgress) {
                    OnProgressCallback(percent, ctxProgress);
                }
            };
            Native.setCallbackThreadInitializer(p, new CallbackThreadInitializer(false, false, "progressCallback"));
            errCheck(((CLibrary)_lib).Toupcam_Update(camId, filePath, p, new Pointer(id)));
        }
    }

    public static ModelV2 get_Model(short idVendor, short idProduct) {
        Pointer qtr = _lib.Toupcam_get_Model(idVendor, idProduct);
        if (qtr == null)
            return null;
        return toModelV2(qtr);
    }
    
    public static int[] Gain2TempTint(int[] gain) throws HRESULTException {
        IntByReference temp = new IntByReference();
        IntByReference tint = new IntByReference();
        errCheck(_lib.Toupcam_Gain2TempTint(gain, temp, tint));
        return new int[] { temp.getValue(), tint.getValue() };
    }
    
    public static void TempTint2Gain(int temp, int tint, int[] gain) throws HRESULTException {
        if (gain.length != 3)
            errCheck(HRESULTException.E_INVALIDARG);
        _lib.Toupcam_TempTint2Gain(temp, tint, gain);
    }
}
