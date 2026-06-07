Imports System.Runtime.InteropServices
Imports Microsoft.Win32.SafeHandles
#If Not (NETFX_CORE OrElse NETCOREAPP OrElse WINDOWS_UWP) Then
Imports System.Security.Permissions
Imports System.Runtime.ConstrainedExecution
#End If
Imports System.Collections.Generic
Imports System.Threading

'    Version: 59.29030.20250722
'
'    For Microsoft dotNET Framework & dotNet Core
'
'    We use P/Invoke to call into the toupcam.dll API, the VB.net class Toupcam is a thin wrapper class to the native api of toupcam.dll.
'    So the manual en.html and hans.html are also applicable for programming with toupcam.vb.
'    See it in the 'doc' directory:
'       (1) en.html, English
'       (2) hans.html, Simplified Chinese
'

'   Please distinguish between camera ID (camId) and camera SN:
'       (a) SN is unique and persistent, fixed inside the camera and remains unchanged, and does not change with connection or system restart.
'       (b) Camera ID (camId) may change due to connection or system restart. Enumerate the cameras to get the camera ID, and then call the Open function to pass in the camId parameter to open the camera.
'

Friend Class Toupcam
    Implements IDisposable

    <Flags>
    Public Enum eFLAG As Long
        FLAG_CMOS = &H1                           ' cmos sensor
        FLAG_CCD_PROGRESSIVE = &H2                ' progressive ccd sensor
        FLAG_CCD_INTERLACED = &H4                 ' interlaced ccd sensor
        FLAG_ROI_HARDWARE = &H8                   ' support hardware ROI
        FLAG_MONO = &H10                          ' monochromatic
        FLAG_BINSKIP_SUPPORTED = &H20             ' support bin/skip mode
        FLAG_USB30 = &H40                         ' usb3.0
        FLAG_TEC = &H80                           ' Thermoelectric Cooler
        FLAG_USB30_OVER_USB20 = &H100             ' usb3.0 camera connected to usb2.0 port
        FLAG_ST4 = &H200                          ' ST4
        FLAG_GETTEMPERATURE = &H400               ' support to get the temperature of the sensor
        FLAG_HIGH_FULLWELL = &H800                ' high fullwell capacity
        FLAG_RAW10 = &H1000                       ' pixel format, RAW 10bits
        FLAG_RAW12 = &H2000                       ' pixel format, RAW 12bits
        FLAG_RAW14 = &H4000                       ' pixel format, RAW 14bits
        FLAG_RAW16 = &H8000                       ' pixel format, RAW 16bits
        FLAG_FAN = &H10000                        ' cooling fan
        FLAG_TEC_ONOFF = &H20000                  ' Thermoelectric Cooler can be turn on or off, support to set the target temperature of TEC
        FLAG_ISP = &H40000                        ' ISP (Image Signal Processing) chip
        FLAG_TRIGGER_SOFTWARE = &H80000           ' support software trigger
        FLAG_TRIGGER_EXTERNAL = &H100000          ' support external trigger
        FLAG_TRIGGER_SINGLE = &H200000            ' only support trigger single: one trigger, one image
        FLAG_BLACKLEVEL = &H400000                ' support set and get the black level
        FLAG_AUTO_FOCUS = &H800000                ' support auto focus
        FLAG_BUFFER = &H1000000                   ' frame buffer
        FLAG_DDR = &H2000000                      ' use very large capacity DDR (Double Data Rate SDRAM) for frame buffer. The capacity is not less than one full frame
        FLAG_CG = &H4000000                       ' Conversion Gain: HCG, LCG
        FLAG_YUV411 = &H8000000                   ' pixel format, yuv411
        FLAG_VUYY = &H10000000                    ' pixel format, yuv422, VUYY
        FLAG_YUV444 = &H20000000                  ' pixel format, yuv444
        FLAG_RGB888 = &H40000000                  ' pixel format, RGB888
        FLAG_RAW8 = &H80000000                    ' pixel format, RAW8
        FLAG_GMCY8 = &H100000000                  ' pixel format, GMCY8
        FLAG_GMCY12 = &H200000000                 ' pixel format, GMCY12
        FLAG_UYVY = &H400000000                   ' pixel format, yuv422, UYVY
        FLAG_CGHDR = &H800000000                  ' Conversion Gain: HCG, LCG, HDR
        FLAG_GLOBALSHUTTER = &H1000000000         ' global shutter
        FLAG_FOCUSMOTOR = &H2000000000            ' support focus motor
        FLAG_PRECISE_FRAMERATE = &H4000000000     ' support precise framerate & bandwidth, see OPTION_PRECISE_FRAMERATE & OPTION_BANDWIDTH
        FLAG_HEAT = &H8000000000                  ' support heat to prevent fogging up
        FLAG_LOW_NOISE = &H10000000000            ' support low noise mode (Higher signal noise ratio, lower frame rate)
        FLAG_LEVELRANGE_HARDWARE = &H20000000000  ' hardware level range, put(get)_LevelRangeV2
        FLAG_EVENT_HARDWARE = &H40000000000       ' hardware event, such as exposure start & stop
        FLAG_LIGHTSOURCE = &H80000000000          ' embedded light source
        FLAG_FILTERWHEEL = &H100000000000         ' astro filter wheel
        FLAG_GIGE = &H200000000000                ' 1 Gigabit GigE
        FLAG_10GIGE = &H400000000000              ' 10 Gigabit GigE
        FLAG_5GIGE = &H800000000000               ' 5 Gigabit GigE
        FLAG_25GIGE = &H1000000000000             ' 2.5 Gigabit GigE
        FLAG_AUTOFOCUSER = &H2000000000000        ' astro auto focuser
        FLAG_LIGHT_SOURCE = &H4000000000000       ' stand alone light source
        FLAG_CAMERALINK = &H8000000000000         ' camera link
        FLAG_CXP = &H10000000000000               ' CXP: CoaXPress
        FLAG_RAW12PACK = &H20000000000000         ' pixel format, RAW 12bits packed
        FLAG_SELFTRIGGER = &H40000000000000       ' self trigger
        FLAG_RAW11 = &H80000000000000             ' pixel format, RAW 11bits
        FLAG_GHOPTO = &H100000000000000           ' ghopto sensor
        FLAG_RAW10PACK = &H200000000000000        ' pixel format, RAW 10bits packed
        FLAG_USB32 = &H400000000000000            ' usb3.2
        FLAG_USB32_OVER_USB30 = &H800000000000000 ' usb3.2 camera connected to usb3.0 port
    End Enum

    Public Enum eEVENT As UInteger
        EVENT_EXPOSURE = &H1                      ' exposure time or gain changed
        EVENT_TEMPTINT = &H2                      ' white balance changed, Temp/Tint mode
        EVENT_CHROME = &H3                        ' reversed, do not use it
        EVENT_IMAGE = &H4                         ' live image arrived, use PullImage to get this image
        EVENT_STILLIMAGE = &H5                    ' snap (still) frame arrived, use PullStillImage to get this frame
        EVENT_WBGAIN = &H6                        ' white balance changed, RGB Gain mode
        EVENT_TRIGGERFAIL = &H7                   ' trigger failed
        EVENT_BLACK = &H8                         ' black balance
        EVENT_FFC = &H9                           ' flat field correction status changed
        EVENT_DFC = &HA                           ' dark field correction status changed
        EVENT_ROI = &HB                           ' roi changed
        EVENT_LEVELRANGE = &HC                    ' level range changed
        EVENT_AUTOEXPO_CONV = &HD                 ' auto exposure convergence
        EVENT_AUTOEXPO_CONVFAIL = &HE             ' auto exposure once mode convergence failed
        EVENT_FPNC = &HF                          ' fix pattern noise correction status changed
        EVENT_ERROR = &H80                        ' generic error
        EVENT_DISCONNECTED = &H81                 ' camera disconnected
        EVENT_NOFRAMETIMEOUT = &H82               ' no frame timeout error
        EVENT_FOCUSPOS = &H84                     ' focus positon
        EVENT_NOPACKETTIMEOUT = &H85              ' no packet timeout
        EVENT_EXPO_START = &H4000                 ' hardware event: exposure start
        EVENT_EXPO_STOP = &H4001                  ' hardware event: exposure stop
        EVENT_TRIGGER_ALLOW = &H4002              ' hardware event: next trigger allow
        EVENT_HEARTBEAT = &H4003                  ' hardware event: heartbeat, can be used to monitor whether the camera is alive
        EVENT_TRIGGER_IN = &H4004                 ' hardware event: trigger in
        EVENT_FACTORY = &H8001                    ' restore factory settings
    End Enum

    ' [RW] = Read/Write; [RO] = Read Only; [WO] = Write Only
    Public Enum eOPTION As UInteger
        OPTION_NOFRAME_TIMEOUT = &H1               ' [RW] no frame timeout: 0 => disable, positive value (>= NOFRAME_TIMEOUT_MIN) => timeout milliseconds. default: disable
        ' [RW] set the priority of the internal thread which grab data from the usb device.
        '   Win: iValue: 0 => THREAD_PRIORITY_NORMAL; 1 => THREAD_PRIORITY_ABOVE_NORMAL; 2 => THREAD_PRIORITY_HIGHEST; 3 => THREAD_PRIORITY_TIME_CRITICAL; default: 1; see: https://docs.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadpriority
        '   Linux & macOS: The high 16 bits for the scheduling policy, and the low 16 bits for the priority; see: https://linux.die.net/man/3/pthread_setschedparam
        OPTION_THREAD_PRIORITY = &H2
        ' [RW]
        '  0: RGB mode
        '  1: RAW mode, read the CMOS or CCD raw data
        ' -1: RAW mode, the difference from 1 is the execution of FFC, DFC, FPNC, black balance, and white balance
        '  default value: 0
        OPTION_RAW = &H4
        OPTION_HISTOGRAM = &H5                     ' [RW] 0 = only one, 1 = continue mode
        OPTION_BITDEPTH = &H6                      ' [RW] 0 = 8 bits mode, 1 = 16 bits mode
        OPTION_FAN = &H7                           ' [RW] 0 = turn off the cooling fan, [1, max] = fan speed, set to "-1" means to use default fan speed
        OPTION_TEC = &H8                           ' [RW] 0 = turn off the thermoelectric cooler, 1 = turn on the thermoelectric cooler
        OPTION_LINEAR = &H9                        ' [RW] 0 = turn off the builtin linear tone mapping, 1 = turn on the builtin linear tone mapping, default value: 1
        OPTION_CURVE = &HA                         ' [RW] 0 = turn off the builtin curve tone mapping, 1 = turn on the builtin polynomial curve tone mapping, 2 = logarithmic curve tone mapping, default value: 2
        OPTION_TRIGGER = &HB                       ' [RW] 0 = video mode, 1 = software or simulated trigger mode, 2 = external trigger mode, 3 = external + software trigger, default value = 0
        OPTION_RGB = &HC                           ' [RW] 0 => RGB24; 1 => enable RGB48 format when bitdepth > 8; 2 => RGB32; 3 => 8 Bits Grey (only for mono camera); 4 => 16 Bits Grey (only for mono camera when bitdepth > 8); 64 => RGB64
        OPTION_COLORMATIX = &HD                    ' [RW] enable or disable the builtin color matrix, default value: 1
        OPTION_WBGAIN = &HE                        ' [RW] enable or disable the builtin white balance gain, default value: 1
        OPTION_TECTARGET = &HF                     ' [RW] get or set the target temperature of the thermoelectric cooler, in 0.1 degree Celsius. For example, 125 means 12.5 degree Celsius, -35 means -3.5 degree Celsius. Set "-2730" or below means using the default for that model
        ' [RW] auto exposure policy:
        '       0: Exposure Only
        '       1: Exposure Preferred
        '       2: Gain Only
        '       3: Gain Preferred
        '    default value: 1
        OPTION_AUTOEXP_POLICY = &H10
        OPTION_FRAMERATE = &H11                    ' [RW] limit the frame rate, the default value 0 means no limit
        ' [RW] demosaic method for both video and still image: BILINEAR = 0, VNG(Variable Number of Gradients) = 1, PPG(Patterned Pixel Grouping) = 2, AHD(Adaptive Homogeneity Directed) = 3, EA(Edge Aware) = 4, see https://en.wikipedia.org/wiki/Demosaicing
        '   In terms of CPU usage, EA is the lowest, followed by BILINEAR, and the others are higher.
        '   default value: 0
        OPTION_DEMOSAIC = &H12
        OPTION_DEMOSAIC_VIDEO = &H13               ' [RW] demosaic method for video
        OPTION_DEMOSAIC_STILL = &H14               ' [RW] demosaic method for still image
        OPTION_BLACKLEVEL = &H15                   ' [RW] black level
        OPTION_MULTITHREAD = &H16                  ' [RW] multithread image processing
        ' [RW] binning
        '     0x01: (no binning)
        '     n: (saturating add, n*n), 0x02(2*2), 0x03(3*3), 0x04(4*4), 0x05(5*5), 0x06(6*6), 0x07(7*7), 0x08(8*8). The Bitdepth of the data remains unchanged.
        '     0x40 | n: (unsaturated add, n*n, works only in RAW moden), 0x42(2*2), 0x43(3*3), 0x44(4*4), 0x45(5*5), 0x46(6*6), 0x47(7*7), 0x48(8*8). The Bitdepth of the data is increased. For example, the original data with bitdepth of 12 will increase the bitdepth by 2 bits and become 14 after 2*2 binning.
        '     0x80 | n: (average, n*n), 0x82(2*2), 0x83(3*3), 0x84(4*4), 0x85(5*5), 0x86(6*6), 0x87(7*7), 0x88(8*8). The Bitdepth of the data remains unchanged.
        ' The final image size is rounded down to an even number, such as 640/3 to get 212
        OPTION_BINNING = &H17
        OPTION_ROTATE = &H18                       ' [RW] rotate clockwise: 0, 90, 180, 270
        ' [RW] Conversion Gain:
        '     0 = LCG
        '     1 = HCG
        '     2 = HDR (for camera with flag FLAG_CGHDR)
        '     2 = MCG (for camera with flag FLAG_GHOPTO)
        OPTION_CG = &H19
        OPTION_PIXEL_FORMAT = &H1A                 ' [RW] pixel format
        ' [RW] flat field correction
        '     set:
        '         0: disable
        '         1: enable
        '        -1: reset
        '        (0xff000000 | n): average number, [1~255]
        '     get:
        '         (val & 0xff): 0 => disable, 1 => enable, 2 => inited
        '         ((val & 0xff00) >> 8): sequence
        '         ((val & 0xff0000) >> 16): average number
        OPTION_FFC = &H1B
        ' [RW] the number of the frames that DDR can cache
        '     1: DDR cache only one frame
        '     0: Auto:
        '         => one for video mode when auto exposure is enabled
        '         => full capacity for others
        '    -1: DDR can cache frames to full capacity
        OPTION_DDR_DEPTH = &H1C
        ' [RW] dark field correction
        '     set:
        '         0: disable
        '         1: enable
        '        -1: reset
        '        (0xff000000 | n): average number, [1~255]
        '     get:
        '         (val & 0xff): 0 => disable, 1 => enable, 2 => inited
        '         ((val & 0xff00) >> 8): sequence
        '         ((val & 0xff0000) >> 16): average number
        '         (val & 0x01000000): capture finished
        OPTION_DFC = &H1D
        ' [RW] Sharpening: (threshold << 24) | (radius << 16) | strength)
        '    strength: [0, 500], default: 0 (disable)
        '    radius: [1, 10]
        '    threshold: [0, 255]
        OPTION_SHARPENING = &H1E
        OPTION_FACTORY = &H1F                      ' [WO] restore the factory settings
        OPTION_TEC_VOLTAGE = &H20                  ' [RO] get the current TEC voltage in 0.1V, 59 mean 5.9V
        OPTION_TEC_VOLTAGE_MAX = &H21              ' [RW] TEC maximum voltage in 0.1V
        OPTION_DEVICE_RESET = &H22                 ' [WO] reset usb device, simulate a replug
        ' [RW] upsize down:
        '    1: yes
        '    0: no
        '    default: 1 (win), 0 (linux/macos)
        OPTION_UPSIDE_DOWN = &H23
        OPTION_FOCUSPOS = &H24                     ' [RW] focus positon
        OPTION_AFMODE = &H25                       ' [RW] auto focus mode, see ToupcamAFMode
        ' [RO] auto focus status, see ToupcamAFStaus
        OPTION_AFSTATUS = &H27
        ' [RW] test pattern:
        '    0: off
        '    3: monochrome diagonal stripes
        '    5: monochrome vertical stripes
        '    7: monochrome horizontal stripes
        '    9: chromatic diagonal stripes
        OPTION_TESTPATTERN = &H28
        OPTION_AUTOEXP_THRESHOLD = &H29            ' [RW] threshold of auto exposure, default value: 5, range = [2, 15]
        OPTION_BYTEORDER = &H2A                    ' [RW] Byte order, BGR or RGB: 0 => RGB, 1 => BGR, default value: 1(Win), 0(macOS, Linux, Android)
        OPTION_NOPACKET_TIMEOUT = &H2B             ' [RW] no packet timeout: 0 => disable, positive value (>= NOPACKET_TIMEOUT_MIN) => timeout milliseconds. default: disable
        OPTION_MAX_PRECISE_FRAMERATE = &H2C        ' [RO] get the precise frame rate maximum value in 0.1 fps, such as 115 means 11.5 fps. E_NOTIMPL means not supported
        OPTION_PRECISE_FRAMERATE = &H2D            ' [RW] precise frame rate current value in 0.1 fps, precise frame rate current value in 0.1 fps. use OPTION_MAX_PRECISE_FRAMERATE, OPTION_MIN_PRECISE_FRAMERATE to get the range. if the set value is out of range, E_INVALIDARG will be returned
        OPTION_BANDWIDTH = &H2E                    ' [RW] bandwidth, [1-100]%
        OPTION_RELOAD = &H2F                       ' [RW] reload the last frame in trigger mode
        OPTION_CALLBACK_THREAD = &H30              ' [RW] dedicated thread for callback: 0 => disable, 1 => enable, default: 0
        ' [RW] frontend (raw) frame buffer deque length, range: [2, 1024], default: 4
        ' All the memory will be pre-allocated when the camera starts, so, please attention to memory usage
        OPTION_FRONTEND_DEQUE_LENGTH = &H31
        OPTION_FRAME_DEQUE_LENGTH = &H31           ' [RW] alias of OPTION_FRONTEND_DEQUE_LENGTH
        OPTION_MIN_PRECISE_FRAMERATE = &H32        ' [RO] get the precise frame rate minimum value in 0.1 fps, such as 15 means 1.5 fps
        OPTION_SEQUENCER_ONOFF = &H33              ' [RW] sequencer trigger: on/off
        OPTION_SEQUENCER_NUMBER = &H34             ' [RW] sequencer trigger: number, range = [1, 255]
        ' [RW] sequencer trigger: exposure time, iOption = OPTION_SEQUENCER_EXPOTIME | index, iValue = exposure time
        '   For example, to set the exposure time of the third group to 50ms, call:
        '     put_Option(OPTION_SEQUENCER_EXPOTIME | 3, 50000)
        OPTION_SEQUENCER_EXPOTIME = &H1000000
        OPTION_SEQUENCER_EXPOGAIN = &H2000000      ' [RW] sequencer trigger: exposure gain, iOption = OPTION_SEQUENCER_EXPOGAIN | index, iValue = gain
        OPTION_DENOISE = &H35                      ' [RW] denoise, strength range: [0, 100], 0 means disable
        OPTION_HEAT_MAX = &H36                     ' [RO] get maximum level: heat to prevent fogging up
        OPTION_HEAT = &H37                         ' [RW] heat to prevent fogging up
        OPTION_LOW_NOISE = &H38                    ' [RW] low noise mode (Higher signal noise ratio, lower frame rate): 1 => enable
        OPTION_POWER = &H39                        ' [RO] get power consumption, unit: milliwatt
        OPTION_GLOBAL_RESET_MODE = &H3A            ' [RW] global reset mode
        OPTION_OPEN_ERRORCODE = &H3B               ' [RO] get the open camera error code
        ' [WO]
        ' 1 = hard flush, discard frames cached by camera DDR (if any)
        ' 2 = soft flush, discard frames cached by toupcam.dll (if any)
        ' 3 = both flush
        ' Toupcam_Flush means 'both flush'
        ' return the number of soft flushed frames if successful, HRESULT if failed
        OPTION_FLUSH = &H3D
        OPTION_NUMBER_DROP_FRAME = &H3E            ' [RO] get the number of frames that have been grabbed from the USB but dropped by the software
        '[RW]  0 = when camera is stopped, do not dump configuration automatically
        ' 1 = when camera is stopped, dump configuration automatically
        ' -1 = explicitly dump configuration once
        ' default: 1
        OPTION_DUMP_CFG = &H3F
        OPTION_DEFECT_PIXEL = &H40                 ' [RW] Defect Pixel Correction: 0 => disable, 1 => enable; default: 1
        ' [RW] backend (pipelined) frame buffer deque length (Only available in pull mode), range: [2, 1024], default: 3
        ' All the memory will be pre-allocated when the camera starts, so, please attention to memory usage
        OPTION_BACKEND_DEQUE_LENGTH = &H41
        OPTION_LIGHTSOURCE_MAX = &H42              ' [RO] get the light source range, [0 ~ max]
        OPTION_LIGHTSOURCE = &H43                  ' [RW] light source
        OPTION_HEARTBEAT = &H44                    ' [RW] Heartbeat interval in millisecond, range = [HEARTBEAT_MIN, HEARTBEAT_MAX], 0 = disable, default: disable
        OPTION_FRONTEND_DEQUE_CURRENT = &H45       ' [RO] get the current number in frontend deque
        OPTION_BACKEND_DEQUE_CURRENT = &H46        ' [RO] get the current number in backend deque
        ' [RW] enable or disable hardware event: 0 => disable, 1 => enable; default: disable
        '     (1) iOption = OPTION_EVENT_HARDWARE, master switch for notification of all hardware events
        '     (2) iOption = OPTION_EVENT_HARDWARE | (event type), a specific type of sub-switch
        ' Only if both the master switch and the sub-switch of a particular type remain on are actually enabled for that type of event notification.
        OPTION_EVENT_HARDWARE = &H4000000
        OPTION_PACKET_NUMBER = &H47                ' [RO] get the received packet number
        OPTION_FILTERWHEEL_SLOT = &H48             ' [RW] filter wheel slot number
        ' [RW] filter wheel position:
        '     set:
        '         -1: reset
        '         val & 0xff: position between 0 and N-1, where N is the number of filter slots
        '         (val >> 8) & 0x1: direction, 0 => clockwise spinning, 1 => auto direction spinning
        '     get:
        '         -1: in motion
        '         val: position arrived
        OPTION_FILTERWHEEL_POSITION = &H49
        ' [RW] auto exposure percent to average:
        '         1~99: top percent average
        '         0 or 100: full roi average, means "disabled"
        OPTION_AUTOEXPOSURE_PERCENT = &H4A
        OPTION_ANTI_SHUTTER_EFFECT = &H4B          ' [RW] anti shutter effect: 1 => enable, 0 => disable; default: 0
        ' [RO] get chamber humidity & temperature:
        '         high 16 bits: humidity, in 0.1%, such as: 325 means humidity is 32.5%
        '         low 16 bits: temperature, in 0.1 degrees Celsius, such as: 32 means 3.2 degrees Celsius
        OPTION_CHAMBER_HT = &H4C
        OPTION_ENV_HT = &H4D                       ' [RO] get environment humidity & temperature
        OPTION_EXPOSURE_PRE_DELAY = &H4E           ' [RW] exposure signal pre-delay, microsecond
        OPTION_EXPOSURE_POST_DELAY = &H4F          ' [RW] exposure signal post-delay, microsecond
        OPTION_AUTOEXPO_CONV = &H50                ' [RO] get auto exposure convergence status: 1(YES) or 0(NO), -1(PENDING)
        OPTION_AUTOEXPO_TRIGGER = &H51             ' [RW] auto exposure on trigger mode: 0 => disable, 1 => enable; default: 0
        OPTION_LINE_PRE_DELAY = &H52               ' [RW] specified line signal pre-delay, microsecond
        OPTION_LINE_POST_DELAY = &H53              ' [RW] specified line signal post-delay, microsecond
        ' [RO] get the tec maximum voltage range:
        '         high 16 bits: max
        '         low 16 bits: min
        OPTION_TEC_VOLTAGE_MAX_RANGE = &H54
        OPTION_HIGH_FULLWELL = &H55                ' [RW] high fullwell capacity: 0 => disable, 1 => enable
        ' [RW] dynamic defect pixel correction:
        '         dead pixel ratio, t1: (high 16 bits): [0, 100], means: [0.0, 1.0]
        '         hot pixel ratio, t2: (low 16 bits): [0, 100], means: [0.0, 1.0]
        OPTION_DYNAMIC_DEFECT = &H56
        ' [RW] HDR synthesize
        '         K (high 16 bits): [1, 25500]
        '         B (low 16 bits): [0, 65535]
        '         0xffffffff => set to default
        OPTION_HDR_KB = &H57
        ' [RW] HDR synthesize
        '         threshold: [1, 4094]
        '         0xffffffff => set to default
        OPTION_HDR_THRESHOLD = &H58
        ' [RW] For GigE cameras, the application periodically sends heartbeat signals to the camera to keep the connection to the camera alive.
        ' If the camera doesn't receive heartbeat signals within the time period specified by the heartbeat timeout counter, the camera resets the connection.
        ' When the application is stopped by the debugger, the application cannot send the heartbeat signals
        '         0 => auto: when the camera is opened, enable if no debugger is present or disable if debugger is present
        '         1 => enable
        '         2 => disable
        '         default: auto
        OPTION_GIGETIMEOUT = &H5A
        OPTION_EEPROM_SIZE = &H5B                  ' [RO] get EEPROM size
        OPTION_OVERCLOCK_MAX = &H5C                ' [RO] get overclock range: [0, max]
        OPTION_OVERCLOCK = &H5D                    ' [RW] overclock, default: 0
        OPTION_RESET_SENSOR = &H5E                 ' [WO] reset sensor
        OPTION_ISP = &H5F                          ' [RW] Enable hardware ISP: 0 => auto (disable in RAW mode, otherwise enable), 1 => enable, -1 => disable; default: 0
        OPTION_AUTOEXP_EXPOTIME_DAMP = &H60        ' [RW] Auto exposure damping coefficient: time (thousandths). The larger the damping coefficient, the smoother and slower the exposure time changes
        OPTION_AUTOEXP_GAIN_DAMP = &H61            ' [RW] Auto exposure damping coefficient: gain (thousandths). The larger the damping coefficient, the smoother and slower the gain changes
        OPTION_MOTOR_NUMBER = &H62                 ' [RW] range: [1, 20]
        OPTION_MOTOR_POS = &H10000000              ' [RW] range: [1, 702]
        OPTION_PSEUDO_COLOR_START = &H63           ' [RW] Pseudo: start color, BGR format
        OPTION_PSEUDO_COLOR_END = &H64             ' [RW] Pseudo: end color, BGR format
        ' [RW] Pseudo: -1 => custom: use startcolor & endcolor to generate the colormap
        '        0 => disable
        '        1 => spot
        '        2 => spring
        '        3 => summer
        '        4 => autumn
        '        5 => winter
        '        6 => bone
        '        7 => jet
        '        8 => rainbow
        '        9 => deepgreen
        '        10 => ocean
        '        11 => cool
        '        12 => hsv
        '        13 => pink
        '        14 => hot
        '        15 => parula
        '        16 => magma
        '        17 => inferno
        '        18 => plasma
        '        19 => viridis
        '        20 => cividis
        '        21 => twilight
        '        22 => twilight_shifted
        '        23 => turbo
        '        24 => red
        '        25 => green
        '        26 => blue
        OPTION_PSEUDO_COLOR_ENABLE = &H65
        OPTION_LOW_POWERCONSUMPTION = &H66         ' [RW] Low Power Consumption: 0 => disable, 1 => enable
        ' [RW] Fix Pattern Noise Correction
        '        set:
        '             0: disable
        '            1: enable
        '           -1: reset
        '            (0xff000000 | n): set the average number to n, [1~255]
        '        get:
        '            (val & 0xff): 0 => disable, 1 => enable, 2 => inited
        '            ((val & 0xff00) >> 8): sequence
        '            ((val & 0xff0000) >> 16): average number
        OPTION_FPNC = &H67
        ' [RW] Auto exposure over exposure policy: when overexposed,
        '       0 => directly reduce the exposure time/gain to the minimum value; or
        '       1 => reduce exposure time/gain in proportion to current and target brightness.
        '       n(n>1) => first adjust the exposure time to (maximum automatic exposure time * maximum automatic exposure gain) * n / 1000, and then adjust according to the strategy of 1
        ' The advantage of policy 0 is that the convergence speed is faster, but there is black screen.
        ' Policy 1 avoids the black screen, but the convergence speed is slower.
        ' Default: 0
        OPTION_OVEREXP_POLICY = &H68
        ' [RW] Readout mode: 0 = IWR (Integrate While Read), 1 = ITR (Integrate Then Read)
        ' The working modes of the detector readout circuit can be divided into two types: ITR and IWR. Using the IWR readout mode can greatly increase the frame rate. In the ITR mode, the integration of the (n+1)th frame starts after all the data of the nth frame are read out, while in the IWR mode, the data of the nth frame is read out at the same time when the (n+1)th frame is integrated
        OPTION_READOUT_MODE = &H69
        ' [RW] Turn on/off tail Led light: 0 => off, 1 => on; default: on
        OPTION_TAILLIGHT = &H6A
        ' [RW] Load/Save lens state to EEPROM: 0 => load, 1 => save
        OPTION_LENSSTATE = &H6B
        ' [RW] Auto White Balance: continuous mode
        '       0:  disable (default)
        '       n>0: every n millisecond(s)
        '       n<0: every -n frame
        OPTION_AWB_CONTINUOUS = &H6C
        ' [RO] TEC target range: min(low 16 bits) = (short)(val & 0xffff), max(high 16 bits) = (short)((val >> 16) & 0xffff)
        OPTION_TECTARGET_RANGE = &H6D
        ' [RW] Correlated Double Sampling: 0~100
        OPTION_CDS = &H6E
        ' [RW] Low Power Consumption: Enable if exposure time is greater than the set value
        OPTION_LOW_POWER_EXPOTIME = &H6F
        ' [RW] Sensor output offset to zero: 0 => disable, 1 => eanble; default: 0
        OPTION_ZERO_OFFSET = &H70
        ' [RW] GVCP Timeout: millisecond, range = [3, 75], default: 15
        '   Unless in very special circumstances, generally no modification is required, just use the default value
        OPTION_GVCP_TIMEOUT = &H71
        ' [RW] GVCP Retry: range = [2, 8], default: 4
        '   Unless in very special circumstances, generally no modification is required, just use the default value
        OPTION_GVCP_RETRY = &H72
        ' [RW] GVSP wait percent: range = [0, 100], default = (trigger mode: 100, realtime: 0, other: 1)
        OPTION_GVSP_WAIT_PERCENT = &H73
        ' [WO] Reset to 0: 1 => seq; 2 => timestamp; 3 => both
        RESET_SEQ_TIMESTAMP = &H74
        ' [RW] Trigger cancel mode: 0 => no frame, 1 => output frame; default: 0
        OPTION_TRIGGER_CANCEL_MODE = &H75
        ' [RW] Mechanical shutter: 0 => open, 1 => close; default: 0
        OPTION_MECHANICALSHUTTER = &H76
        ' [RO] Line-time of sensor in nanosecond
        OPTION_LINE_TIME = &H77
        ' [RW] Zero padding: 0 => high, 1 => low; default: 0
        OPTION_ZERO_PADDING = &H78
        ' [RO] device uptime in millisecond
        OPTION_UPTIME = &H79
        ' [RW] Bit range: [0, 8]
        OPTION_BITRANGE = &H7A
        ' [RW] Mode of seq & timestamp: 0 => reset to 0 automatically; 1 => never reset automatically; default: 0
        OPTION_MODE_SEQ_TIMESTAMP = &H7B
        ' [RW] Timed trigger number
        OPTION_TIMED_TRIGGER_NUM = &H7C
        ' [RW] Timed trigger: lower 32 bits of 64-bit integer, nanosecond since epoch (00:00:00 UTC on Thursday, 1 January 1970, see https://en.wikipedia.org/wiki/Unix_time)
        OPTION_TIMED_TRIGGER_LOW = &H20000000
        ' [RW] Timed trigger: high 32 bits. The lower 32 bits must be set first, followed by the higher 32 bits
        OPTION_TIMED_TRIGGER_HIGH = &H40000000
        ' [RW] trigger threshold of auto exposure
        OPTION_AUTOEXP_THLD_TRIGGER = &H7D
        OPTION_LANE = &H7E              ' [RW]
        ' [RW] Voltage bias
        OPTION_VOLTAGEBIAS = &H7f
        ' [RO] Voltage bias range: min (low 16bits), max (high 16bits)
        OPTION_VOLTAGEBIAS_RANGE = &H80
        OPTION_READ_TIME = &H81         ' [RO]
        OPTION_ALL_EXPO_TIME = &H82     ' [RO]
        OPTION_CAMERA_PERIOD = &H83     ' [RO]
        ' [RO] get the number of frontend deque full
        OPTION_FRONTEND_FULL = &H84
        ' [RO] get the number of backend deque full
        OPTION_BACKEND_FULL = &H85
        ' [RO] gps status: 0 => not supported; -1 => gps device offline; 1 => gps device online
        TOUPCAM_OPTION_GPS = &H86
        ' [RW] Line length in pixel clock
        OPTION_LINE_LENGTH = &H87
        ' [RW] Scan direction: 0(forward), 1(reverse), 2(alternate)
        OPTION_SCAN_DIRECTION = &H88
        ' [RW] Black level automatic adjustment function: 0: off, 1: on
        '  This setting turn on/off black level auto adjust function by OB(Optical Black) level.
        '  In case of long exposure and so on, OB level is offset by leak or any other reason.
        '  Because of it, if the adjustment becomes a problem, this setting is introduced for one of the solution.
        OPTION_BLACKLEVEL_AUTOADJUST = &H89
        ' [RW] digital gain
        OPTION_DIGITAL_GAIN = &H1001
    End Enum

    ' HRESULT: Error code
    Public Const S_OK = &H0                                     ' Success
    Public Const S_FALSE = &H1                                  ' Yet another success ' Remark: Different from S_OK, such as internal values and user-set values have coincided, equivalent to noop
    Public Const E_UNEXPECTED = CType(&H8000FFFF, Integer)      ' Catastrophic failure ' Remark: Generally indicates that the conditions are not met, such as calling put_Option setting some options that do not support modification when the camera is running, and so on
    Public Const E_NOTIMPL = CType(&H80004001, Integer)         ' Not supported or not implemented ' Remark: This feature is not supported on this model of camera
    Public Const E_NOINTERFACE = CType(&H80004002, Integer)
    Public Const E_ACCESSDENIED = CType(&H80070005, Integer)    ' Permission denied ' Remark: The program on Linux does not have permission to open the USB device, please enable udev rules file or run as root
    Public Const E_OUTOFMEMORY = CType(&H8007000E, Integer)     ' Out of memory
    Public Const E_INVALIDARG = CType(&H80070057, Integer)      ' One or more arguments are not valid
    Public Const E_POINTER = CType(&H80004003, Integer)         ' Pointer that is not valid ' Remark: Pointer is NULL
    Public Const E_FAIL = CType(&H80004005, Integer)            ' Generic failure
    Public Const E_WRONG_THREAD = CType(&H8001010E, Integer)    ' Call function in the wrong thread
    Public Const E_GEN_FAILURE = CType(&H8007001F, Integer)     ' Device not functioning ' Remark: It is generally caused by hardware errors, such as cable problems, USB port problems, poor contact, camera hardware damage, etc
    Public Const E_BUSY = CType(&H800700AA, Integer)            ' The requested resource is in use ' Remark: The camera is already in use, such as duplicated opening/starting the camera, or being used by other application, etc
    Public Const E_PENDING = CType(&H8000000A, Integer)         ' The data necessary to complete this operation is not yet available ' Remark: No data is available at this time
    Public Const E_TIMEOUT = CType(&H8001011F, Integer)         ' This operation returned because the timeout period expired
    Public Const E_UNREACH = CType(&H80072743, Integer)         ' Network is unreachable

    Public Const EXPOGAIN_DEF = 100      ' exposure gain, default value
    Public Const EXPOGAIN_MIN = 100      ' exposure gain, minimum value
    Public Const TEMP_DEF = 6503     ' color temperature, default value
    Public Const TEMP_MIN = 2000     ' color temperature, minimum value
    Public Const TEMP_MAX = 15000    ' color temperature, maximum value
    Public Const TINT_DEF = 1000     ' tint
    Public Const TINT_MIN = 200      ' tint
    Public Const TINT_MAX = 2500     ' tint
    Public Const HUE_DEF = 0        ' hue
    Public Const HUE_MIN = -180     ' hue
    Public Const HUE_MAX = 180      ' hue
    Public Const SATURATION_DEF = 128      ' saturation
    Public Const SATURATION_MIN = 0        ' saturation
    Public Const SATURATION_MAX = 255      ' saturation
    Public Const BRIGHTNESS_DEF = 0        ' brightness
    Public Const BRIGHTNESS_MIN = -255     ' brightness
    Public Const BRIGHTNESS_MAX = 255      ' brightness
    Public Const CONTRAST_DEF = 0        ' contrast
    Public Const CONTRAST_MIN = -255     ' contrast
    Public Const CONTRAST_MAX = 255      ' contrast
    Public Const GAMMA_DEF = 100      ' gamma
    Public Const GAMMA_MIN = 20       ' gamma
    Public Const GAMMA_MAX = 180      ' gamma
    Public Const AETARGET_DEF = 120      ' target Of auto exposure
    Public Const AETARGET_MIN = 16       ' target Of auto exposure
    Public Const AETARGET_MAX = 220      ' target Of auto exposure
    Public Const WBGAIN_DEF = 0        ' white balance gain
    Public Const WBGAIN_MIN = -127     ' white balance gain
    Public Const WBGAIN_MAX = 127      ' white balance gain
    Public Const BLACKLEVEL_MIN = 0        ' minimum black level
    Public Const BLACKLEVEL8_MAX = 31       ' maximum black level For bitdepth = 8
    Public Const BLACKLEVEL10_MAX = 31 * 4   ' maximum black level For bitdepth = 10
    Public Const BLACKLEVEL11_MAX = 31 * 8   ' maximum black level for bitdepth = 11
    Public Const BLACKLEVEL12_MAX = 31 * 16  ' maximum black level For bitdepth = 12
    Public Const BLACKLEVEL14_MAX = 31 * 64  ' maximum black level For bitdepth = 14
    Public Const BLACKLEVEL16_MAX = 31 * 256 ' maximum black level For bitdepth = 16
    Public Const SHARPENING_STRENGTH_DEF = 0        ' sharpening strength
    Public Const SHARPENING_STRENGTH_MIN = 0        ' sharpening strength
    Public Const SHARPENING_STRENGTH_MAX = 500      ' sharpening strength
    Public Const SHARPENING_RADIUS_DEF = 2        ' sharpening radius
    Public Const SHARPENING_RADIUS_MIN = 1        ' sharpening radius
    Public Const SHARPENING_RADIUS_MAX = 10       ' sharpening radius
    Public Const SHARPENING_THRESHOLD_DEF = 0        ' sharpening threshold
    Public Const SHARPENING_THRESHOLD_MIN = 0        ' sharpening threshold
    Public Const SHARPENING_THRESHOLD_MAX = 255      ' sharpening threshold
    Public Const AUTOEXPO_THRESHOLD_DEF = 5        ' auto exposure threshold
    Public Const AUTOEXPO_THRESHOLD_MIN = 2        ' auto exposure threshold
    Public Const AUTOEXPO_THRESHOLD_MAX = 15       ' auto exposure threshold
    Public Const AUTOEXPO_THLD_TRIGGER_DEF = 5     ' auto exposure trigger threshold
    Public Const AUTOEXPO_THLD_TRIGGER_MIN = 2     ' auto exposure trigger threshold
    Public Const AUTOEXPO_THLD_TRIGGER_MAX = 64    ' auto exposure trigger threshold
    Public Const AUTOEXPO_DAMP_DEF = 0        ' auto exposure damping coefficient: thousandths
    Public Const AUTOEXPO_DAMP_MIN = 0        ' auto exposure damping coefficient: thousandths
    Public Const AUTOEXPO_DAMP_MAX = 1000     ' auto exposure damping coefficient: thousandths
    Public Const BANDWIDTH_DEF = 100      ' bandwidth
    Public Const BANDWIDTH_MIN = 1        ' bandwidth
    Public Const BANDWIDTH_MAX = 100      ' bandwidth
    Public Const DENOISE_DEF = 0        ' denoise
    Public Const DENOISE_MIN = 0        ' denoise
    Public Const DENOISE_MAX = 100      ' denoise
    Public Const HEARTBEAT_MIN = 100      ' millisecond
    Public Const HEARTBEAT_MAX = 10000    ' millisecond
    Public Const AE_PERCENT_MIN = 0        ' auto exposure percent; 0 or 100 => full roi average, means "disabled"
    Public Const AE_PERCENT_MAX = 100
    Public Const AE_PERCENT_DEF = 1        ' auto exposure percent: enabled, percentage = 1%
    Public Const NOPACKET_TIMEOUT_MIN = 500      ' no packet timeout minimum: 500ms
    Public Const NOFRAME_TIMEOUT_MIN = 500       ' no frame timeout minimum: 500ms
    Public Const DYNAMIC_DEFECT_T1_MIN = 0       ' dynamic defect pixel correction, dead pixel ratio: the smaller the dead ratio is, the more stringent the conditions for processing dead pixels are, and fewer pixels will be processed
    Public Const DYNAMIC_DEFECT_T1_MAX = 100     ' means: 1.0
    Public Const DYNAMIC_DEFECT_T1_DEF = 90      ' means: 0.9
    Public Const DYNAMIC_DEFECT_T2_MIN = 0       ' dynamic defect pixel correction, hot pixel ratio: the smaller the hot ratio is, the more stringent the conditions for processing hot pixels are, and fewer pixels will be processed
    Public Const DYNAMIC_DEFECT_T2_MAX = 100     '
    Public Const DYNAMIC_DEFECT_T2_DEF = 90
    Public Const HDR_K_MIN = 1        ' HDR synthesize
    Public Const HDR_K_MAX = 25500
    Public Const HDR_B_MIN = 0
    Public Const HDR_B_MAX = 65535
    Public Const HDR_THRESHOLD_MIN = 0
    Public Const HDR_THRESHOLD_MAX = 4094
    Public Const CDS_MIN = 0 'Correlated Double Sampling
    Public Const CDS_MAX = 100

    Public Enum ePIXELFORMAT As Integer
        PIXELFORMAT_RAW8 = &H0
        PIXELFORMAT_RAW10 = &H1
        PIXELFORMAT_RAW12 = &H2
        PIXELFORMAT_RAW14 = &H3
        PIXELFORMAT_RAW16 = &H4
        PIXELFORMAT_YUV411 = &H5
        PIXELFORMAT_VUYY = &H6
        PIXELFORMAT_YUV444 = &H7
        PIXELFORMAT_RGB888 = &H8
        PIXELFORMAT_GMCY8 = &H9      ' map to RGGB 8 bits
        PIXELFORMAT_GMCY12 = &HA     ' map to RGGB 12 bits
        PIXELFORMAT_UYVY = &HB
        PIXELFORMAT_RAW12PACK = &HC
        PIXELFORMAT_RAW11 = &HD
        PIXELFORMAT_HDR8HL = &HE     ' HDR, Bitdepth: 8, Conversion Gain: High + Low
        PIXELFORMAT_HDR10HL = &HF    ' HDR, Bitdepth: 10, Conversion Gain: High + Low
        PIXELFORMAT_HDR11HL = &H10   ' HDR, Bitdepth: 11, Conversion Gain: High + Low
        PIXELFORMAT_HDR12HL = &H11   ' HDR, Bitdepth: 12, Conversion Gain: High + Low
        PIXELFORMAT_HDR14HL = &H12   ' HDR, Bitdepth: 14, Conversion Gain: High + Low
        PIXELFORMAT_RAW10PACK = &H13
    End Enum

    <Flags>
    Public Enum eFRAMEINFO_FLAG As Integer
        FRAMEINFO_FLAG_SEQ = &H1                      ' frame sequence number
        FRAMEINFO_FLAG_TIMESTAMP = &H2                ' timestamp
        FRAMEINFO_FLAG_EXPOTIME = &H4                 ' exposure time
        FRAMEINFO_FLAG_EXPOGAIN = &H8                 ' exposure gain
        FRAMEINFO_FLAG_BLACKLEVEL = &H10              ' black level
        FRAMEINFO_FLAG_SHUTTERSEQ = &H20              ' sequence shutter counter
        FRAMEINFO_FLAG_GPS = &H40                     ' GPS
        FRAMEINFO_FLAG_AUTOFOCUS = &H80               ' auto focus: uLum & uFV
        FRAMEINFO_FLAG_COUNT = &H100                  ' timecount, framecount, tricount
        FRAMEINFO_FLAG_MECHANICALSHUTTER = &H200      ' Mechanical shutter: closed
        FRAMEINFO_FLAG_STILL = &H8000                 ' still image
        FRAMEINFO_FLAG_CG = &H10000                   ' Conversion Gain: High
    End Enum

    Public Enum eIoControType As Integer
        IOCONTROLTYPE_GET_SUPPORTEDMODE = &H1          ' 1 => Input, 2 => Output, (1 | 2) => support both Input and Output
        IOCONTROLTYPE_GET_GPIODIR = &H3                ' 0x00 => Input, 0x01 => Output
        IOCONTROLTYPE_SET_GPIODIR = &H4
        ' 0 => not connected
        ' 1 => Tri-state: Tri-state mode (Not driven)
        ' 2 => TTL: TTL level signals
        ' 3 => LVDS: LVDS level signals
        ' 4 => RS422: RS422 level signals
        ' 5 => Opto-coupled
        IOCONTROLTYPE_GET_FORMAT = &H5
        IOCONTROLTYPE_SET_FORMAT = &H6
        IOCONTROLTYPE_GET_OUTPUTINVERTER = &H7         ' boolean, only support output signal
        IOCONTROLTYPE_SET_OUTPUTINVERTER = &H8
        IOCONTROLTYPE_GET_INPUTACTIVATION = &H9        ' 0x00 => Rising edge, 0x01 => Falling edge, 0x02 => Level high, 0x03 => Level low
        IOCONTROLTYPE_SET_INPUTACTIVATION = &HA
        IOCONTROLTYPE_GET_DEBOUNCERTIME = &HB          ' debouncer time in microseconds, range: [0, 20000]
        IOCONTROLTYPE_SET_DEBOUNCERTIME = &HC
        ' 0 => Opto-isolated input
        ' 1 => GPIO0
        ' 2 => GPIO1
        ' 3 => Counter
        ' 4 => PWM
        ' 5 => Software
        IOCONTROLTYPE_GET_TRIGGERSOURCE = &HD
        IOCONTROLTYPE_SET_TRIGGERSOURCE = &HE
        IOCONTROLTYPE_GET_TRIGGERDELAY = &HF           ' Trigger delay time in microseconds, range: [0, 5000000]
        IOCONTROLTYPE_SET_TRIGGERDELAY = &H10
        IOCONTROLTYPE_GET_BURSTCOUNTER = &H11          ' Burst Counter, range: [1 ~ 65535]
        IOCONTROLTYPE_SET_BURSTCOUNTER = &H12
        IOCONTROLTYPE_GET_COUNTERSOURCE = &H13         ' 0 => Opto-isolated input, 1 => GPIO0, 2=> GPIO1
        IOCONTROLTYPE_SET_COUNTERSOURCE = &H14
        IOCONTROLTYPE_GET_COUNTERVALUE = &H15          ' Counter Value, range: [1 ~ 65535]
        IOCONTROLTYPE_SET_COUNTERVALUE = &H16
        IOCONTROLTYPE_SET_RESETCOUNTER = &H18
        IOCONTROLTYPE_GET_PWM_FREQ = &H19              ' PWM Frequency
        IOCONTROLTYPE_SET_PWM_FREQ = &H1A
        IOCONTROLTYPE_GET_PWM_DUTYRATIO = &H1B         ' PWM Duty Ratio
        IOCONTROLTYPE_SET_PWM_DUTYRATIO = &H1C
        IOCONTROLTYPE_GET_PWMSOURCE = &H1D             ' PWM Source: 0 => Opto-isolated input, 0x01 => GPIO0, 0x02 => GPIO1
        IOCONTROLTYPE_SET_PWMSOURCE = &H1E
        ' 0 => Frame Trigger Wait
        ' 1 => Exposure Active
        ' 2 => Strobe
        ' 3 => User output
        ' 4 => Counter Output
        ' 5 => Timer Output
        IOCONTROLTYPE_GET_OUTPUTMODE = &H1F
        IOCONTROLTYPE_SET_OUTPUTMODE = &H20
        IOCONTROLTYPE_GET_STROBEDELAYMODE = &H21       ' boolean, 1 => delay, 0 => pre-delay; compared to exposure active signal
        IOCONTROLTYPE_SET_STROBEDELAYMODE = &H22
        IOCONTROLTYPE_GET_STROBEDELAYTIME = &H23       ' Strobe delay or pre-delay time in microseconds, range: [0, 5000000]
        IOCONTROLTYPE_SET_STROBEDELAYTIME = &H24
        IOCONTROLTYPE_GET_STROBEDURATION = &H25        ' Strobe duration time in microseconds, range: [0, 5000000]
        IOCONTROLTYPE_SET_STROBEDURATION = &H26
        ' bit0 => Opto-isolated output
        ' bit1 => GPIO0 output
        ' bit2 => GPIO1 output
        IOCONTROLTYPE_GET_USERVALUE = &H27
        IOCONTROLTYPE_SET_USERVALUE = &H28
        IOCONTROLTYPE_GET_UART_ENABLE = &H29           ' enable: 1 => on; 0 => off
        IOCONTROLTYPE_SET_UART_ENABLE = &H2A
        IOCONTROLTYPE_GET_UART_BAUDRATE = &H2B         ' baud rate: 0 => 9600; 1 => 19200; 2 => 38400; 3 => 57600; 4 => 115200
        IOCONTROLTYPE_SET_UART_BAUDRATE = &H2C
        IOCONTROLTYPE_GET_UART_LINEMODE = &H2D         ' line mode: 0 => TX(GPIO_0)/RX(GPIO_1); 1 => TX(GPIO_1)/RX(GPIO_0)
        IOCONTROLTYPE_SET_UART_LINEMODE = &H2E
        IOCONTROLTYPE_GET_EXPO_ACTIVE_MODE = &H2F      ' exposure time signal: 0 => specified line, 1 => common exposure time
        IOCONTROLTYPE_SET_EXPO_ACTIVE_MODE = &H30
        IOCONTROLTYPE_GET_EXPO_START_LINE = &H31       ' exposure start line, default: 0
        IOCONTROLTYPE_SET_EXPO_START_LINE = &H32
        ' exposure end line, default: 0
        ' end line must be no less than start line
        IOCONTROLTYPE_GET_EXPO_END_LINE = &H33
        IOCONTROLTYPE_SET_EXPO_END_LINE = &H34
        ' exposure event: 0 => specified line, 1 => common exposure time
        IOCONTROLTYPE_GET_EXEVT_ACTIVE_MODE = &H35
        IOCONTROLTYPE_SET_EXEVT_ACTIVE_MODE = &H36
        ' Output Counter Value, range: [0 ~ 65535]
        IOCONTROLTYPE_GET_OUTPUTCOUNTERVALUE = &H37
        IOCONTROLTYPE_SET_OUTPUTCOUNTERVALUE = &H38
        ' Output pause: 1 => puase, 0 => unpause
        IOCONTROLTYPE_SET_OUTPUT_PAUSE = &H3A
        ' Input state: 0 (low level) or 1 (high level)
        IOCONTROLTYPE_GET_INPUT_STATE = &H3B
        ' User pulse high level time: us
        IOCONTROLTYPE_GET_USER_PULSE_HIGH = &H3D
        IOCONTROLTYPE_SET_USER_PULSE_HIGH = &H3E
        ' User pulse low level time: us
        IOCONTROLTYPE_GET_USER_PULSE_LOW = &H3F
        IOCONTROLTYPE_SET_USER_PULSE_LOW = &H40
        ' User pulse number: default 0
        IOCONTROLTYPE_GET_USER_PULSE_NUMBER = &H41
        IOCONTROLTYPE_SET_USER_PULSE_NUMBER = &H42
        ' External trigger number
        IOCONTROLTYPE_GET_EXTERNAL_TRIGGER_NUMBER = &H43
        ' Trigger signal number after debounce
        IOCONTROLTYPE_GET_DEBOUNCER_TRIGGER_NUMBER = &H45
        ' Effective trigger signal number
        IOCONTROLTYPE_GET_EFFECTIVE_TRIGGER_NUMBER = &H47
    End Enum

    Public Const IOCONTROL_DELAYTIME_MAX = 5 * 1000 * 1000

    ' AAF: Astro Auto Focuser
    Public Enum eAAF As Integer
        AAF_SETPOSITION = &H1
        AAF_GETPOSITION = &H2
        AAF_SETZERO = &H3
        AAF_SETDIRECTION = &H5
        AAF_GETDIRECTION = &H6
        AAF_SETMAXINCREMENT = &H7
        AAF_GETMAXINCREMENT = &H8
        AAF_SETFINE = &H9
        AAF_GETFINE = &HA
        AAF_SETCOARSE = &HB
        AAF_GETCOARSE = &HC
        AAF_SETBUZZER = &HD
        AAF_GETBUZZER = &HE
        AAF_SETBACKLASH = &HF
        AAF_GETBACKLASH = &H10
        AAF_GETAMBIENTTEMP = &H12
        AAF_GETTEMP = &H14         ' in 0.1 degrees Celsius, such as: 32 means 3.2 degrees Celsius
        AAF_ISMOVING = &H16
        AAF_HALT = &H17
        AAF_SETMAXSTEP = &H1B
        AAF_GETMAXSTEP = &H1C
        AAF_GETSTEPSIZE = &H1E
        AAF_RANGEMIN = &HFD        ' Range: min value
        AAF_RANGEMAX = &HFE        ' Range: max value
        AAF_RANGEDEF = &HFF        ' Range: default value
    End Enum

    ' hardware level range mode
    Public Enum eLevelRange As UShort
        LEVELRANGE_MANUAL = &H0     ' manual
        LEVELRANGE_ONCE = &H1       ' once
        LEVELRANGE_CONTINUE = &H2   ' continue
        LEVELRANGE_ROI = &HFFFF     ' update roi rect only
    End Enum

    Public Structure Resolution
        Public width As UInteger
        Public height As UInteger
    End Structure
    Public Structure ModelV2
        Public name As String           ' model name
        Public flag As Long             ' FLAG_xxx, 64 bits
        Public maxspeed As UInteger     ' number of speed level, same as Toupcam_get_MaxSpeed(), the speed range = [0, maxspeed], closed interval
        Public preview As UInteger      ' number of preview resolution, same as Toupcam_get_ResolutionNumber()
        Public still As UInteger        ' number of still resolution, same as get_StillResolutionNumber()
        Public maxfanspeed As UInteger  ' maximum fan speed, fan speed range = [0, max], closed interval
        Public ioctrol As UInteger      ' number of input/output control
        Public xpixsz As Single         ' physical pixel size in micrometer
        Public ypixsz As Single         ' physical pixel size in micrometer
        Public res As Resolution()
    End Structure
    Public Structure DeviceV2
        Public displayname As String    ' display name
        Public id As String             ' unique and opaque id of a connected camera
        Public model As ModelV2
    End Structure
    <StructLayout(LayoutKind.Sequential)>
    Public Structure FrameInfoV3
        Public width As UInteger
        Public height As UInteger
        Public flag As UInteger         ' FRAMEINFO_FLAG_xxxx
        Public seq As UInteger          ' frame sequence number
        Public timestamp As ULong       ' microsecond
        Public shutterseq As UInteger   ' sequence shutter counter
        Public expotime As UInteger     ' expotime
        Public expogain As UShort       ' expogain
        Public blacklevel As UShort     ' black level
    End Structure
    <StructLayout(LayoutKind.Sequential)>
    Public Structure Gps
        Public utcstart As ULong        ' exposure start time: nanosecond since epoch (00:00:00 UTC on Thursday, 1 January 1970, see https://en.wikipedia.org/wiki/Unix_time)
        Public utcend As ULong          ' exposure end time
        Public longitude As Integer     ' millionth of a degree, 0.000001 degree
        Public latitudee As Integer
        Public altitudee As Integer     ' millimeter
        Public satellite As UShort      ' number of satellite
        Public reserved As UShort       ' not used
    End Structure
    <StructLayout(LayoutKind.Sequential)>
    Public Structure FrameInfoV4
        Public v3 As FrameInfoV3
        Public reserved As UInteger     ' not used
        Public uLum As UInteger
        Public uFV As ULong
        Public timecount As ULong
        Public framecount As UInteger
        Public tricount As UInteger
        Public gps As Gps
    End Structure
    <StructLayout(LayoutKind.Sequential), Obsolete("Use FrameInfoV4")>
    Public Structure FrameInfoV2
        Public width As UInteger
        Public height As UInteger
        Public flag As UInteger         ' FRAMEINFO_FLAG_xxxx
        Public seq As UInteger          ' frame sequence number
        Public timestamp As ULong       ' microsecond
    End Structure
    <StructLayout(LayoutKind.Sequential)>
    Public Structure SelfTrigger
        Public sensingLeft, sensingTop, sensingWidth, sensingHeight As UInteger ' Sensing Area
        Public hThreshold, lThreshold As UInteger   ' threshold High side, threshold Low side
        Public expoTime As UInteger                 ' Exposure Time
        Public expoGain As UShort                   ' Exposure Gain
        Public hCount, lCount As UShort             ' Count threshold High side, Count threshold Low side, thousandths Of Sensing Area
        Public reserved As UShort
    End Structure
    Public Structure LensInfo
        Public lensID As UShort
        Public lensType As Byte
        Public statusAfmf As Byte        ' LENS_AF = 0x00,  LENS_MF = 0x80

        Public maxFocalLength As UShort
        Public curFocalLength As UShort
        Public minFocalLength As UShort

        Public farFM As Short            ' focus motor, absolute value
        Public curFM As Short            ' current focus motor
        Public nearFM As Short

        Public maxFocusDistance As UShort
        Public minFocusDistance As UShort

        Public curAM As SByte
        Public maxAM As Byte             ' maximum Aperture, mimimum F#
        Public minAM As Byte             ' mimimum Aperture, maximum F#
        Public posAM As Byte             ' used For Set aperture motor To posAM, it Is an index
        Public posFM As Integer          ' used For Set focus motor To posFM

        Public arrayFN As String()
        Public lensName As String
    End Structure
    Public Enum eAFMode As UInteger
        AFMode_CALIBRATE = &H0  ' lens calibration mode
        AFMode_MANUAL = &H1     ' manual focus mode
        AFMode_ONCE = &H2       ' onepush focus mode
        AFMode_AUTO = &H3       ' autofocus mode
        AFMode_NONE = &H4       ' no active selection Of focus mode
        AFMode_IDLE = &H5
    End Enum
    Public Enum eAFStatus As UInteger
        AFStatus_NA = &H0           ' Not available
        AFStatus_PEAKPOINT = &H1    ' Focus completed, find the focus position
        AFStatus_DEFOCUS = &H2      ' End Of focus, defocus
        AFStatus_NEAR = &H3         ' Focusing ended, Object too close
        AFStatus_FAR = &H4          ' Focusing ended, Object too far
        AFStatus_ROICHANGED = &H5   ' Focusing ends, roi changes
        AFStatus_SCENECHANGED = &H6 ' Focusing ends, scene changes
        AFStatus_MODECHANGED = &H7  ' The End Of focusing And the change In focusing mode Is usually determined by the user moderator
        AFStatus_UNFINISH = &H8     ' The focus Is Not complete. At the beginning Of focusing, it will be Set As incomplete
    End Enum
    <StructLayout(LayoutKind.Sequential)>
    Public Structure AFState
        Public AF_Mode As eAFMode
        Public AF_Status As eAFStatus
        Public AF_LensAP_Update_Flag As Byte  ' mark for whether the lens aperture is calibrated
        Public AF_LensManual_Flag As Byte     ' if true, allows manual operation
        Public Reserved0 As Byte
        Public Reserved1 As Byte
    End Structure
    <StructLayout(LayoutKind.Sequential)>
    Public Structure FocusMotor
        Public imax As Integer          ' maximum auto focus sensor board positon
        Public imin As Integer          ' minimum auto focus sensor board positon
        Public idef As Integer          ' conjugate calibration positon
        Public imaxabs As Integer       ' maximum absolute auto focus sensor board positon, micrometer
        Public iminabs As Integer       ' maximum absolute auto focus sensor board positon, micrometer
        Public zoneh As Integer         ' zone horizontal
        Public zonev As Integer         ' zone vertical
    End Structure

#If Not (NETFX_CORE OrElse NETCOREAPP OrElse WINDOWS_UWP) Then
    <DllImport("ntdll.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Cdecl, SetLastError:=False)>
    Public Shared Sub memcpy(Destination As IntPtr, Source As IntPtr, Length As IntPtr)
    End Sub
#End If

    Public Delegate Sub DelegateEventCallback(nEvent As eEVENT)
    Public Delegate Sub DelegateDataCallbackV4(pData As IntPtr, ByRef info As FrameInfoV3, bSnap As Boolean)
    <Obsolete>
    Public Delegate Sub DelegateDataCallbackV3(pData As IntPtr, ByRef info As FrameInfoV2, bSnap As Boolean)
    Public Delegate Sub DelegateHistogramCallback(aHistY As Single(), aHistR As Single(), aHistG As Single(), aHistB As Single())
    Public Delegate Sub DelegateHistogramCallbackV2(aHist As Integer())
    Public Delegate Sub DelegateProgressCallback(percent As Integer)
    Public Delegate Sub DelegateHotplugCallback()

    Public Shared Function TDIBWIDTHBYTES(bits As Integer) As Integer
        Return ((bits + 31) And (Not 31)) / 8
    End Function

    Public Sub Close()
        Dispose()
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        ' Follow the Dispose pattern - public nonvirtual
        Dispose(True)
        map_.Remove(id_.ToInt32())
        GC.SuppressFinalize(Me)
    End Sub

    ' get the version of this dll, which is: 59.29030.20250722
    Public Shared Function Version() As String
        Return Marshal.PtrToStringUni(Toupcam_Version())
    End Function

    ' only for compatibility with .Net 4.0 And below
    Private Shared Function IncIntPtr(p As IntPtr, offset As Integer) As IntPtr
        Return New IntPtr(p.ToInt64() + offset)
    End Function

    Private Shared Function Ptr2Device(p As IntPtr, cnt As UInteger) As DeviceV2()
        Dim ti As IntPtr = p
        Dim arr As DeviceV2() = New DeviceV2(cnt - 1) {}
        If cnt <> 0 Then
            For i As UInteger = 0 To cnt - 1
                arr(i).displayname = Marshal.PtrToStringUni(p)
                p = IncIntPtr(p, 2 * 64)
                arr(i).id = Marshal.PtrToStringUni(p)
                p = IncIntPtr(p, 2 * 64)

                Dim q As IntPtr = Marshal.ReadIntPtr(p)
                p = IncIntPtr(p, IntPtr.Size)
                arr(i).model = toModelV2(q)
            Next
        End If
        Marshal.FreeHGlobal(ti)
        Return arr
    End Function

    ' enumerate Toupcam cameras that are currently connected to computer
    Public Shared Function EnumV2() As DeviceV2()
        Dim p As IntPtr = Marshal.AllocHGlobal(512 * 128)
        Return Ptr2Device(p, Toupcam_EnumV2(p))
    End Function

    Public Shared Function EnumWithName() As DeviceV2()
        Dim p As IntPtr = Marshal.AllocHGlobal(512 * 128)
        Return Ptr2Device(p, Toupcam_EnumWithName(p))
    End Function

    ' Initialize support for GigE cameras. If online/offline notifications are not required, the callback function can be set to null
    Public Shared Function GigeEnable(funHotplug As DelegateHotplugCallback) As Integer
        Dim pHotplug As HOTPLUG_CALLBACK = New HOTPLUG_CALLBACK(AddressOf HotplugCallback)
        Dim id As IntPtr = New IntPtr(Interlocked.Increment(sid_))
        map_.Add(id.ToInt32, funHotplug)
        Return Toupcam_GigeEnable(pHotplug, id)
    End Function
    
    ' Initialize support for PCIe cameras. If online/offline notifications are not required, the callback function can be set to null
    Public Shared Function PciEnable(funHotplug As DelegateHotplugCallback) As Integer
        Dim pHotplug As HOTPLUG_CALLBACK = New HOTPLUG_CALLBACK(AddressOf HotplugCallback)
        Dim id As IntPtr = New IntPtr(Interlocked.Increment(sid_))
        map_.Add(id.ToInt32, funHotplug)
        Return Toupcam_PciEnable(pHotplug, id)
    End Function

    ' Initialize support for *.cti cameras. If online/offline notifications are not required, the callback function can be set to NULL
    ' (1) ctiPath = Nothing means all *.cti in GENICAM_GENTL64_PATH/GENICAM_GENTL32_PATH
    ' or
    ' (2) Dim ctiPath() As String = {
    '       "/full/path/to/file.cti"
    '       ...
    '       Nothing
    '    }
    '
    Public Shared Function CtlEnable(funHotplug As DelegateHotplugCallback, ctiPath() As String) As Integer
        Dim newPath() As String = Nothing
        If ctiPath IsNot Nothing AndAlso ctiPath.Length > 0 AndAlso ctiPath(ctiPath.Length - 1) IsNot Nothing Then
            newPath = New String(ctiPath.Length) {}
            For i As Integer = 0 To ctiPath.Length - 1
                newPath(i) = ctiPath(i)
            Next
        End If
        Dim pHotplug As HOTPLUG_CALLBACK = New HOTPLUG_CALLBACK(AddressOf HotplugCallback)
        Dim id As IntPtr = New IntPtr(Interlocked.Increment(sid_))
        map_.Add(id.ToInt32, funHotplug)
        Return Toupcam_CtiEnable(pHotplug, id, ctiPath)
    End Function

    '
    ' the object of Toupcam must be obtained by static mothod Open or OpenByIndex, it cannot be obtained by obj = New Toupcam (The constructor is private on purpose)
    '
    ' camId: enumerated by EnumV2, Nothing means the first enumerated camera
    ' For USB, GigE or PCIe camera, the camId can also be specified as (case sensitive):
    '    (a) "sn:xxxxxxxxxxxx" (such as sn:ZP250212241204105), or
    '    (b) "name:xxxxxx" (such as name:Camera1)
    ' Moreover, for GigE camera, the camId can also be specified as (case sensitive):
    '    (a) "ip:xxx.xxx.xxx.xxx" (such as ip:192.168.1.100), or
    '    (b) "mac:xxxxxxxxxxxx" (such as mac:d05f64ffff23)
    Public Shared Function Open(camId As String) As Toupcam
        Dim tmphandle As SafeCamHandle = Toupcam_Open(camId)
        If tmphandle Is Nothing OrElse tmphandle.IsInvalid OrElse tmphandle.IsClosed Then
            Return Nothing
        End If
        Return New Toupcam(tmphandle)
    End Function

    '
    ' the object of Toupcam must be obtained by static mothod Open or OpenByIndex, it cannot be obtained by obj = New Toupcam (The constructor is private on purpose)
    '
    ' the same with Open, but use the index as the parameter. such as:
    ' index == 0, open the first camera,
    ' index == 1, open the second camera,
    ' etc
    Public Shared Function OpenByIndex(index As UInteger) As Toupcam
        Dim tmphandle As SafeCamHandle = Toupcam_OpenByIndex(index)
        If tmphandle Is Nothing OrElse tmphandle.IsInvalid OrElse tmphandle.IsClosed Then
            Return Nothing
        End If
        Return New Toupcam(tmphandle)
    End Function

    ' the last HRESULT return code of api call
    Public ReadOnly Property HResult() As Integer
        Get
            Return hResult_
        End Get
    End Property

    Public ReadOnly Property ResolutionNumber() As UInteger
        Get
            If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
                Return 0
            End If
            Return Toupcam_get_ResolutionNumber(handle_)
        End Get
    End Property

    Public ReadOnly Property StillResolutionNumber() As UInteger
        Get
            If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
                Return 0
            End If
            Return Toupcam_get_StillResolutionNumber(handle_)
        End Get
    End Property

    Public ReadOnly Property MonoMode() As Boolean
        Get
            If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
                Return False
            End If
            Return (0 = Toupcam_get_MonoMode(handle_))
        End Get
    End Property

    ' get the maximum speed, see "Frame Speed Level"
    Public ReadOnly Property MaxSpeed() As UInteger
        Get
            If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
                Return 0
            End If
            Return Toupcam_get_MaxSpeed(handle_)
        End Get
    End Property

    ' get the max bitdepth of this camera, such as 8, 10, 12, 14, 16
    Public ReadOnly Property MaxBitDepth() As UInteger
        Get
            If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
                Return 0
            End If
            Return Toupcam_get_MaxBitDepth(handle_)
        End Get
    End Property

    ' get the maximum fan speed, the fan speed range = [0, max], closed interval
    Public ReadOnly Property FanMaxSpeed() As UInteger
        Get
            If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
                Return 0
            End If
            Return Toupcam_get_FanMaxSpeed(handle_)
        End Get
    End Property

    Public ReadOnly Property TecTargetMax As Short
        Get
            If handle_ IsNot Nothing And Not handle_.IsInvalid And Not handle_.IsClosed Then
                Dim range As Integer = 0
                If CheckHResult(Toupcam_get_Option(handle_, eOPTION.OPTION_TECTARGET_RANGE, range)) Then
                    Dim x As Integer = (range >> 16) And &HFFFF
                    Return BitConverter.ToInt16(BitConverter.GetBytes(x), 0)
                End If
            End If
            Return Short.MaxValue
        End Get
    End Property

    Public ReadOnly Property TecTargetMin As Short
        Get
            If handle_ IsNot Nothing And Not handle_.IsInvalid And Not handle_.IsClosed Then
                Dim range As Integer = 0
                If CheckHResult(Toupcam_get_Option(handle_, eOPTION.OPTION_TECTARGET_RANGE, range)) Then
                    Dim x As Integer = range And &HFFFF
                    Return BitConverter.ToInt16(BitConverter.GetBytes(x), 0)
                End If
            End If
            Return Short.MinValue
        End Get
    End Property

    ' get the revision
    Public ReadOnly Property Revision() As UShort
        Get
            Dim rev As UShort = 0
            If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
                Return rev
            End If
            Toupcam_get_Revision(handle_, rev)
            Return rev
        End Get
    End Property

    ' get the serial number which is always 32 chars which is zero-terminated such as "TP110826145730ABCD1234FEDC56787"
    Public ReadOnly Property SerialNumber() As String
        Get
            Dim str As String = ""
            If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
                Return str
            End If
            Dim ptr As IntPtr = Marshal.AllocHGlobal(64)
            If Toupcam_get_SerialNumber(handle_, ptr) >= 0 Then
                str = Marshal.PtrToStringAnsi(ptr)
            End If
            Marshal.FreeHGlobal(ptr)
            Return str
        End Get
    End Property

    ' get the camera firmware version, such as: 3.2.1.20140922
    Public ReadOnly Property FwVersion() As String
        Get
            Dim str As String = ""
            If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
                Return str
            End If
            Dim ptr As IntPtr = Marshal.AllocHGlobal(32)
            If Toupcam_get_FwVersion(handle_, ptr) >= 0 Then
                str = ""
            Else
                str = Marshal.PtrToStringAnsi(ptr)
            End If
            Marshal.FreeHGlobal(ptr)
            Return str
        End Get
    End Property

    ' get the camera hardware version, such as: 3.2.1.20140922
    Public ReadOnly Property HwVersion() As String
        Get
            Dim str As String = ""
            If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
                Return str
            End If
            Dim ptr As IntPtr = Marshal.AllocHGlobal(32)
            If Toupcam_get_HwVersion(handle_, ptr) >= 0 Then
                str = ""
            Else
                str = Marshal.PtrToStringAnsi(ptr)
            End If
            Marshal.FreeHGlobal(ptr)
            Return str
        End Get
    End Property

    ' get FPGA version, such as: 1.3
    Public ReadOnly Property FpgaVersion() As String
        Get
            Dim str As String = ""
            If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
                Return str
            End If
            Dim ptr As IntPtr = Marshal.AllocHGlobal(32)
            If Toupcam_get_FpgaVersion(handle_, ptr) >= 0 Then
                str = ""
            Else
                str = Marshal.PtrToStringAnsi(ptr)
            End If
            Marshal.FreeHGlobal(ptr)
            Return str
        End Get
    End Property

    ' such as: 20150327
    Public ReadOnly Property ProductionDate() As String
        Get
            Dim str As String = ""
            If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
                Return str
            End If
            Dim ptr As IntPtr = Marshal.AllocHGlobal(32)
            If Toupcam_get_ProductionDate(handle_, ptr) >= 0 Then
                str = ""
            Else
                str = Marshal.PtrToStringAnsi(ptr)
            End If
            Marshal.FreeHGlobal(ptr)
            Return str
        End Get
    End Property

    Public ReadOnly Property Field() As UInteger
        Get
            If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
                Return 0
            End If
            Return Toupcam_get_Field(handle_)
        End Get
    End Property

#If Not (NETFX_CORE OrElse NETCOREAPP OrElse WINDOWS_UWP) Then
    Public Function StartPullModeWithWndMsg(hWnd As IntPtr, nMsg As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_StartPullModeWithWndMsg(handle_, hWnd, nMsg))
    End Function
#End If

    Public Function StartPullModeWithCallback(funEvent As DelegateEventCallback) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        funEvent_ = funEvent
        If funEvent Is Nothing Then
            Return CheckHResult(Toupcam_StartPullModeWithCallback(handle_, Nothing, IntPtr.Zero))
        Else
            pEvent_ = New EVENT_CALLBACK(AddressOf EventCallback)
            Return CheckHResult(Toupcam_StartPullModeWithCallback(handle_, pEvent_, id_))
        End If
    End Function

    '  bits: 24 (RGB24), 32 (RGB32), 8 (Grey), 16 (Grey), 48(RGB48), 64(RGB64)
    <Obsolete("Use FrameInfoV4")>
    Public Function PullImage(pImageData As IntPtr, bits As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImage(handle_, pImageData, bits, pnWidth, pnHeight))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImage(pImageData As Byte(), bits As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImage(handle_, pImageData, bits, pnWidth, pnHeight))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImage(pImageData As UShort(), bits As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImage(handle_, pImageData, bits, pnWidth, pnHeight))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImage(pImageData As IntPtr, bits As Integer, ByRef pInfo As FrameInfoV2) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageV2(handle_, pImageData, bits, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImage(pImageData As Byte(), bits As Integer, ByRef pInfo As FrameInfoV2) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageV2(handle_, pImageData, bits, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImage(pImageData As UShort(), bits As Integer, ByRef pInfo As FrameInfoV2) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageV2(handle_, pImageData, bits, pInfo))
    End Function

    ' bits: 24 (RGB24), 32 (RGB32), 8 (Grey), 16 (Grey), 48(RGB48), 64(RGB64)
    <Obsolete("Use FrameInfoV4")>
    Public Function PullStillImage(pImageData As IntPtr, bits As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullStillImage(handle_, pImageData, bits, pnWidth, pnHeight))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullStillImage(pImageData As Byte(), bits As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullStillImage(handle_, pImageData, bits, pnWidth, pnHeight))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullStillImage(pImageData As UShort(), bits As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullStillImage(handle_, pImageData, bits, pnWidth, pnHeight))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullStillImageV2(pImageData As IntPtr, bits As Integer, ByRef pInfo As FrameInfoV2) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullStillImageV2(handle_, pImageData, bits, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullStillImageV2(pImageData As Byte(), bits As Integer, ByRef pInfo As FrameInfoV2) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullStillImageV2(handle_, pImageData, bits, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullStillImageV2(pImageData As UShort(), bits As Integer, ByRef pInfo As FrameInfoV2) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullStillImageV2(handle_, pImageData, bits, pInfo))
    End Function

    ' nWaitMS: The timeout interval, in milliseconds. If a non-zero value is specified, the function either successfully fetches the image or waits for a timeout.
    '          If nWaitMS is zero, the function does not wait when there are no images to fetch; It always returns immediately; this is equal to PullImageV3.
    ' bStill: to pull still image, set to 1, otherwise 0
    ' bits: 24 (RGB24), 32 (RGB32), 48 (RGB48), 8 (Grey), 16 (Grey), 64 (RGB64).
    '       In RAW mode, this parameter is ignored.
    '       bits = 0 means using default bits base on OPTION_RGB.
    '       When bits and OPTION_RGB are inconsistent, format conversion will have to be performed, resulting in loss of efficiency.
    '       See the following bits and OPTION_RGB correspondence table:
    '         ----------------------------------------------------------------------------------------------------------------------
    '         | OPTION_RGB         |   0 (RGB24)   |   1 (RGB48)   |   2 (RGB32)   |   3 (Grey8)   |  4 (Grey16)   |   5 (RGB64)   |
    '         |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
    '         | bits = 0           |      24       |       48      |      32       |       8       |       16      |       64      |
    '         |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
    '         | bits = 24          |      24       |       NA      | Convert to 24 | Convert to 24 |       NA      |       NA      |
    '         |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
    '         | bits = 32          | Convert to 32 |       NA      |       32      | Convert to 32 |       NA      |       NA      |
    '         |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
    '         | bits = 48          |      NA       |       48      |       NA      |       NA      | Convert to 48 | Convert to 48 |
    '         |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
    '         | bits = 8           | Convert to 8  |       NA      | Convert to 8  |       8       |       NA      |       NA      |
    '         |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
    '         | bits = 16          |      NA       | Convert to 16 |       NA      |       NA      |       16      | Convert to 16 |
    '         |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
    '         | bits = 64          |      NA       | Convert to 64 |       NA      |       NA      | Convert to 64 |       64      |
    '         |--------------------|---------------|---------------|---------------|---------------|---------------|---------------|
    '
    ' rowPitch: The distance from one row to the next row. rowPitch = 0 means using the default row pitch. rowPitch = -1 means zero padding, see below:
    '         ----------------------------------------------------------------------------------------------
    '         | format                             | 0 means default row pitch     | -1 means zero padding |
    '         |------------------------------------|-------------------------------|-----------------------|
    '         | RGB       | RGB24                  | TDIBWIDTHBYTES(24 * Width)    | Width * 3             |
    '         |           | RGB32                  | Width * 4                     | Width * 4             |
    '         |           | RGB48                  | TDIBWIDTHBYTES(48 * Width)    | Width * 6             |
    '         |           | GREY8                  | TDIBWIDTHBYTES(8 * Width)     | Width                 |
    '         |           | GREY16                 | TDIBWIDTHBYTES(16 * Width)    | Width * 2             |
    '         |           | RGB64                  | Width * 8                     | Width * 8             |
    '         |-----------|------------------------|-------------------------------|-----------------------|
    '         | RAW       | 8bits Mode             | Width                         | Width                 |
    '         |           | 10/12/14/16bits Mode   | Width * 2                     | Width * 2             |
    '         |-----------|------------------------|-------------------------------|-----------------------|
    '
    Public Function PullImage(pImageData As IntPtr, bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageV4(handle_, pImageData, bStill, bits, rowPitch, pInfo))
    End Function

    Public Function PullImage(pImageData As Byte(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageV4(handle_, pImageData, bStill, bits, rowPitch, pInfo))
    End Function

    Public Function PullImage(pImageData As UShort(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageV4(handle_, pImageData, bStill, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImage(pImageData As IntPtr, bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageV3(handle_, pImageData, bStill, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImage(pImageData As Byte(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageV3(handle_, pImageData, bStill, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImage(pImageData As UShort(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageV3(handle_, pImageData, bStill, bits, rowPitch, pInfo))
    End Function

    Public Function WaitImage(nWaitMS As UInteger, pImageData As IntPtr, bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_WaitImageV4(handle_, nWaitMS, pImageData, bStill, bits, rowPitch, pInfo))
    End Function

    Public Function WaitImage(nWaitMS As UInteger, pImageData As Byte(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_WaitImageV4(handle_, nWaitMS, pImageData, bStill, bits, rowPitch, pInfo))
    End Function

    Public Function WaitImage(nWaitMS As UInteger, pImageData As UShort(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_WaitImageV4(handle_, nWaitMS, pImageData, bStill, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function WaitImage(nWaitMS As UInteger, pImageData As IntPtr, bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_WaitImageV3(handle_, nWaitMS, pImageData, bStill, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function WaitImage(nWaitMS As UInteger, pImageData As Byte(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_WaitImageV3(handle_, nWaitMS, pImageData, bStill, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function WaitImage(nWaitMS As UInteger, pImageData As UShort(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_WaitImageV3(handle_, nWaitMS, pImageData, bStill, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImageWithRowPitch(pImageData As IntPtr, bits As Integer, rowPitch As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageWithRowPitch(handle_, pImageData, bits, rowPitch, pnWidth, pnHeight))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImageWithRowPitch(pImageData As Byte(), bits As Integer, rowPitch As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageWithRowPitch(handle_, pImageData, bits, rowPitch, pnWidth, pnHeight))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImageWithRowPitch(pImageData As UShort(), bits As Integer, rowPitch As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageWithRowPitch(handle_, pImageData, bits, rowPitch, pnWidth, pnHeight))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImageWithRowPitchV2(pImageData As IntPtr, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV2) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageWithRowPitchV2(handle_, pImageData, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImageWithRowPitchV2(pImageData As Byte(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV2) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageWithRowPitchV2(handle_, pImageData, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullImageWithRowPitchV2(pImageData As UShort(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV2) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullImageWithRowPitchV2(handle_, pImageData, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullStillImageWithRowPitch(pImageData As IntPtr, bits As Integer, rowPitch As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullStillImageWithRowPitch(handle_, pImageData, bits, rowPitch, pnWidth, pnHeight))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullStillImageWithRowPitch(pImageData As Byte(), bits As Integer, rowPitch As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullStillImageWithRowPitch(handle_, pImageData, bits, rowPitch, pnWidth, pnHeight))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullStillImageWithRowPitch(pImageData As UShort(), bits As Integer, rowPitch As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullStillImageWithRowPitch(handle_, pImageData, bits, rowPitch, pnWidth, pnHeight))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullStillImageWithRowPitchV2(pImageData As IntPtr, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV2) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullStillImageWithRowPitchV2(handle_, pImageData, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullStillImageWithRowPitchV2(pImageData As Byte(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV2) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullStillImageWithRowPitchV2(handle_, pImageData, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function PullStillImageWithRowPitchV2(pImageData As UShort(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV2) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_PullStillImageWithRowPitchV2(handle_, pImageData, bits, rowPitch, pInfo))
    End Function

    Public Function StartPushModeV4(funData As DelegateDataCallbackV4, funEvent As DelegateEventCallback) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        funDataV4_ = funData
        funEvent_ = funEvent
        pDataV4_ = New DATA_CALLBACK_V4(AddressOf DataCallbackV4)
        pEvent_ = New EVENT_CALLBACK(AddressOf EventCallback)
        Return CheckHResult(Toupcam_StartPushModeV4(handle_, pDataV4_, id_, pEvent_, id_))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function StartPushModeV3(funData As DelegateDataCallbackV3, funEvent As DelegateEventCallback) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        funDataV3_ = funData
        funEvent_ = funEvent
        pDataV3_ = New DATA_CALLBACK_V3(AddressOf DataCallbackV3)
        pEvent_ = New EVENT_CALLBACK(AddressOf EventCallback)
        Return CheckHResult(Toupcam_StartPushModeV3(handle_, pDataV3_, id_, pEvent_, id_))
    End Function

    Public Function [Stop]() As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_Stop(handle_))
    End Function

    ' 1 => pause, 0 => continue
    Public Function Pause(bPause As Boolean) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_Pause(handle_, If(bPause, 1, 0)))
    End Function

    ' nResolutionIndex = 0xffffffff means use the cureent preview resolution
    Public Function Snap(nResolutionIndex As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_Snap(handle_, nResolutionIndex))
    End Function

    ' multiple still image snap, nResolutionIndex = 0xffffffff means use the cureent preview resolution
    Public Function SnapN(nResolutionIndex As UInteger, nNumber As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_SnapN(handle_, nResolutionIndex, nNumber))
    End Function

    ' multiple RAW still image snap, nResolutionIndex = 0xffffffff means use the cureent preview resolution
    Public Function SnapR(nResolutionIndex As UInteger, nNumber As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_SnapR(handle_, nResolutionIndex, nNumber))
    End Function

    '
    '    soft trigger:
    '    nNumber:    0xffff:     trigger continuously
    '                0:          cancel trigger
    '                others:     number of images to be triggered
    '
    Public Function Trigger(nNumber As UShort) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_Trigger(handle_, nNumber))
    End Function

    '  trigger synchronously
    '  nWaitMS:     0:              by default, exposure * 102% + 4000 milliseconds
    '               0xffffffff:     wait infinite
    '               other:          milliseconds to wait
    '
    Public Function TriggerSync(nWaitMS As UInteger, pImageData As IntPtr, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_TriggerSyncV4(handle_, nWaitMS, pImageData, bits, rowPitch, pInfo))
    End Function

    Public Function TriggerSync(nWaitMS As UInteger, pImageData As Byte(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_TriggerSyncV4(handle_, nWaitMS, pImageData, bits, rowPitch, pInfo))
    End Function

    Public Function TriggerSync(nWaitMS As UInteger, pImageData As UShort(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_TriggerSyncV4(handle_, nWaitMS, pImageData, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function TriggerSync(nWaitMS As UInteger, pImageData As IntPtr, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_TriggerSync(handle_, nWaitMS, pImageData, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function TriggerSync(nWaitMS As UInteger, pImageData As Byte(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_TriggerSync(handle_, nWaitMS, pImageData, bits, rowPitch, pInfo))
    End Function

    <Obsolete("Use FrameInfoV4")>
    Public Function TriggerSync(nWaitMS As UInteger, pImageData As UShort(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Return CheckHResult(Toupcam_TriggerSync(handle_, nWaitMS, pImageData, bits, rowPitch, pInfo))
    End Function

    '
    '  put_Size, put_eSize, can be used to set the video output resolution BEFORE Start.
    '  put_Size use width and height parameters, put_eSize use the index parameter.
    '  for example, UCMOS03100KPA support the following resolutions:
    '      index 0:    2048,   1536
    '      index 1:    1024,   768
    '      index 2:    680,    510
    '  so, we can use put_Size(h, 1024, 768) or put_eSize(h, 1). Both have the same effect.
    '
    Public Function put_Size(nWidth As Integer, nHeight As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Size(handle_, nWidth, nHeight))
    End Function

    Public Function get_Size(ByRef nWidth As Integer, ByRef nHeight As Integer) As Boolean
        nWidth = 0
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_Size(handle_, nWidth, nHeight))
    End Function

    Public Function put_eSize(nResolutionIndex As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_eSize(handle_, nResolutionIndex))
    End Function

    Public Function get_eSize(ByRef nResolutionIndex As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_eSize(handle_, nResolutionIndex))
    End Function

    '
    ' final size after ROI, rotate, binning
    '
    Public Function get_FinalSize(ByRef nWidth As Integer, ByRef nHeight As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_FinalSize(handle_, nWidth, nHeight))
    End Function

    Public Function get_Resolution(nResolutionIndex As UInteger, ByRef pWidth As Integer, ByRef pHeight As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_Resolution(handle_, nResolutionIndex, pWidth, pHeight))
    End Function

    '
    ' get the sensor pixel size, such as: 2.4um x 2.4um
    '
    Public Function get_PixelSize(nResolutionIndex As UInteger, ByRef x As Single, ByRef y As Single) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_PixelSize(handle_, nResolutionIndex, x, y))
    End Function

    '
    ' numerator/denominator, such as: 1/1, 1/2, 1/3
    '
    Public Function get_ResolutionRatio(nResolutionIndex As UInteger, ByRef pNumerator As Integer, ByRef pDenominator As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_ResolutionRatio(handle_, nResolutionIndex, pNumerator, pDenominator))
    End Function

    '
    ' see: http://www.fourcc.org
    ' FourCC:
    '     MAKEFOURCC('G', 'B', 'R', 'G'), see http://www.siliconimaging.com/RGB%20Bayer.htm
    '     MAKEFOURCC('R', 'G', 'G', 'B')
    '     MAKEFOURCC('B', 'G', 'G', 'R')
    '     MAKEFOURCC('G', 'R', 'B', 'G')
    '     MAKEFOURCC('Y', 'Y', 'Y', 'Y'), monochromatic sensor
    '     MAKEFOURCC('Y', '4', '1', '1'), yuv411
    '     MAKEFOURCC('V', 'U', 'Y', 'Y'), yuv422
    '     MAKEFOURCC('U', 'Y', 'V', 'Y'), yuv422
    '     MAKEFOURCC('Y', '4', '4', '4'), yuv444
    '     MAKEFOURCC('R', 'G', 'B', '8'), RGB888
    '
    Public Function get_RawFormat(ByRef nFourCC As UInteger, ByRef bitdepth As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_RawFormat(handle_, nFourCC, bitdepth))
    End Function

    ' 0: no realtime
    '       stop grab frame when frame buffer deque is full, until the frames in the queue are pulled away and the queue is not full
    ' 1: realtime
    '       use minimum frame buffer. When new frame arrive, drop all the pending frame regardless of whether the frame buffer is full.
    '       If DDR present, also limit the DDR frame buffer to only one frame.
    ' 2: soft realtime
    '       Drop the oldest frame when the queue is full and then enqueue the new frame
    ' default: 0
    '
    Public Function put_RealTime(val As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_RealTime(handle_, val))
    End Function

    Public Function get_RealTime(ByRef val As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_RealTime(handle_, val))
    End Function

    ' Flush() is obsolete, recommend using put_Option(OPTION_FLUSH, 3)
    Public Function Flush() As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_Flush(handle_))
    End Function

    '
    ' bAutoExposure:
    '   0: disable auto exposure
    '   1: auto exposure continue mode
    '   2: auto exposure once mode
    '
    Public Function get_AutoExpoEnable(ByRef bAutoExposure As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_AutoExpoEnable(handle_, bAutoExposure))
    End Function

    Public Function put_AutoExpoEnable(bAutoExposure As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_AutoExpoEnable(handle_, bAutoExposure))
    End Function

    Public Function get_AutoExpoEnable(ByRef bAutoExposure As Boolean) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim iEnable As Integer = 0
        If Not CheckHResult(Toupcam_get_AutoExpoEnable(handle_, iEnable)) Then
            Return False
        End If

        bAutoExposure = (iEnable <> 0)
        Return True
    End Function

    Public Function put_AutoExpoEnable(bAutoExposure As Boolean) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_AutoExpoEnable(handle_, If(bAutoExposure, 1, 0)))
    End Function

    Public Function get_AutoExpoTarget(ByRef Target As UShort) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_AutoExpoTarget(handle_, Target))
    End Function

    Public Function put_AutoExpoTarget(Target As UShort) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_AutoExpoTarget(handle_, Target))
    End Function

    ' set the maximum/minimal auto exposure time and agin. The default maximum auto exposure time is 350ms
    Public Function put_AutoExpoRange(maxTime As UInteger, minTime As UInteger, maxGain As UShort, minGain As UShort) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_AutoExpoRange(handle_, maxTime, minTime, maxGain, minGain))
    End Function

    Public Function get_AutoExpoRange(ByRef maxTime As UInteger, ByRef minTime As UInteger, ByRef maxGain As UShort, ByRef minGain As UShort) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_AutoExpoRange(handle_, maxTime, minTime, maxGain, minGain))
    End Function

    Public Function put_MaxAutoExpoTimeAGain(maxTime As UInteger, maxGain As UShort) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_MaxAutoExpoTimeAGain(handle_, maxTime, maxGain))
    End Function

    Public Function get_MaxAutoExpoTimeAGain(ByRef maxTime As UInteger, ByRef maxGain As UShort) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_MaxAutoExpoTimeAGain(handle_, maxTime, maxGain))
    End Function

    Public Function put_MinAutoExpoTimeAGain(minTime As UInteger, minGain As UShort) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_MinAutoExpoTimeAGain(handle_, minTime, minGain))
    End Function

    Public Function get_MinAutoExpoTimeAGain(ByRef minTime As UInteger, ByRef minGain As UShort) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_MinAutoExpoTimeAGain(handle_, minTime, minGain))
    End Function

    Public Function get_ExpoTime(ByRef Time As UInteger) As Boolean
        ' in microseconds
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_ExpoTime(handle_, Time))
    End Function

    Public Function get_RealExpoTime(ByRef Time As UInteger) As Boolean
        ' actual exposure time
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_RealExpoTime(handle_, Time))
    End Function

    Public Function put_ExpoTime(Time As UInteger) As Boolean
        ' in microseconds
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_ExpoTime(handle_, Time))
    End Function

    Public Function get_ExpTimeRange(ByRef nMin As UInteger, ByRef nMax As UInteger, ByRef nDef As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_ExpTimeRange(handle_, nMin, nMax, nDef))
    End Function

    Public Function get_ExpoAGain(ByRef Gain As UShort) As Boolean
        ' percent, such as 300
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_ExpoAGain(handle_, Gain))
    End Function

    Public Function put_ExpoAGain(Gain As UShort) As Boolean
        ' percent
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_ExpoAGain(handle_, Gain))
    End Function

    Public Function get_ExpoAGainRange(ByRef nMin As UShort, ByRef nMax As UShort, ByRef nDef As UShort) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_ExpoAGainRange(handle_, nMin, nMax, nDef))
    End Function

    Public Function put_LevelRange(aLow As UShort(), aHigh As UShort()) As Boolean
        If aLow.Length <> 4 OrElse aHigh.Length <> 4 Then
            Return False
        End If
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_LevelRange(handle_, aLow, aHigh))
    End Function

    Public Function get_LevelRange(aLow As UShort(), aHigh As UShort()) As Boolean
        If aLow.Length <> 4 OrElse aHigh.Length <> 4 Then
            Return False
        End If
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_LevelRange(handle_, aLow, aHigh))
    End Function

    Public Function put_LevelRangeV2(mode As UShort, roiX As Integer, roiY As Integer, roiWidth As Integer, roiHeight As Integer, aLow As UShort(), aHigh As UShort()) As Boolean
        If aLow.Length <> 4 OrElse aHigh.Length <> 4 Then
            Return False
        End If
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Dim rc As New RECT()
        rc.left = roiX
        rc.right = roiX + roiWidth
        rc.top = roiY
        rc.bottom = roiY + roiHeight
        Return CheckHResult(Toupcam_put_LevelRangeV2(handle_, mode, rc, aLow, aHigh))
    End Function

    Public Function get_LevelRangeV2(mode As UShort, ByRef roiX As Integer, ByRef roiY As Integer, ByRef roiWidth As Integer, ByRef roiHeight As Integer, aLow As UShort(), aHigh As UShort()) As Boolean
        If aLow.Length <> 4 OrElse aHigh.Length <> 4 Then
            Return False
        End If
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim rc As New RECT()
        If Not CheckHResult(Toupcam_get_LevelRangeV2(handle_, mode, rc, aLow, aHigh)) Then
            Return False
        End If

        roiX = rc.left
        roiY = rc.top
        roiWidth = rc.right - rc.left
        roiHeight = rc.bottom - rc.top
        Return True
    End Function

    Public Function put_Hue(Hue As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Hue(handle_, Hue))
    End Function

    Public Function get_Hue(ByRef Hue As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_Hue(handle_, Hue))
    End Function

    Public Function put_Saturation(Saturation As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Saturation(handle_, Saturation))
    End Function

    Public Function get_Saturation(ByRef Saturation As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_Saturation(handle_, Saturation))
    End Function

    Public Function put_Brightness(Brightness As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Brightness(handle_, Brightness))
    End Function

    Public Function get_Brightness(ByRef Brightness As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_Brightness(handle_, Brightness))
    End Function

    Public Function get_Contrast(ByRef Contrast As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_Contrast(handle_, Contrast))
    End Function

    Public Function put_Contrast(Contrast As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Contrast(handle_, Contrast))
    End Function

    Public Function get_Gamma(ByRef Gamma As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_Gamma(handle_, Gamma))
    End Function

    Public Function put_Gamma(Gamma As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Gamma(handle_, Gamma))
    End Function

    Public Function get_Chrome(ByRef bChrome As Boolean) As Boolean
        ' monochromatic mode
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim iEnable As Integer = 0
        If Not CheckHResult(Toupcam_get_Chrome(handle_, iEnable)) Then
            Return False
        End If

        bChrome = (iEnable <> 0)
        Return True
    End Function

    Public Function put_Chrome(bChrome As Boolean) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Chrome(handle_, If(bChrome, 1, 0)))
    End Function

    Public Function get_VFlip(ByRef bVFlip As Boolean) As Boolean
        ' vertical flip
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim iVFlip As Integer = 0
        If Not CheckHResult(Toupcam_get_VFlip(handle_, iVFlip)) Then
            Return False
        End If

        bVFlip = (iVFlip <> 0)
        Return True
    End Function

    Public Function put_VFlip(bVFlip As Boolean) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_VFlip(handle_, If(bVFlip, 1, 0)))
    End Function

    Public Function get_HFlip(ByRef bHFlip As Boolean) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim iHFlip As Integer = 0
        If Not CheckHResult(Toupcam_get_HFlip(handle_, iHFlip)) Then
            Return False
        End If

        bHFlip = (iHFlip <> 0)
        Return True
    End Function

    Public Function put_HFlip(bHFlip As Boolean) As Boolean
        ' horizontal flip
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_HFlip(handle_, If(bHFlip, 1, 0)))
    End Function

    ' negative film
    Public Function get_Negative(ByRef bNegative As Boolean) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim iNegative As Integer = 0
        If Not CheckHResult(Toupcam_get_Negative(handle_, iNegative)) Then
            Return False
        End If

        bNegative = (iNegative <> 0)
        Return True
    End Function

    ' negative film
    Public Function put_Negative(bNegative As Boolean) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Negative(handle_, If(bNegative, 1, 0)))
    End Function

    Public Function put_Speed(nSpeed As UShort) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Speed(handle_, nSpeed))
    End Function

    Public Function get_Speed(ByRef pSpeed As UShort) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_Speed(handle_, pSpeed))
    End Function

    Public Function put_HZ(nHZ As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_HZ(handle_, nHZ))
    End Function

    Public Function get_HZ(ByRef nHZ As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_HZ(handle_, nHZ))
    End Function

    Public Function put_Mode(bSkip As Boolean) As Boolean
        ' skip or bin
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Mode(handle_, If(bSkip, 1, 0)))
    End Function

    Public Function get_Mode(ByRef bSkip As Boolean) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim iSkip As Integer = 0
        If Not CheckHResult(Toupcam_get_Mode(handle_, iSkip)) Then
            Return False
        End If

        bSkip = (iSkip <> 0)
        Return True
    End Function

    ' White Balance, Temp/Tint mode
    Public Function put_TempTint(nTemp As Integer, nTint As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_TempTint(handle_, nTemp, nTint))
    End Function

    ' White Balance, Temp/Tint mode
    Public Function get_TempTint(ByRef nTemp As Integer, ByRef nTint As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_TempTint(handle_, nTemp, nTint))
    End Function

    ' White Balance, RGB Gain Mode
    Public Function put_WhiteBalanceGain(aGain As Integer()) As Boolean
        If aGain.Length <> 3 Then
            Return False
        End If
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_WhiteBalanceGain(handle_, aGain))
    End Function

    ' White Balance, RGB Gain Mode
    Public Function get_WhiteBalanceGain(aGain As Integer()) As Boolean
        If aGain.Length <> 3 Then
            Return False
        End If
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_WhiteBalanceGain(handle_, aGain))
    End Function

    Public Function put_AWBAuxRect(X As Integer, Y As Integer, Width As Integer, Height As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim rc As New RECT()
        rc.left = X
        rc.right = X + Width
        rc.top = Y
        rc.bottom = Y + Height
        Return CheckHResult(Toupcam_put_AWBAuxRect(handle_, rc))
    End Function

    Public Function get_AWBAuxRect(ByRef X As Integer, ByRef Y As Integer, ByRef Width As Integer, ByRef Height As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim rc As New RECT()
        If Not CheckHResult(Toupcam_get_AWBAuxRect(handle_, rc)) Then
            Return False
        End If

        X = rc.left
        Y = rc.top
        Width = rc.right - rc.left
        Height = rc.bottom - rc.top
        Return True
    End Function

    Public Function put_BlackBalance(aSub As UShort()) As Boolean
        If aSub.Length <> 3 Then
            Return False
        End If
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_BlackBalance(handle_, aSub))
    End Function

    Public Function get_BlackBalance(aSub As UShort()) As Boolean
        If aSub.Length <> 3 Then
            Return False
        End If
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_BlackBalance(handle_, aSub))
    End Function

    Public Function put_ABBAuxRect(X As Integer, Y As Integer, Width As Integer, Height As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim rc As New RECT()
        rc.left = X
        rc.right = X + Width
        rc.top = Y
        rc.bottom = Y + Height
        Return CheckHResult(Toupcam_put_ABBAuxRect(handle_, rc))
    End Function

    Public Function get_ABBAuxRect(ByRef X As Integer, ByRef Y As Integer, ByRef Width As Integer, ByRef Height As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim rc As New RECT()
        If Not CheckHResult(Toupcam_get_ABBAuxRect(handle_, rc)) Then
            Return False
        End If

        X = rc.left
        Y = rc.top
        Width = rc.right - rc.left
        Height = rc.bottom - rc.top
        Return True
    End Function

    Public Function put_AEAuxRect(X As Integer, Y As Integer, Width As Integer, Height As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim rc As New RECT()
        rc.left = X
        rc.right = X + Width
        rc.top = Y
        rc.bottom = Y + Height
        Return CheckHResult(Toupcam_put_AEAuxRect(handle_, rc))
    End Function

    Public Function get_AEAuxRect(ByRef X As Integer, ByRef Y As Integer, ByRef Width As Integer, ByRef Height As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim rc As New RECT()
        If Not CheckHResult(Toupcam_get_AEAuxRect(handle_, rc)) Then
            Return False
        End If

        X = rc.left
        Y = rc.top
        Width = rc.right - rc.left
        Height = rc.bottom - rc.top
        Return True
    End Function

    Public Function get_StillResolution(nResolutionIndex As UInteger, ByRef pWidth As Integer, ByRef pHeight As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_StillResolution(handle_, nResolutionIndex, pWidth, pHeight))
    End Function

    Public Function put_VignetEnable(bEnable As Boolean) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_VignetEnable(handle_, If(bEnable, 1, 0)))
    End Function

    Public Function get_VignetEnable(ByRef bEnable As Boolean) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If

        Dim iEanble As Integer = 0
        If Not CheckHResult(Toupcam_get_VignetEnable(handle_, iEanble)) Then
            Return False
        End If

        bEnable = (iEanble <> 0)
        Return True
    End Function

    Public Function put_VignetAmountInt(nAmount As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_VignetAmountInt(handle_, nAmount))
    End Function

    Public Function get_VignetAmountInt(ByRef nAmount As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_VignetAmountInt(handle_, nAmount))
    End Function

    Public Function put_VignetMidPointInt(nMidPoint As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_VignetMidPointInt(handle_, nMidPoint))
    End Function

    Public Function get_VignetMidPointInt(ByRef nMidPoint As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_VignetMidPointInt(handle_, nMidPoint))
    End Function

    Public Function LevelRangeAuto() As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_LevelRangeAuto(handle_))
    End Function

    ' led state:
    '    iLed: Led index, (0, 1, 2, ...)
    '    iState: 1 => Ever bright; 2 => Flashing; other => Off
    '    iPeriod: Flashing Period (>= 500ms)
    Public Function put_LEDState(iLed As UShort, iState As UShort, iPeriod As UShort) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_LEDState(handle_, iLed, iState, iPeriod))
    End Function

    Public Function write_EEPROM(addr As UInteger, pBuffer As IntPtr, nBufferLen As UInteger) As Integer
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return E_UNEXPECTED
        End If
        Return Toupcam_write_EEPROM(handle_, addr, pBuffer, nBufferLen)
    End Function

    Public Function write_EEPROM(addr As UInteger, pBuffer As Byte()) As Integer
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return E_UNEXPECTED
        End If
        Return Toupcam_write_EEPROM(handle_, addr, pBuffer, pBuffer.Length)
    End Function

    Public Function read_EEPROM(addr As UInteger, pBuffer As IntPtr, nBufferLen As UInteger) As Integer
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return E_UNEXPECTED
        End If
        Return Toupcam_read_EEPROM(handle_, addr, pBuffer, nBufferLen)
    End Function

    Public Function read_EEPROM(addr As UInteger, pBuffer As Byte()) As Integer
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return E_UNEXPECTED
        End If
        Return Toupcam_read_EEPROM(handle_, addr, pBuffer, pBuffer.Length)
    End Function

    Public Const FLASH_SIZE As UInteger = &H0    ' query total size
    Public Const FLASH_EBLOCK As UInteger = &H1  ' query Erase block size
    Public Const FLASH_RWBLOCK As UInteger = &H2 ' query read/write block size
    Public Const FLASH_STATUS As UInteger = &H3  ' query status
    Public Const FLASH_READ As UInteger = &H4    ' read
    Public Const FLASH_WRITE As UInteger = &H5   ' write
    Public Const FLASH_ERASE As UInteger = &H6   ' erase
    ' Flash:
    ' action = FLASH_XXXX: read, write, erase, query total size, query read/write block size, query erase block size
    ' addr = address
    ' see democpp
    Public Function rwc_Flash(action As UInteger, addr As UInteger, len As UInteger, pData As IntPtr) As Integer
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return E_UNEXPECTED
        End If
        Return Toupcam_rwc_Flash(handle_, action, addr, len, pData)
    End Function

    Public Function rwc_Flash(action As UInteger, addr As UInteger, pData As Byte()) As Integer
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return E_UNEXPECTED
        End If
        Return Toupcam_rwc_Flash(handle_, action, addr, pData.Length, pData)
    End Function

    Public Function write_Pipe(pipeId As UInteger, pBuffer As IntPtr, nBufferLen As UInteger) As Integer
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return E_UNEXPECTED
        End If
        Return Toupcam_write_Pipe(handle_, pipeId, pBuffer, nBufferLen)
    End Function

    Public Function read_Pipe(pipeId As UInteger, pBuffer As IntPtr, nBufferLen As UInteger) As Integer
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return E_UNEXPECTED
        End If
        Return Toupcam_read_Pipe(handle_, pipeId, pBuffer, nBufferLen)
    End Function

    Public Function feed_Pipe(pipeId As UInteger) As Integer
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return E_UNEXPECTED
        End If
        Return Toupcam_feed_Pipe(handle_, pipeId)
    End Function

    Public Function write_UART(pBuffer As IntPtr, nBufferLen As UInteger) As Integer
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return E_UNEXPECTED
        End If
        Return Toupcam_write_UART(handle_, pBuffer, nBufferLen)
    End Function

    Public Function read_UART(pBuffer As IntPtr, nBufferLen As UInteger) As Integer
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return E_UNEXPECTED
        End If
        Return Toupcam_read_UART(handle_, pBuffer, nBufferLen)
    End Function

    Public Function put_Option(iOption As eOPTION, iValue As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Option(handle_, iOption, iValue))
    End Function

    Public Function get_Option(iOption As eOPTION, ByRef iValue As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            iValue = 0
            Return False
        End If
        Return CheckHResult(Toupcam_get_Option(handle_, iOption, iValue))
    End Function

    '
    ' cmd: input
    '    -1:       query the number
    '    0~number: query the nth pixel format
    ' pixelFormat: output, PIXELFORMAT_xxxx
    '
    Public Function get_PixelFormatSupport(cmd As SByte, ByRef pixelFormat As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            pixelFormat = 0
            Return False
        End If
        Return CheckHResult(Toupcam_get_PixelFormatSupport(handle_, cmd, pixelFormat))
    End Function

    ' Hardware Binning
    ' Value: 1x1, 2x2, etc
    ' Method: Average, Add, Skip
    '
    Public Function put_Binning(Value As String, Method As String) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Binning(handle_, Value, Method))
    End Function

    Public Function get_Binning(ByRef Value As String, ByRef Method As String) As Boolean
        Value = Method = Nothing
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Dim valuePtr As New IntPtr(0)
        Dim methodPtr As New IntPtr(0)
        Dim ret As Boolean = CheckHResult(Toupcam_get_Binning(handle_, valuePtr, methodPtr))
        If ret Then
            Value = Marshal.PtrToStringAnsi(valuePtr)
            Method = Marshal.PtrToStringAnsi(methodPtr)
        End If
        Return ret
    End Function

    Public Function get_BinningNumber() As Integer
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return 0
        End If
        Return Toupcam_get_BinningNumber(handle_)
    End Function

    Public Function get_BinningValue(index As UInteger, ByRef Value As String) As Boolean
        Value = Nothing
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Dim valuePtr As New IntPtr(0)
        Dim ret As Boolean = CheckHResult(Toupcam_get_BinningValue(handle_, index, valuePtr))
        If ret Then
            Value = Marshal.PtrToStringAnsi(valuePtr)
        End If
        Return ret
    End Function

    Public Function get_BinningMethod(index As UInteger, ByRef Method As String) As Boolean
        Method = Nothing
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Dim methodPtr As New IntPtr(0)
        Dim ret As Boolean = CheckHResult(Toupcam_get_BinningMethod(handle_, index, methodPtr))
        If ret Then
            Method = Marshal.PtrToStringAnsi(methodPtr)
        End If
        Return ret
    End Function

    Public Function put_SelfTrigger(ByRef pSt As SelfTrigger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_SelfTrigger(handle_, pSt))
    End Function

    Public Function get_SelfTrigger(ByRef pSt As SelfTrigger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_SelfTrigger(handle_, pSt))
    End Function

    Public Function put_Linear(v8 As Byte(), v16 As UShort()) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Linear(handle_, v8, v16))
    End Function

    Public Function put_Curve(v8 As Byte(), v16 As UShort()) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Curve(handle_, v8, v16))
    End Function

    Public Function put_ColorMatrix(v As Double()) As Boolean
        If v.Length <> 9 Then
            Return False
        End If
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_ColorMatrix(handle_, v))
    End Function

    Public Function put_InitWBGain(v As UShort()) As Boolean
        If v.Length <> 3 Then
            Return False
        End If
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_InitWBGain(handle_, v))
    End Function

    ' get the temperature of the sensor, in 0.1 degrees Celsius (32 means 3.2 degrees Celsius, -35 means -3.5 degree Celsius)
    Public Function get_Temperature(ByRef pTemperature As Short) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_Temperature(handle_, pTemperature))
    End Function

    ' set the target temperature of the sensor or TEC, in 0.1 degrees Celsius (32 means 3.2 degrees Celsius, -35 means -3.5 degree Celsius)
    ' set "-2730" or below means using the default value of this model
    Public Function put_Temperature(nTemperature As Short) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Temperature(handle_, nTemperature))
    End Function

    Public Function get_Roi(ByRef pxOffset As UInteger, ByRef pyOffset As UInteger, ByRef pxWidth As UInteger, ByRef pyHeight As UInteger) As Boolean
        pxOffset = pyOffset = pxWidth = pyHeight = 0
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_Roi(handle_, pxOffset, pyOffset, pxWidth, pyHeight))
    End Function

    '
    ' xOffset, yOffset, xWidth, yHeight: must be even numbers
    '
    Public Function put_Roi(xOffset As UInteger, yOffset As UInteger, xWidth As UInteger, yHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_Roi(handle_, xOffset, yOffset, xWidth, yHeight))
    End Function

    '
    ' multiple Roi
    '
    Public Function put_RoiN(xOffset As UInteger(), yOffset As UInteger(), xWidth As UInteger(), yHeight As UInteger()) As Boolean
        If xOffset.Length <> yHeight.Length Or xOffset.Length <> xWidth.Length Or xOffset.Length <> yHeight.Length Then
            Return False
        End If
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_RoiN(handle_, xOffset, yOffset, xWidth, yHeight, xOffset.Length))
    End Function

    Public Function put_XY(x As Integer, y As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_XY(handle_, x, y))
    End Function

    ' get the actual frame rate of the camera at the most recent time (about a few seconds):
    ' framerate (fps) = nFrame * 1000.0 / nTime
    Public Function get_FrameRate(ByRef nFrame As UInteger, ByRef nTime As UInteger, ByRef nTotalFrame As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_FrameRate(handle_, nFrame, nTime, nTotalFrame))
    End Function

    ' Auto White Balance "Once", Temp/Tint Mode
    Public Function AwbOnce() As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_AwbOnce(handle_, IntPtr.Zero, IntPtr.Zero))
    End Function

    ' Auto White Balance "Once", RGB Gain Mode
    Public Function AwbInit() As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_AwbInit(handle_, IntPtr.Zero, IntPtr.Zero))
    End Function

    Public Function AbbOnce() As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_AbbOnce(handle_, IntPtr.Zero, IntPtr.Zero))
    End Function

    Public Function FfcOnce() As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_FfcOnce(handle_))
    End Function

    Public Function DfcOnce() As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_DfcOnce(handle_))
    End Function

    Public Function FpncOnce() As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_FpncOnce(handle_))
    End Function

    Public Function FfcExport(filePath As String) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_FfcExport(handle_, filePath))
    End Function

    Public Function FfcImport(filePath As String) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_FfcImport(handle_, filePath))
    End Function

    Public Function DfcExport(filePath As String) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_DfcExport(handle_, filePath))
    End Function

    Public Function DfcImport(filePath As String) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_DfcImport(handle_, filePath))
    End Function

    Public Function IoControl(ioLine As UInteger, eType As eIoControType, inVal As Integer, ByRef outVal As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_IoControl(handle_, ioLine, eType, inVal, outVal))
    End Function

    Public Function IoControl(ioLine As UInteger, eType As eIoControType, inVal As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Dim outVal As Integer = 0
        Return CheckHResult(Toupcam_IoControl(handle_, ioLine, eType, inVal, outVal))
    End Function

    Public Function AAF(action As eAAF, inVal As Integer, ByRef outVal As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_AAF(handle_, action, inVal, outVal))
    End Function

    Public Function AAF(action As eAAF, inVal As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Dim outVal As Integer = 0
        Return CheckHResult(Toupcam_AAF(handle_, action, inVal, outVal))
    End Function

    Private Function Ptr2LensInfo(ByRef pInfo As LensInfo, p As IntPtr) As Boolean
        Dim saveptr As IntPtr = p

        pInfo.lensID = CType(Marshal.ReadInt16(p), UShort)
        p = IncIntPtr(p, 2)
        pInfo.lensType = Marshal.ReadByte(p)
        p = IncIntPtr(p, 1)
        pInfo.statusAfmf = Marshal.ReadByte(p)       ' LENS_AF = 0x00,  LENS_MF = 0x80
        p = IncIntPtr(p, 1)

        pInfo.maxFocalLength = CType(Marshal.ReadInt16(p), UShort)
        p = IncIntPtr(p, 2)
        pInfo.curFocalLength = CType(Marshal.ReadInt16(p), UShort)
        p = IncIntPtr(p, 2)
        pInfo.minFocalLength = CType(Marshal.ReadInt16(p), UShort)
        p = IncIntPtr(p, 2)

        pInfo.farFM = Marshal.ReadInt16(p)           ' focus motor, absolute value
        p = IncIntPtr(p, 2)
        pInfo.curFM = Marshal.ReadInt16(p)           ' current focus motor
        p = IncIntPtr(p, 2)
        pInfo.nearFM = Marshal.ReadInt16(p)
        p = IncIntPtr(p, 2)

        pInfo.maxFocusDistance = CType(Marshal.ReadInt16(p), UShort)
        p = IncIntPtr(p, 2)
        pInfo.minFocusDistance = CType(Marshal.ReadInt16(p), UShort)
        p = IncIntPtr(p, 2)

        pInfo.curAM = CType(Marshal.ReadByte(p), SByte)
        p = IncIntPtr(p, 1)
        pInfo.maxAM = Marshal.ReadByte(p)            ' maximum Aperture, mimimum F#
        p = IncIntPtr(p, 1)
        pInfo.minAM = Marshal.ReadByte(p)            ' mimimum Aperture, maximum F#
        p = IncIntPtr(p, 1)
        pInfo.posAM = Marshal.ReadByte(p)            ' used For Set aperture motor To posAM, it Is an index
        p = IncIntPtr(p, 1)
        pInfo.posFM = Marshal.ReadInt32(p)
        p = IncIntPtr(p, 4)

        Dim sizeFN As Integer = Marshal.ReadInt32(p)
        p = IncIntPtr(p, 4)

        If (sizeFN >= 0) Then
            Dim sp As IntPtr = Marshal.ReadIntPtr(p)
            If (sp <> IntPtr.Zero) Then
                pInfo.arrayFN = New String(sizeFN - 1) {}
                For i As Integer = 0 To sizeFN - 1
                    pInfo.arrayFN(i) = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(sp))
                    sp = IncIntPtr(sp, IntPtr.Size)
                Next
            End If
        End If

        p = IncIntPtr(p, IntPtr.Size)
        pInfo.lensName = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(p))
        Marshal.FreeHGlobal(saveptr)
        Return True
    End Function

    Public Function get_LensInfo(ByRef pInfo As LensInfo) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Dim p As IntPtr = Marshal.AllocHGlobal(128)
        If (Not CheckHResult(Toupcam_get_LensInfo(handle_, p))) Then
            Marshal.FreeHGlobal(p)
            Return False
        End If
        Return Ptr2LensInfo(pInfo, p)
    End Function

    Public Function get_AFState(ByRef pState As AFState) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_AFState(handle_, pState))
    End Function

    Public Function put_AFMode(mode As eAFMode) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_AFMode(handle_, mode))
    End Function

    Public Function put_AFRoi(xOffset As UInteger, yOffset As UInteger, xWidth As UInteger, yHeight As UInteger) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_AFRoi(handle_, xOffset, yOffset, xWidth, yHeight))
    End Function

    Public Function put_AFAperture(iAperture As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_AFAperture(handle_, iAperture))
    End Function

    Public Function put_AFFMPos(iFMPos As Integer) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_put_AFFMPos(handle_, iFMPos))
    End Function

    Public Function get_FocusMotor(ByRef pFocusMotor As FocusMotor) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        Return CheckHResult(Toupcam_get_FocusMotor(handle_, pFocusMotor))
    End Function

    Public Function GetHistogram(funHistogramV1 As DelegateHistogramCallback) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        funHistogramV1_ = funHistogramV1
        pHistogramV1_ = New HISTOGRAM_CALLBACKV1(AddressOf HistogramCallbackV1)
        Return CheckHResult(Toupcam_GetHistogram(handle_, pHistogramV1_, id_))
    End Function

    Public Function GetHistogramV2(funHistogramV2 As DelegateHistogramCallbackV2) As Boolean
        If handle_ Is Nothing OrElse handle_.IsInvalid OrElse handle_.IsClosed Then
            Return False
        End If
        funHistogramV2_ = funHistogramV2
        pHistogramV2_ = New HISTOGRAM_CALLBACKV2(AddressOf HistogramCallbackV2)
        Return CheckHResult(Toupcam_GetHistogramV2(handle_, pHistogramV2_, id_))
    End Function

    '
    '   calculate the clarity factor:
    '   pImageData: pointer to the image data
    '   bits: 8(Grey), 16(Grey), 24(RGB24), 32(RGB32), 48(RGB48), 64(RGB64)
    '   nImgWidth, nImgHeight: the image width and height
    '   xOffset, yOffset, xWidth, yHeight: the Roi used to calculate. If not specified, use 1/5 * 1/5 rectangle in the center
    '   return < 0.0 when error
    '
    Public Shared Function calcClarityFactor(pImageData As IntPtr, bits As Integer, nImgWidth As UInteger, nImgHeight As UInteger) As Double
        Return Toupcam_calc_ClarityFactor(pImageData, bits, nImgWidth, nImgHeight)
    End Function
    Public Shared Function calcClarityFactorV2(pImageData As IntPtr, bits As Integer, nImgWidth As UInteger, nImgHeight As UInteger, xOffset As UInteger, yOffset As UInteger, xWidth As UInteger, yHeight As UInteger) As Double
        Return Toupcam_calc_ClarityFactorV2(pImageData, bits, nImgWidth, nImgHeight, xOffset, yOffset, xWidth, yHeight)
    End Function

    Public Shared Sub deBayerV2(nBayer As UInteger, nW As Integer, nH As Integer, input As IntPtr, output As IntPtr, nBitDepth As Byte, nBitCount As Byte)
        Toupcam_deBayerV2(nBayer, nW, nH, input, output, nBitDepth, nBitCount)
    End Sub

    '
    '   simulate replug:
    '   return > 0, the number of device has been replug
    '   return = 0, no device found
    '   return E_ACCESSDENIED if without UAC Administrator privileges
    '   for each device found, it will take about 3 seconds
    '
    Public Shared Function Replug(camId As String) As Integer
        Return Toupcam_Replug(camId)
    End Function

    ' firmware update
    '   camId: camera ID
    '   filePath: ufw file full path
    '   pFun, pCtx: progress percent callback
    ' Please do Not unplug the camera Or lost power during the upgrade process, this Is very very important.
    ' Once an unplugging Or power outage occurs during the upgrade process, the camera will no longer be available And can only be returned To the factory For repair.
    '
    Public Shared Function Update(camId As String, filePath As String, funProgess As DelegateProgressCallback) As Integer
        Dim pProgess As PROGRESS_CALLBACK = New PROGRESS_CALLBACK(AddressOf ProgressCallback)
        Dim id As IntPtr = New IntPtr(Interlocked.Increment(sid_))
        map_.Add(id.ToInt32, funProgess)
        Dim ret As Integer = Toupcam_Update(camId, filePath, pProgess, id)
        map_.Remove(id)
        Return ret
    End Function

    Private Shared Function toModelV2(q As IntPtr) As ModelV2
        Dim model As ModelV2 = New ModelV2
        model.name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(q))
        q = IncIntPtr(q, IntPtr.Size)
        If (4 = IntPtr.Size) Then
            q = IncIntPtr(q, 4)
        End If
        model.flag = Marshal.ReadInt64(q)
        q = IncIntPtr(q, 8)
        model.maxspeed = CUInt(Marshal.ReadInt32(q))
        q = IncIntPtr(q, 4)
        model.preview = CUInt(Marshal.ReadInt32(q))
        q = IncIntPtr(q, 4)
        model.still = CUInt(Marshal.ReadInt32(q))
        q = IncIntPtr(q, 4)
        model.maxfanspeed = CUInt(Marshal.ReadInt32(q))
        q = IncIntPtr(q, 4)
        model.ioctrol = CUInt(Marshal.ReadInt32(q))
        q = IncIntPtr(q, 4)
        Dim tmp As Single() = New Single(0) {}
        Marshal.Copy(q, tmp, 0, 1)
        model.xpixsz = tmp(0)
        q = IncIntPtr(q, 4)
        Marshal.Copy(q, tmp, 0, 1)
        model.ypixsz = tmp(0)
        q = IncIntPtr(q, 4)
        Dim resn As UInteger = Math.Max(model.preview, model.still)
        model.res = New Resolution(resn - 1) {}
        For j As UInteger = 0 To resn - 1
            model.res(j).width = CUInt(Marshal.ReadInt32(q))
            q = IncIntPtr(q, 4)
            model.res(j).height = CUInt(Marshal.ReadInt32(q))
            q = IncIntPtr(q, 4)
        Next
        Return model
    End Function

    Public Shared Function getModel(idVendor As UShort, idProduct As UShort) As ModelV2
        Return toModelV2(Toupcam_get_Model(idVendor, idProduct))
    End Function

    Public Shared Function allModel() As ModelV2()
        Dim a As List(Of ModelV2) = New List(Of ModelV2)()
        Dim p As IntPtr = Toupcam_all_Model()
        Do While True
            Dim q As IntPtr = Marshal.ReadIntPtr(p)
            If q = IntPtr.Zero Then
                Exit Do
            End If
            a.Add(toModelV2(q))
            p = IncIntPtr(p, IntPtr.Size)
        Loop
        Return a.ToArray()
    End Function

    Public Shared Function Gain2TempTint(gain As Integer(), ByRef temp As Integer, ByRef tint As Integer) As Integer
        If gain.Length <> 3 Then
            Return E_INVALIDARG
        End If
        Return Toupcam_Gain2TempTint(gain, temp, tint)
    End Function

    Public Shared Sub TempTint2Gain(temp As Integer, tint As Integer, gain As Integer())
        Toupcam_TempTint2Gain(temp, tint, gain)
    End Sub

    Public Shared Function PixelFormatName(pixelFormat As Integer) As String
        Dim ptr As IntPtr = Toupcam_get_PixelFormatName(pixelFormat)
        If IntPtr.Zero = ptr Then
            Return Nothing
        End If
        Return Marshal.PtrToStringAnsi(ptr)
    End Function
    
    Public Shared Function FfcFile(data() As IntPtr, num As UInteger, width As UInteger, height As UInteger, fourcc As UInteger, bits As UInteger, outfile As String) As Integer
        Return Toupcam_FfcFile(data, num, width, height, fourcc, bits, outfile)
    End Function

    Public Shared Function HResult2String(hResult As Integer) As String
        Select Case (hResult)
            Case S_OK
                Return "Success"
            Case S_FALSE ' Remark: Different from S_OK, such as internal values and user-set values have coincided, equivalent to noop
                Return "Yet another success"
            Case E_INVALIDARG
                Return "One or more arguments are not valid"
            Case E_NOTIMPL ' Remark: This feature is not supported on this model of camera
                Return "Not supported or not implemented"
            Case E_POINTER ' Remark: Pointer is NULL
                Return "Pointer that is not valid"
            Case E_UNEXPECTED ' Remark: Generally indicates that the conditions are not met, such as calling put_Option setting some options that do not support modification when the camera is running, and so on
                Return "Catastrophic failure"
            Case E_ACCESSDENIED ' Remark: The program on Linux does not have permission to open the USB device, please enable udev rules file or run as root
                Return "General access denied error"
            Case E_OUTOFMEMORY
                Return "Out of memory"
            Case E_WRONG_THREAD
                Return "Call function in the wrong thread"
            Case E_GEN_FAILURE ' Remark: It is generally caused by hardware errors, such as cable problems, USB port problems, poor contact, camera hardware damage, etc
                Return "Device not functioning"
            Case E_PENDING ' Remark: No data is available at this time
                Return "The data necessary to complete this operation is not yet available"
            Case E_BUSY ' Remark: The camera is already in use, such as duplicated opening/starting the camera, or being used by other application, etc
                Return "The requested resource is in use"
            Case E_TIMEOUT
                Return "This operation returned because the timeout period expired"
            case E_UNREACH
                Return "Network is unreachable"
            Case Else
                Return "Unspecified failure"
        End Select
    End Function

    Private Shared sid_ As Integer = 0
    Private Shared map_ As Dictionary(Of Integer, Object) = New Dictionary(Of Integer, Object)()
    Private handle_ As SafeCamHandle
    Private id_ As IntPtr
    Private funDataV4_ As DelegateDataCallbackV4
    <Obsolete>
    Private funDataV3_ As DelegateDataCallbackV3
    Private funEvent_ As DelegateEventCallback
    Private funHistogramV1_ As DelegateHistogramCallback
    Private funHistogramV2_ As DelegateHistogramCallbackV2
    Private pDataV4_ As DATA_CALLBACK_V4
    Private pDataV3_ As DATA_CALLBACK_V3
    Private pEvent_ As EVENT_CALLBACK
    Private pHistogramV1_ As HISTOGRAM_CALLBACKV1
    Private pHistogramV2_ As HISTOGRAM_CALLBACKV2
    Private hResult_ As Integer

    Protected Overrides Sub Finalize()
        Try
            Dispose(False)
        Finally
            MyBase.Finalize()
        End Try
    End Sub

#If Not (NETFX_CORE OrElse NETCOREAPP OrElse WINDOWS_UWP) Then
    <SecurityPermission(SecurityAction.Demand, UnmanagedCode:=True)>
    Protected Overridable Sub Dispose(disposing As Boolean)
#Else
    Protected Overridable Sub Dispose(disposing As Boolean)
#End If
        ' Note there are three interesting states here:
        ' 1) CreateFile failed, handle_ contains an invalid handle
        ' 2) We called Dispose already, handle_ is closed.
        ' 3) handle_ is null, due to an async exception before
        '    calling CreateFile. Note that the finalizer runs
        '    if the constructor fails.
        If handle_ IsNot Nothing AndAlso Not handle_.IsInvalid Then
            ' Free the handle
            handle_.Dispose()
        End If
        ' SafeHandle records the fact that we've called Dispose.
    End Sub

    '
    '   the object of Toupcam must be obtained by static mothod Open or OpenByIndex, it cannot be obtained by obj = New Toupcam (The constructor is private on purpose)
    '
    Private Sub New(h As SafeCamHandle)
        handle_ = h
        id_ = New IntPtr(Interlocked.Increment(sid_))
        map_.Add(id_.ToInt32(), Me)
    End Sub

    Private Function CheckHResult(r As Integer) As Boolean
        hResult_ = r
        Return (hResult_ >= 0)
    End Function

    Private Sub DataCallbackV4(pData As IntPtr, pInfo As IntPtr, bSnap As Boolean)
        If pData = IntPtr.Zero OrElse pInfo = IntPtr.Zero Then
            ' pData == 0 means that something error, we callback to tell the application
            If funDataV4_ IsNot Nothing Then
                Dim info As New FrameInfoV3()
                funDataV4_(IntPtr.Zero, info, bSnap)
            End If
        Else
#If Not (NETFX_CORE OrElse NETCOREAPP OrElse WINDOWS_UWP) Then
            Dim info As FrameInfoV3 = CType(Marshal.PtrToStructure(pInfo, GetType(FrameInfoV3)), FrameInfoV3)
#Else
            Dim info As FrameInfoV3 = Marshal.PtrToStructure(Of FrameInfoV3)(pInfo)
#End If
            funDataV4_(pData, info, bSnap)
        End If
    End Sub

    <Obsolete>
    Private Sub DataCallbackV3(pData As IntPtr, pInfo As IntPtr, bSnap As Boolean)
        If pData = IntPtr.Zero OrElse pInfo = IntPtr.Zero Then
            ' pData == 0 means that something error, we callback to tell the application
            If funDataV3_ IsNot Nothing Then
                Dim info As New FrameInfoV2()
                funDataV3_(IntPtr.Zero, info, bSnap)
            End If
        Else
#If Not (NETFX_CORE OrElse NETCOREAPP OrElse WINDOWS_UWP) Then
            Dim info As FrameInfoV2 = CType(Marshal.PtrToStructure(pInfo, GetType(FrameInfoV2)), FrameInfoV2)
#Else
            Dim info As FrameInfoV2 = Marshal.PtrToStructure(Of FrameInfoV2)(pInfo)
#End If
            funDataV3_(pData, info, bSnap)
        End If
    End Sub

    Private Shared Sub DataCallbackV4(pData As IntPtr, pInfo As IntPtr, bSnap As Boolean, ctxData As IntPtr)
        Dim pthis As Toupcam = Nothing
        map_.TryGetValue(ctxData.ToInt32(), pthis)
        If pthis IsNot Nothing Then
            pthis.DataCallbackV4(pData, pInfo, bSnap)
        End If
    End Sub

    <Obsolete>
    Private Shared Sub DataCallbackV3(pData As IntPtr, pInfo As IntPtr, bSnap As Boolean, ctxData As IntPtr)
        Dim pthis As Toupcam = Nothing
        map_.TryGetValue(ctxData.ToInt32(), pthis)
        If pthis IsNot Nothing Then
            pthis.DataCallbackV3(pData, pInfo, bSnap)
        End If
    End Sub

    Private Shared Sub EventCallback(nEvent As eEVENT, ctxEvent As IntPtr)
        Dim pthis As Toupcam = Nothing
        map_.TryGetValue(ctxEvent.ToInt32(), pthis)
        If pthis IsNot Nothing And pthis.funEvent_ IsNot Nothing Then
            pthis.funEvent_(nEvent)
        End If
    End Sub

    Private Shared Sub HistogramCallbackV1(aHistY As IntPtr, aHistR As IntPtr, aHistG As IntPtr, aHistB As IntPtr, ctxHistogramV1 As IntPtr)
        Dim pthis As Toupcam = Nothing
        map_.TryGetValue(ctxHistogramV1.ToInt32(), pthis)
        If pthis IsNot Nothing Then
            If pthis.funHistogramV1_ IsNot Nothing Then
                Dim arrHistY As Single() = New Single(255) {}
                Dim arrHistR As Single() = New Single(255) {}
                Dim arrHistG As Single() = New Single(255) {}
                Dim arrHistB As Single() = New Single(255) {}
                Marshal.Copy(aHistY, arrHistY, 0, 256)
                Marshal.Copy(aHistR, arrHistR, 0, 256)
                Marshal.Copy(aHistG, arrHistG, 0, 256)
                Marshal.Copy(aHistB, arrHistB, 0, 256)

                pthis.funHistogramV1_(arrHistY, arrHistR, arrHistG, arrHistB)
                pthis.funHistogramV1_ = Nothing
            End If
            pthis.pHistogramV1_ = Nothing
        End If
    End Sub

    Private Shared Sub HistogramCallbackV2(aHist As IntPtr, nFlag As UInteger, ctxHistogramV2 As IntPtr)
        Dim pthis As Toupcam = Nothing
        map_.TryGetValue(ctxHistogramV2.ToInt32(), pthis)
        If pthis IsNot Nothing Then
            Dim arraySize As Integer = 1 << (nFlag And &HF)
            If (nFlag And &H8000) = 0 Then
                arraySize = arraySize * 3
            End If
            Dim arrHist As Integer() = New Integer(arraySize - 1) {}
            Marshal.Copy(aHist, arrHist, 0, arraySize)

            If pthis.funHistogramV2_ IsNot Nothing Then
                pthis.funHistogramV2_(arrHist)
            End If
        End If
    End Sub

    Private Shared Sub HotplugCallback(ctxHotplug As IntPtr)
        Dim obj As Object = Nothing
        map_.TryGetValue(ctxHotplug.ToInt32(), obj)
        Dim funHotplug As DelegateHotplugCallback = TryCast(obj, DelegateHotplugCallback)
        If funHotplug IsNot Nothing Then
            funHotplug()
        End If
    End Sub

    Private Shared Sub ProgressCallback(percent As Integer, ctxProgress As IntPtr)
        Dim obj As Object = Nothing
        map_.TryGetValue(ctxProgress.ToInt32(), obj)
        Dim funProgress As DelegateProgressCallback = TryCast(obj, DelegateProgressCallback)
        If funProgress IsNot Nothing Then
            funProgress(percent)
        End If
    End Sub

#If Not (NETFX_CORE OrElse NETCOREAPP OrElse WINDOWS_UWP) Then
    Private Class SafeCamHandle
        Inherits SafeHandleZeroOrMinusOneIsInvalid
        <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
        Private Shared Sub Toupcam_Close(h As IntPtr)
        End Sub

        Public Sub New()
            MyBase.New(True)
        End Sub

        <ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)>
        Protected Overrides Function ReleaseHandle() As Boolean
            ' Here, we must obey all rules for constrained execution regions.
            Toupcam_Close(handle)
            Return True
        End Function
    End Class
#Else
    Private Class SafeCamHandle
        Inherits SafeHandle
        <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
        Private Shared Sub Toupcam_Close(h As IntPtr)
        End Sub
        
        Public Sub New()
            MyBase.New(IntPtr.Zero, True)
        End Sub
        
        Protected Overrides Function ReleaseHandle() As Boolean
            Toupcam_Close(handle)
            Return True
        End Function
        
        Public Overrides ReadOnly Property IsInvalid() As Boolean
            Get
                Return MyBase.handle = IntPtr.Zero
            End Get
        End Property
    End Class
#End If

    <UnmanagedFunctionPointerAttribute(CallingConvention.Winapi)>
    Private Delegate Sub EVENT_CALLBACK(nEvent As eEVENT, pCtx As IntPtr)
    <UnmanagedFunctionPointerAttribute(CallingConvention.Winapi)>
    Private Delegate Sub DATA_CALLBACK_V4(pData As IntPtr, pInfo As IntPtr, bSnap As Boolean, pCtx As IntPtr)
    <UnmanagedFunctionPointerAttribute(CallingConvention.Winapi)>
    Private Delegate Sub DATA_CALLBACK_V3(pData As IntPtr, pInfo As IntPtr, bSnap As Boolean, pCtx As IntPtr)
    <UnmanagedFunctionPointerAttribute(CallingConvention.Winapi)>
    Private Delegate Sub HISTOGRAM_CALLBACKV1(aHistY As IntPtr, aHistR As IntPtr, aHistG As IntPtr, aHistB As IntPtr, pCtx As IntPtr)
    <UnmanagedFunctionPointerAttribute(CallingConvention.Winapi)>
    Private Delegate Sub HISTOGRAM_CALLBACKV2(aHist As IntPtr, nFlag As UInteger, pCtx As IntPtr)
    <UnmanagedFunctionPointerAttribute(CallingConvention.Winapi)>
    Private Delegate Sub PROGRESS_CALLBACK(percent As Integer, pCtx As IntPtr)
    <UnmanagedFunctionPointerAttribute(CallingConvention.Winapi)>
    Private Delegate Sub HOTPLUG_CALLBACK(pCtx As IntPtr)

    <StructLayout(LayoutKind.Sequential)>
    Private Structure RECT
        Public left As Integer, top As Integer, right As Integer, bottom As Integer
    End Structure

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_Version() As IntPtr
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_EnumV2(ti As IntPtr) As UInteger
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_EnumWithName(ti As IntPtr) As UInteger
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_Open(<MarshalAs(UnmanagedType.LPWStr)> camId As String) As SafeCamHandle
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_OpenByIndex(index As UInteger) As SafeCamHandle
    End Function
#If Not (NETFX_CORE OrElse NETCOREAPP OrElse WINDOWS_UWP) Then
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_StartPullModeWithWndMsg(h As SafeCamHandle, hWnd As IntPtr, nMsg As UInteger) As Integer
    End Function
#End If
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_StartPullModeWithCallback(h As SafeCamHandle, funEvent As EVENT_CALLBACK, ctxEvent As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_PullImageV4(h As SafeCamHandle, pImageData As IntPtr, bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_PullImageV4(h As SafeCamHandle, pImageData As Byte(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_PullImageV4(h As SafeCamHandle, pImageData As UShort(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_PullImageV3(h As SafeCamHandle, pImageData As IntPtr, bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_PullImageV3(h As SafeCamHandle, pImageData As Byte(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_PullImageV3(h As SafeCamHandle, pImageData As UShort(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_WaitImageV4(h As SafeCamHandle, nWaitMS As UInteger, pImageData As IntPtr, bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_WaitImageV4(h As SafeCamHandle, nWaitMS As UInteger, pImageData As Byte(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_WaitImageV4(h As SafeCamHandle, nWaitMS As UInteger, pImageData As UShort(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_WaitImageV3(h As SafeCamHandle, nWaitMS As UInteger, pImageData As IntPtr, bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_WaitImageV3(h As SafeCamHandle, nWaitMS As UInteger, pImageData As Byte(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_WaitImageV3(h As SafeCamHandle, nWaitMS As UInteger, pImageData As UShort(), bStill As Integer, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullImage(h As SafeCamHandle, pImageData As IntPtr, bits As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullImage(h As SafeCamHandle, pImageData As Byte(), bits As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullImage(h As SafeCamHandle, pImageData As UShort(), bits As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullStillImage(h As SafeCamHandle, pImageData As IntPtr, bits As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullStillImage(h As SafeCamHandle, pImageData As Byte(), bits As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullStillImage(h As SafeCamHandle, pImageData As UShort(), bits As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullImageWithRowPitch(h As SafeCamHandle, pImageData As IntPtr, bits As Integer, rowPitch As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullImageWithRowPitch(h As SafeCamHandle, pImageData As Byte(), bits As Integer, rowPitch As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullImageWithRowPitch(h As SafeCamHandle, pImageData As UShort(), bits As Integer, rowPitch As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullStillImageWithRowPitch(h As SafeCamHandle, pImageData As IntPtr, bits As Integer, rowPitch As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullStillImageWithRowPitch(h As SafeCamHandle, pImageData As Byte(), bits As Integer, rowPitch As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullStillImageWithRowPitch(h As SafeCamHandle, pImageData As UShort(), bits As Integer, rowPitch As Integer, ByRef pnWidth As UInteger, ByRef pnHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullImageV2(h As SafeCamHandle, pImageData As IntPtr, bits As Integer, ByRef pInfo As FrameInfoV2) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullImageV2(h As SafeCamHandle, pImageData As Byte(), bits As Integer, ByRef pInfo As FrameInfoV2) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullImageV2(h As SafeCamHandle, pImageData As UShort(), bits As Integer, ByRef pInfo As FrameInfoV2) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullStillImageV2(h As SafeCamHandle, pImageData As IntPtr, bits As Integer, ByRef pInfo As FrameInfoV2) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullStillImageV2(h As SafeCamHandle, pImageData As Byte(), bits As Integer, ByRef pInfo As FrameInfoV2) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullStillImageV2(h As SafeCamHandle, pImageData As UShort(), bits As Integer, ByRef pInfo As FrameInfoV2) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullImageWithRowPitchV2(h As SafeCamHandle, pImageData As IntPtr, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV2) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullImageWithRowPitchV2(h As SafeCamHandle, pImageData As Byte(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV2) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullImageWithRowPitchV2(h As SafeCamHandle, pImageData As UShort(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV2) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullStillImageWithRowPitchV2(h As SafeCamHandle, pImageData As IntPtr, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV2) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullStillImageWithRowPitchV2(h As SafeCamHandle, pImageData As Byte(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV2) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_PullStillImageWithRowPitchV2(h As SafeCamHandle, pImageData As UShort(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV2) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_StartPushModeV4(h As SafeCamHandle, funData As DATA_CALLBACK_V4, ctxData As IntPtr, funEvent As EVENT_CALLBACK, ctxEvent As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_StartPushModeV3(h As SafeCamHandle, funData As DATA_CALLBACK_V3, ctxData As IntPtr, funEvent As EVENT_CALLBACK, ctxEvent As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_Stop(h As SafeCamHandle) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_Pause(h As SafeCamHandle, bPause As Integer) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_Snap(h As SafeCamHandle, nResolutionIndex As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_SnapN(h As SafeCamHandle, nResolutionIndex As UInteger, nNumber As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_SnapR(h As SafeCamHandle, nResolutionIndex As UInteger, nNumber As UInteger) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_Trigger(h As SafeCamHandle, nNumber As UShort) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_TriggerSyncV4(h As SafeCamHandle, nWaitMS As UInteger, pImageData As IntPtr, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_TriggerSyncV4(h As SafeCamHandle, nWaitMS As UInteger, pImageData As Byte(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_TriggerSyncV4(h As SafeCamHandle, nWaitMS As UInteger, pImageData As UShort(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV4) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_TriggerSync(h As SafeCamHandle, nWaitMS As UInteger, pImageData As IntPtr, bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_TriggerSync(h As SafeCamHandle, nWaitMS As UInteger, pImageData As Byte(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi), Obsolete>
    Private Shared Function Toupcam_TriggerSync(h As SafeCamHandle, nWaitMS As UInteger, pImageData As UShort(), bits As Integer, rowPitch As Integer, ByRef pInfo As FrameInfoV3) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Size(h As SafeCamHandle, nWidth As Integer, nHeight As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Size(h As SafeCamHandle, ByRef nWidth As Integer, ByRef nHeight As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_eSize(h As SafeCamHandle, nResolutionIndex As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_eSize(h As SafeCamHandle, ByRef nResolutionIndex As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_FinalSize(h As SafeCamHandle, ByRef nWidth As Integer, ByRef nHeight As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_ResolutionNumber(h As SafeCamHandle) As UInteger
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Resolution(h As SafeCamHandle, nResolutionIndex As UInteger, ByRef pWidth As Integer, ByRef pHeight As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_ResolutionRatio(h As SafeCamHandle, nResolutionIndex As UInteger, ByRef pNumerator As Integer, ByRef pDenominator As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Field(h As SafeCamHandle) As UInteger
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_RawFormat(h As SafeCamHandle, ByRef nFourCC As UInteger, ByRef bitdepth As UInteger) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_RealTime(h As SafeCamHandle, val As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_RealTime(h As SafeCamHandle, ByRef val As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_Flush(h As SafeCamHandle) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Temperature(h As SafeCamHandle, ByRef pTemperature As Short) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Temperature(h As SafeCamHandle, nTemperature As Short) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Roi(h As SafeCamHandle, ByRef pxOffset As UInteger, ByRef pyOffsett As UInteger, ByRef pxWidtht As UInteger, ByRef pyHeightt As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Roi(h As SafeCamHandle, xOffsett As UInteger, yOffset As UInteger, xWidth As UInteger, yHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_RoiN(h As SafeCamHandle, xOffset As UInteger(), yOffset As UInteger(), xWidth As UInteger(), yHeight As UInteger(), nNum As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_XY(h As SafeCamHandle, x As Integer, y As Integer) As Integer
    End Function

    '
    '  ------------------------------------------------------------------|
    '  | Parameter               |   Range       |   Default             |
    '  |-----------------------------------------------------------------|
    '  | Auto Exposure Target    |   16~235      |   120                 |
    '  | Exposure Gain           |   100~        |   100                 |
    '  | Temp                    |   1000~25000  |   6503                |
    '  | Tint                    |   100~2500    |   1000                |
    '  | LevelRange              |   0~255       |   Low = 0, High = 255 |
    '  | Contrast                |   -255~255    |   0                   |
    '  | Hue                     |   -180~180    |   0                   |
    '  | Saturation              |   0~255       |   128                 |
    '  | Brightness              |   -255~255    |   0                   |
    '  | Gamma                   |   20~180      |   100                 |
    '  | WBGain                  |   -127~127    |   0                   |
    '  ------------------------------------------------------------------|
    '
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_AutoExpoEnable(h As SafeCamHandle, ByRef bAutoExposure As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_AutoExpoEnable(h As SafeCamHandle, bAutoExposure As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_AutoExpoTarget(h As SafeCamHandle, ByRef Target As UShort) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_AutoExpoTarget(h As SafeCamHandle, Target As UShort) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_AutoExpoRange(h As SafeCamHandle, maxTime As UInteger, minTime As UInteger, maxGain As UShort, minGain As UShort) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_AutoExpoRange(h As SafeCamHandle, ByRef maxTime As UInteger, ByRef minTime As UInteger, ByRef maxGain As UShort, ByRef minGain As UShort) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_MaxAutoExpoTimeAGain(h As SafeCamHandle, maxTime As UInteger, maxGain As UShort) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_MaxAutoExpoTimeAGain(h As SafeCamHandle, ByRef maxTime As UInteger, ByRef maxGain As UShort) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_MinAutoExpoTimeAGain(h As SafeCamHandle, minTime As UInteger, minGain As UShort) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_MinAutoExpoTimeAGain(h As SafeCamHandle, ByRef minTime As UInteger, ByRef minGain As UShort) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_ExpoTime(h As SafeCamHandle, ByRef Time As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_ExpoTime(h As SafeCamHandle, Time As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_RealExpoTime(h As SafeCamHandle, ByRef Time As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_ExpTimeRange(h As SafeCamHandle, ByRef nMin As UInteger, ByRef nMax As UInteger, ByRef nDef As UInteger) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_ExpoAGain(h As SafeCamHandle, ByRef Gain As UShort) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_ExpoAGain(h As SafeCamHandle, Gain As UShort) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_ExpoAGainRange(h As SafeCamHandle, ByRef nMin As UShort, ByRef nMax As UShort, ByRef nDef As UShort) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_LevelRange(h As SafeCamHandle, aLow As UShort(), aHigh As UShort()) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_LevelRange(h As SafeCamHandle, aLow As UShort(), aHigh As UShort()) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_LevelRangeV2(h As SafeCamHandle, mode As UShort, ByRef pRoiRect As RECT, aLow As UShort(), aHigh As UShort()) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_LevelRangeV2(h As SafeCamHandle, ByRef pMode As UShort, ByRef pRoiRect As RECT, aLow As UShort(), aHigh As UShort()) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Hue(h As SafeCamHandle, Hue As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Hue(h As SafeCamHandle, ByRef Hue As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Saturation(h As SafeCamHandle, Saturation As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Saturation(h As SafeCamHandle, ByRef Saturation As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Brightness(h As SafeCamHandle, Brightness As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Brightness(h As SafeCamHandle, ByRef Brightness As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Contrast(h As SafeCamHandle, ByRef Contrast As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Contrast(h As SafeCamHandle, Contrast As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Gamma(h As SafeCamHandle, ByRef Gamma As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Gamma(h As SafeCamHandle, Gamma As Integer) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Chrome(h As SafeCamHandle, ByRef bChrome As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Chrome(h As SafeCamHandle, bChrome As Integer) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_VFlip(h As SafeCamHandle, ByRef bVFlip As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_VFlip(h As SafeCamHandle, bVFlip As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_HFlip(h As SafeCamHandle, ByRef bHFlip As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_HFlip(h As SafeCamHandle, bHFlip As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Negative(h As SafeCamHandle, ByRef bNegative As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Negative(h As SafeCamHandle, bNegative As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Speed(h As SafeCamHandle, nSpeed As UShort) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Speed(h As SafeCamHandle, ByRef pSpeed As UShort) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_MaxSpeed(h As SafeCamHandle) As UInteger
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_MaxBitDepth(h As SafeCamHandle) As UInteger
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_FanMaxSpeed(h As SafeCamHandle) As UInteger
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_HZ(h As SafeCamHandle, nHZ As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_HZ(h As SafeCamHandle, ByRef nHZ As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Mode(h As SafeCamHandle, bSkip As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Mode(h As SafeCamHandle, ByRef bSkip As Integer) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_TempTint(h As SafeCamHandle, nTemp As Integer, nTint As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_TempTint(h As SafeCamHandle, ByRef nTemp As Integer, ByRef nTint As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_WhiteBalanceGain(h As SafeCamHandle, aGain As Integer()) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_WhiteBalanceGain(h As SafeCamHandle, aGain As Integer()) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_BlackBalance(h As SafeCamHandle, aSub As UShort()) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_BlackBalance(h As SafeCamHandle, aSub As UShort()) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_AWBAuxRect(h As SafeCamHandle, ByRef pAuxRect As RECT) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_AWBAuxRect(h As SafeCamHandle, ByRef pAuxRect As RECT) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_AEAuxRect(h As SafeCamHandle, ByRef pAuxRect As RECT) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_AEAuxRect(h As SafeCamHandle, ByRef pAuxRect As RECT) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_ABBAuxRect(h As SafeCamHandle, ByRef pAuxRect As RECT) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_ABBAuxRect(h As SafeCamHandle, ByRef pAuxRect As RECT) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_MonoMode(h As SafeCamHandle) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_StillResolutionNumber(h As SafeCamHandle) As UInteger
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_StillResolution(h As SafeCamHandle, nResolutionIndex As UInteger, ByRef pWidth As Integer, ByRef pHeight As Integer) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Revision(h As SafeCamHandle, ByRef pRevision As UShort) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_SerialNumber(h As SafeCamHandle, sn As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_FwVersion(h As SafeCamHandle, fwver As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_HwVersion(h As SafeCamHandle, hwver As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_FpgaVersion(h As SafeCamHandle, fpgaver As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_ProductionDate(h As SafeCamHandle, pdate As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_PixelSize(h As SafeCamHandle, nResolutionIndex As UInteger, ByRef x As Single, ByRef y As Single) As Integer
    End Function

    '
    '  ------------------------------------------------------------|
    '  | Parameter         |   Range       |   Default             |
    '  |-----------------------------------------------------------|
    '  | VidgetAmount      |   -100~100    |   0                   |
    '  | VignetMidPoint    |   0~100       |   50                  |
    '  -------------------------------------------------------------
    '
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_VignetEnable(h As SafeCamHandle, bEnable As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_VignetEnable(h As SafeCamHandle, ByRef bEnable As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_VignetAmountInt(h As SafeCamHandle, nAmount As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_VignetAmountInt(h As SafeCamHandle, ByRef nAmount As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_VignetMidPointInt(h As SafeCamHandle, nMidPoint As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_VignetMidPointInt(h As SafeCamHandle, ByRef nMidPoint As Integer) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_AwbOnce(h As SafeCamHandle, funTT As IntPtr, ctxTT As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_AwbInit(h As SafeCamHandle, funWB As IntPtr, ctxWB As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_LevelRangeAuto(h As SafeCamHandle) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_GetHistogram(h As SafeCamHandle, funHistogramV1 As HISTOGRAM_CALLBACKV1, ctxHistogramV1 As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_GetHistogramV2(h As SafeCamHandle, funHistogramV2 As HISTOGRAM_CALLBACKV2, ctxHistogramV2 As IntPtr) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_AbbOnce(h As SafeCamHandle, funBB As IntPtr, ctxBB As IntPtr) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_LEDState(h As SafeCamHandle, iLed As UShort, iState As UShort, iPeriod As UShort) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_write_EEPROM(h As SafeCamHandle, addr As UInteger, pBuffer As IntPtr, nBufferLen As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_write_EEPROM(h As SafeCamHandle, addr As UInteger, pBuffer As Byte(), nBufferLen As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_read_EEPROM(h As SafeCamHandle, addr As UInteger, pBuffer As IntPtr, nBufferLen As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_read_EEPROM(h As SafeCamHandle, addr As UInteger, pBuffer As Byte(), nBufferLen As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_rwc_Flash(h As SafeCamHandle, action As UInteger, addr As UInteger, len As UInteger, pData As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_rwc_Flash(h As SafeCamHandle, action As UInteger, addr As UInteger, len As Integer, pData As Byte()) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_write_Pipe(h As SafeCamHandle, pipeId As UInteger, pBuffer As IntPtr, nBufferLen As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_read_Pipe(h As SafeCamHandle, pipeId As UInteger, pBuffer As IntPtr, nBufferLen As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_feed_Pipe(h As SafeCamHandle, pipeId As UInteger) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_write_UART(h As SafeCamHandle, pBuffer As IntPtr, nBufferLen As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_read_UART(h As SafeCamHandle, pBuffer As IntPtr, nBufferLen As UInteger) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Option(h As SafeCamHandle, iOption As eOPTION, iValue As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Option(h As SafeCamHandle, iOption As eOPTION, ByRef iValue As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_PixelFormatSupport(h As SafeCamHandle, cmd As SByte, ByRef iValue As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_PixelFormatName(val As Integer) As IntPtr
    End Function

    ' Hardware Binning
    ' Value: 1x1, 2x2, etc
    ' Method: Average, Add
    '
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Binning(h As SafeCamHandle, Value As String, Method As String) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Binning(h As SafeCamHandle, ByRef Value As IntPtr, ByRef Method As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_BinningNumber(h As SafeCamHandle) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_BinningValue(h As SafeCamHandle, index As UInteger, ByRef Value As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_BinningMethod(h As SafeCamHandle, index As UInteger, ByRef Method As IntPtr) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_SelfTrigger(h As SafeCamHandle, ByRef pSt As SelfTrigger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_SelfTrigger(h As SafeCamHandle, ByRef pSt As SelfTrigger) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Linear(h As SafeCamHandle, v8 As Byte(), v16 As UShort()) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_Curve(h As SafeCamHandle, v8 As Byte(), v16 As UShort()) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_ColorMatrix(h As SafeCamHandle, v As Double()) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_InitWBGain(h As SafeCamHandle, v As UShort()) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_FrameRate(h As SafeCamHandle, ByRef nFrame As UInteger, ByRef nTime As UInteger, ByRef nTotalFrame As UInteger) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_FfcOnce(h As SafeCamHandle) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_DfcOnce(h As SafeCamHandle) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_FpncOnce(h As SafeCamHandle) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_FfcImport(h As SafeCamHandle, <MarshalAs(UnmanagedType.LPWStr)> filePath As String) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_FfcExport(h As SafeCamHandle, <MarshalAs(UnmanagedType.LPWStr)> filePath As String) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_DfcImport(h As SafeCamHandle, <MarshalAs(UnmanagedType.LPWStr)> filePath As String) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_DfcExport(h As SafeCamHandle, <MarshalAs(UnmanagedType.LPWStr)> filePath As String) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_FpncImport(h As SafeCamHandle, <MarshalAs(UnmanagedType.LPWStr)> filePath As String) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_FpncExport(h As SafeCamHandle, <MarshalAs(UnmanagedType.LPWStr)> filePath As String) As Integer
    End Function
    
    <DllImport("toupcam.dll", CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_FfcFile(<MarshalAs(UnmanagedType.LPArray, ArraySubType:=UnmanagedType.SysUInt)> data() As IntPtr, num As UInteger, width As UInteger, height As UInteger, fourcc As UInteger, bits As UInteger, <MarshalAs(UnmanagedType.LPStr)> ByVal outfile As String) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_IoControl(h As SafeCamHandle, ioLine As UInteger, eType As eIoControType, inVal As Integer, ByRef outVal As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_AAF(h As SafeCamHandle, action As eAAF, inVal As Integer, ByRef outVal As UInteger) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_LensInfo(h As SafeCamHandle, pInfo As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_AFState(h As SafeCamHandle, ByRef pState As AFState) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_AFMode(h As SafeCamHandle, mode As eAFMode) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_AFRoi(h As SafeCamHandle, xOffset As UInteger, yOffset As UInteger, xWidth As UInteger, yHeight As UInteger) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_AFAperture(h As SafeCamHandle, iAperture As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_put_AFFMPos(h As SafeCamHandle, iFMPos As Integer) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_FocusMotor(h As SafeCamHandle, ByRef pFocusMotor As FocusMotor) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_calc_ClarityFactor(pImageData As IntPtr, bits As Integer, nImgWidth As UInteger, nImgHeight As UInteger) As Double
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_calc_ClarityFactorV2(pImageData As IntPtr, bits As Integer, nImgWidth As UInteger, nImgHeight As UInteger, xOffset As UInteger, yOffset As UInteger, xWidth As UInteger, yHeight As UInteger) As Double
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_deBayerV2(nBayer As UInteger, nW As Integer, nH As Integer, input As IntPtr, output As IntPtr, nBitDepth As Byte, nBitCount As Byte)
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_Replug(<MarshalAs(UnmanagedType.LPWStr)> camId As String) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_Update(<MarshalAs(UnmanagedType.LPWStr)> camId As String, <MarshalAs(UnmanagedType.LPWStr)> filePath As String, funProgress As PROGRESS_CALLBACK, ctxProgress As IntPtr) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_GigeEnable(funHotplug As HOTPLUG_CALLBACK, ctxHotplug As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_PciEnable(funHotplug As HOTPLUG_CALLBACK, ctxHotplug As IntPtr) As Integer
    End Function
    <DllImport("toupcam.dll", CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_CtiEnable(funHotplug As HOTPLUG_CALLBACK, ctxHotplug As IntPtr, <MarshalAs(UnmanagedType.LPArray, ArraySubType:=UnmanagedType.LPWStr)> ctiPath() As String) As Integer
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_get_Model(idVendor As UShort, idProduct As UShort) As IntPtr
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_all_Model() As IntPtr
    End Function

    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Function Toupcam_Gain2TempTint(gain As Integer(), ByRef temp As Integer, ByRef tint As Integer) As Integer
    End Function
    <DllImport("toupcam.dll", ExactSpelling:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Shared Sub Toupcam_TempTint2Gain(temp As Integer, tint As Integer, gain As Integer())
    End Sub
End Class
