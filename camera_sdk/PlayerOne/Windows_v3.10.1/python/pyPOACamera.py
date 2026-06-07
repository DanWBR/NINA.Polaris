# -*- coding: utf-8 -*-
from ctypes import *
import numpy as np
from enum import Enum
dll = cdll.LoadLibrary("./PlayerOneCamera.dll") # Windows, if your python is 64bit, please copy dll file from lib\x64 folder, if python is 32bit, copy dll file from lib\x86
#dll = cdll.LoadLibrary("./libPlayerOneCamera.so") # Linux, please copy the 4 'so' files of the corresponding architecture, eg: if your Linux is arm64(aarch64) architecture, please copy the 4 'so' files from lib\arm64
#dll = cdll.LoadLibrary("./libPlayerOneCamera.dylib") # Mac OS, please copy the 4 'dylib' files from 'lib' folder


#************************Type Define************************

class POABayerPattern(Enum):    
    '''Bayer Pattern Definition'''
    POA_BAYER_MONO = -1         # Monochrome, the mono camera with this
    POA_BAYER_RG = 0            # RGGB
    POA_BAYER_BG = 1            # BGGR
    POA_BAYER_GR = 2            # GRBG
    POA_BAYER_GB = 3            # GBRG

class POAImgFormat(Enum):       
    '''Image Data Format Definition'''
    POA_END = -1                # ending in imgFormats[] of POACameraProperties, please ignore this
    POA_RAW8 = 0                # 8bit raw data, 1 pixel 1 byte, value range[0, 255]
    POA_RAW16 = 1               # 16bit raw data, 1 pixel 2 bytes, value range[0, 65535]
    POA_RGB24 = 2               # RGB888 color data, 1 pixel 3 bytes, value range[0, 255] (only color camera)
    POA_MONO8 = 3               # 8bit monochrome data, convert the Bayer Filter Array to monochrome data. 1 pixel 1 byte, value range[0, 255] (only color camera)

class POAErrors(Enum):                      
    '''Return Error Code Definition'''
    POA_OK = 0                              # operation successful
    POA_ERROR_INVALID_INDEX = 1             # invalid index, means the index is < 0 or >= the count( camera or config)
    POA_ERROR_INVALID_ID = 2                # invalid camera ID
    POA_ERROR_INVALID_CONFIG = 3            # invalid POAConfig
    POA_ERROR_INVALID_ARGU = 4              # invalid argument(parameter)
    POA_ERROR_NOT_OPENED = 5                # camera not opened
    POA_ERROR_DEVICE_NOT_FOUND = 6          # camera not found, may be removed
    POA_ERROR_OUT_OF_LIMIT = 7              # the value out of limit
    POA_ERROR_EXPOSURE_FAILED = 8           # camera exposure failed
    POA_ERROR_TIMEOUT = 9                   # timeout
    POA_ERROR_SIZE_LESS = 10                # the data buffer size is not enough
    POA_ERROR_EXPOSING = 11                 # camera is exposing. some operation, must stop exposure first
    POA_ERROR_POINTER = 12                  # invalid pointer, when get some value, do not pass the NULL pointer to the function
    POA_ERROR_CONF_CANNOT_WRITE = 13        # the POAConfig is not writable
    POA_ERROR_CONF_CANNOT_READ = 14         # the POAConfig is not readable
    POA_ERROR_ACCESS_DENIED = 15            # access denied
    POA_ERROR_OPERATION_FAILED = 16         # operation failed, maybe the camera is disconnected suddenly
    POA_ERROR_MEMORY_FAILED = 17            # memory allocation failed

class POACameraState(Enum):         
    '''Camera State Definition'''
    STATE_CLOSED = 0                # camera was closed
    STATE_OPENED = 1                # camera was opened, but not exposing(idle)
    STATE_EXPOSING = 2              # camera is exposing

class POAGuideDirection(Enum):      
    '''ST4 Guide Direction Definition'''
    GUIDE_NORTH = 0                 # guide north, generally,it's DEC+ on the mount
    GUIDE_SOUTH = 1                 # guide south, generally,it's DEC- on the mount
    GUIDE_EAST = 2                  # guide east, generally,it's RA+ on the mount
    GUIDE_WEST = 3                  # guide west, generally,it's RA- on the mount

class POAConfig(Enum):                  
    '''Camera Config Definition'''
    POA_EXPOSURE = 0                    # exposure time(microsecond (us)), range:[10 - 2000000000], recommended to use SetExp_S(), read-write, support auto
    POA_GAIN = 1                        # gain, read-write, support auto
    POA_HARDWARE_BIN = 2                # hardware bin, read-write, On/Off type(bool)
    POA_WB_R = 4                        # red channel of white balance, range:[-1200, 1200], read-write
    POA_WB_G = 5                        # green channel of white balance, range:[-1200, 1200], read-write
    POA_WB_B = 6                        # blue channel of white balance, range:[-1200, 1200], read-write
    POA_OFFSET = 7                      # camera offset, read-write
    POA_AUTOEXPO_MAX_GAIN = 8           # maximum gain when auto-adjust, read-write
    POA_AUTOEXPO_MAX_EXPOSURE = 9       # maximum exposure when auto-adjust(uint: ms), read-write
    POA_AUTOEXPO_BRIGHTNESS = 10        # target brightness when auto-adjust, read-write
    POA_COOLER_POWER = 16               # cooler power percentage[0-100%](only cool camera), read-only
    POA_TARGET_TEMP = 17                # camera target temperature(uint: C), read-write
    POA_COOLER = 18                     # turn cooler(and fan) on or off, read-write, On/Off type(bool)
    POA_HEATER = 19                     # (deprecated)get state of lens heater(on or off), read-only
    POA_HEATER_POWER = 20               # lens heater power percentage[0-100%], read-write
    POA_FAN_POWER = 21                  # radiator fan power percentage[0-100%], read-write
    POA_FRAME_LIMIT = 26                # Frame rate limit, the range:[0, 2000], 0 means no limit, read-write
    POA_HQI = 27                        # High Quality Image, for those cameras without DDR(guide camera), reduce frame rate to improve image quality, read-write, On/Off type(bool)
    POA_USB_BANDWIDTH_LIMIT = 28        # USB bandwidth limit[35-100]%, read-write
    POA_PIXEL_BIN_SUM = 29              # take the sum of pixels after binning, True(1) is sum and False(0) is average, default is False(0), read-write, On/Off type(bool)
    POA_MONO_BIN = 30                   # only for color camera, when set to True, pixel binning will use neighbour pixels and image after binning will lose the bayer pattern, read-write, On/Off type(bool)


class POACameraProperties(Structure):               
    '''Camera Properties Definition'''
    _fields_ = [("cameraModelName",c_char*256),     # the camera name
                ("userCustomID",c_char*16),         # user custom name, it will be will be added after the camera name, max len 16 bytes,like:Mars-C [Juno], default is empty
                ("cameraID",c_int),                 # it's unique,camera can be controlled and set by the cameraID
                ("maxWidth",c_int),                 # max width of the camera
                ("maxHeight",c_int),                # max height of the camera
                ("bitDepth",c_int),                 # ADC depth of CMOS sensor
                ("isColorCamera",c_int),            # is a color camera or not
                ("isHasST4Port",c_int),             # does the camera have ST4 port, if not, camera don't support ST4 guide
                ("isHasCooler",c_int),              # does the camera have cooler assembly, generally, the cooled camera with cooler, lens heater and fan
                ("isUSB3Speed",c_int),              # is usb3.0 speed connection
                ("bayerPattern_",c_int),    # NOTE:please use the 'bayerPattern' property, the bayer filter pattern of camera
                ("pixelSize",c_double),             # camera pixel size(unit: um)
                ("SN",c_char*64),                   # the serial number of camera,it's unique
                ("sensorModelName",c_char*32),      # the sersor model(name) of camera, eg: IMX462
                ("localPath",c_char*256),           # the path of the camera in the computer host
                ("bins_",c_int*8),          # NOTE:please use the 'bins' property, bin supported by the camera, 1 == bin1, 2 == bin2,..., end with 0, eg:[1,2,3,4,0,0,0,0]
                ("imgFormats_",c_int*8),    # NOTE:please use the 'imgFormats' property, image data format supported by the camera, end with POA_END, eg:[POA_RAW8, POA_RAW16, POA_END,...]
                ("isSupportHardBin",c_int),         # does the camera sensor support hardware bin
                ("pID",c_int),                      # camera's Product ID, note: the vID of PlayerOne is 0xA0A0
                ("reserved",c_char*248)]            # reserved
    
    def get_bayer_pattern(self):
        return POABayerPattern(self.bayerPattern_)   
    bayerPattern = property(get_bayer_pattern)
    
    def get_img_formats(self):
        formats = []
        for i in range(8):
            fmt = self.imgFormats_[i]
            if fmt == POAImgFormat.POA_END.value:
                break
            formats.append(POAImgFormat(fmt))
        return formats   
    imgFormats = property(get_img_formats)
    
    def get_bins(self):
        bins = []
        for i in range(8):
            bin = self.bins_[i]
            if bin == 0:
                break
            bins.append(bin)
        return bins 
    bins = property(get_bins)


class POAConfigAttributes(Structure):
    '''Camera Config Attributes Definition(every POAConfig has a POAConfigAttributes)'''
    _fields_ = [("isSupportAuto",c_int),        # is support auto?
                ("isWritable",c_int),           # is writable?
                ("isReadable",c_int),           # is readable?
                ("configID_",c_int),        # NOTE:please use the 'configID' property, config ID, eg: POA_EXPOSURE
                ("valueType",c_int),            # 0 means this config value is int type, eg: POA_EXPOSURE, 2 means this config value is bool type, eg: POA_HARDWARE_BIN, POA_COOLER
                ("maxValue_",c_double),     # NOTE:please use the 'maxValue' property, maximum
                ("minValue_",c_double),     # NOTE:please use the 'minValue' property, minimum 
                ("defaultValue_",c_double), # NOTE:please use the 'defaultValue' property, default
                ("szConfName",c_char*64),       # POAConfig name, eg: POA_EXPOSURE: "Exposure", POA_TARGET_TEMP: "TargetTemp"
                ("szDescription",c_char*128),   # a brief introduction about this one POAConfig
                ("reserved",c_char*64)]         # reserved
    
    class CfgVal(Union):        # ignore this, used for the SDK's internal implementation
        _fields_ = [("intValue", c_long),
                    ("floatValue", c_double),
                    ("boolValue", c_int)]
    
    def get_config_ID(self):
        return POAConfig(self.configID_)   
    configID = property(get_config_ID)
    
    def get_max_value(self):
        uCfgVal = POAConfigAttributes.CfgVal()
        uCfgVal.floatValue = self.maxValue_   
        return int(uCfgVal.intValue) 
    maxValue = property(get_max_value)
    
    def get_min_value(self):
        uCfgVal = POAConfigAttributes.CfgVal()
        uCfgVal.floatValue = self.minValue_   
        return int(uCfgVal.intValue)   
    minValue = property(get_min_value)
    
    def get_def_value(self):
        uCfgVal = POAConfigAttributes.CfgVal()
        uCfgVal.floatValue = self.defaultValue_   
        return int(uCfgVal.intValue)     
    defaultValue = property(get_def_value)
    

class POASensorModeInfo(Structure):
    '''sensor mode information'''
    _fields_ = [("name",c_char*64),     # sensor mode name, can be used to display on the UI (eg: Combobox)
                ("desc",c_char*128)]    # sensor mode description, may be useful for tooltip


class POAGainsAndOffsets(Structure):
    '''some preset values of gain and offset'''
    _fields_ = [("pGainHighestDR",c_int),       # gain at highest dynamic range, in most cases, this gain is 0
                ("pHCGain",c_int),              # gain at HCG Mode(High Conversion Gain)
                ("pUnityGain",c_int),           # unity gain(or standard gain), with this gain, eGain(e/ADU) will be 1.0
                ("pGainLowestRN",c_int),        # Maximum Analog Gain, gain at lowest read noise
                ("pOffsetHighestDR",c_int),     # offset at highest dynamic range
                ("pOffsetHCGain",c_int),        # offset at HCG Mode
                ("pOffsetUnityGain",c_int),     # offset at unity gain
                ("pOffsetLowestRN",c_int)]      # offset at lowest read noise

#************************Low Level************************
def GetCameraCount():
    """
    get connected camera count

    Returns:
        int: the counts of POA cameras connected to the computer host
    """
    dll.POAGetCameraCount.restype = c_int
    return dll.POAGetCameraCount()

def GetCameraProperties(nIndex):
    """
    get the property of the connected cameras, NO need to open the camera for this operation

    Args:
        nIndex (int): the range: [0, camera count), note: index is not cameraID

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        POACameraProperties: property of camera
    """
    CameraProperties = POACameraProperties()
    Func = dll.POAGetCameraProperties
    Func.argtypes = [c_int,POINTER(POACameraProperties)]
    Func.restype = POAErrors
    Status = Func(nIndex,byref(CameraProperties))
    return Status,CameraProperties

def GetCameraPropertiesByID(nCameraID):
    """
    get the property of the connected cameras by ID, it's a convenience function to get the property of the known camera ID

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        POACameraProperties: property of camera
    """
    CameraProperties = POACameraProperties()
    Func = dll.POAGetCameraPropertiesByID
    Func.argtypes = [c_int,POINTER(POACameraProperties)]
    Func.restype = POAErrors
    Status = Func(nCameraID,byref(CameraProperties))
    return Status,CameraProperties

def OpenCamera(nCameraID):
    """
    open the camera, note: the following API functions need to open the camera first

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POAOpenCamera.restype = POAErrors
    return dll.POAOpenCamera(nCameraID)

def InitCamera(nCameraID):
    """
    initialize the camera's hardware, parameters, and malloc memory

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POAInitCamera.restype = POAErrors
    return dll.POAInitCamera(nCameraID)

def CloseCamera(nCameraID):
    """
    close the camera and free allocated memory

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POACloseCamera.restype = POAErrors
    return dll.POACloseCamera(nCameraID)

def GetAllConfigsAttributes(nCameraID):
    """
    get all POAConfigs attribute of this camera

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        POAConfigAttributes[]: list with all POAConfigAttributes
    """
    dll.POAGetConfigsCount.restype = POAErrors
    pConfCount=c_int(0)
    Status = dll.POAGetConfigsCount(nCameraID,byref(pConfCount))
    if Status != POAErrors.POA_OK:
        return Status, None
    
    Func = dll.POAGetConfigAttributes
    Func.argtypes = [c_int,c_int,POINTER(POAConfigAttributes)]
    Func.restype = POAErrors
    CfgsAttrs = []
    index = 0
    while index < pConfCount.value:
        ConfigAttributes = POAConfigAttributes()
        Status = Func(nCameraID,index,byref(ConfigAttributes))
        if Status != POAErrors.POA_OK:
            index += 1
            continue
        
        if (
            ConfigAttributes.configID_ == 3 or   # POA_TEMPERATURE
            ConfigAttributes.configID_ == 11 or  # POA_GUIDE_NORTH
            ConfigAttributes.configID_ == 12 or  # POA_GUIDE_SOUTH
            ConfigAttributes.configID_ == 13 or  # POA_GUIDE_EAST
            ConfigAttributes.configID_ == 14 or  # POA_GUIDE_WEST
            ConfigAttributes.configID_ == 15 or  # POA_EGAIN
            ConfigAttributes.configID_ == 22 or  # POA_FLIP_NONE
            ConfigAttributes.configID_ == 23 or  # POA_FLIP_HORI
            ConfigAttributes.configID_ == 24 or  # POA_FLIP_VERT
            ConfigAttributes.configID_ == 25 or  # POA_FLIP_BOTH
            ConfigAttributes.configID_ == 31     # POA_EXP
        ):
            index += 1
            continue
        
        CfgsAttrs.append(ConfigAttributes)
        index += 1
        
    return POAErrors.POA_OK, CfgsAttrs
    

def GetConfigAttributesByConfigID(nCameraID,confID):
    """
    get POAConfig attribute by POAConfig ID, it's a convenience function to get the attribute of the known POAConfig ID

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        confID (POAConfig): a POAConfig, eg: POA_EXPOSURE, POA_USB_BANDWIDTH

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        POAConfigAttributes: config attribute of this POAConfig
    """
    ConfigAttributes = POAConfigAttributes()
    Func = dll.POAGetConfigAttributesByConfigID
    Func.argtypes = [c_int,c_int,POINTER(POAConfigAttributes)]
    Func.restype = POAErrors
    Status = Func(nCameraID,confID.value,byref(ConfigAttributes))
    return Status,ConfigAttributes

def GetCameraTEMP(nCameraID):
    """
    get camera temperature

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        float: camera temperature
    """
    dll.POAGetConfig.restype = POAErrors
    confValue=c_double(0)
    isAuto=c_int(0)
    Status = dll.POAGetConfig(nCameraID,3,byref(confValue),byref(isAuto))
    return Status,confValue.value

def SetConfig(nCameraID,confID,confValue,isAuto):
    """
    set POAConfig value and auto value

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        confID (POAConfig): a POAConfig, eg: POA_EXPOSURE, POA_USB_BANDWIDTH
        confValue (int): the value set to the POAConfig
        isAuto (bool): set the POAConfig auto

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POASetConfig.restype = POAErrors
    dll.POASetConfig.argtypes = [c_int, c_int, c_int, c_int]
    return dll.POASetConfig(nCameraID,confID.value,confValue,int(isAuto))

def GetConfig(nCameraID,confID):
    """
    get the POAConfig value and auto value

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        confID (POAConfig): a POAConfig, eg: POA_EXPOSURE, POA_USB_BANDWIDTH

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        int: the POAConfig value
        bool: the POAConfig auto value, True is auto, False is not auto
    """
    dll.POAGetConfig.restype = POAErrors
    confValue=c_long(0)
    isAuto=c_int(0)
    Status = dll.POAGetConfig(nCameraID,confID.value,byref(confValue),byref(isAuto))
    return Status,confValue.value,bool(isAuto.value)

def GetImageStartPos(nCameraID):
    """
    get the start position of the ROI area

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        int: the starting point X of the ROI
        int: the starting point Y of the ROI
    """
    dll.POAGetImageStartPos.restype = POAErrors
    pStartX=c_int(0)
    pStartY=c_int(0)
    Status = dll.POAGetImageStartPos(nCameraID,byref(pStartX),byref(pStartY))
    return Status,pStartX.value,pStartY.value

def SetImageStartPos(nCameraID,startX,startY):
    """
    set the start position of the ROI area

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        startX (int): the starting point X of the ROI
        startY (int): the starting point Y of the ROI

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POASetImageStartPos.restype = POAErrors
    return dll.POASetImageStartPos(nCameraID,startX,startY)

def GetImageSize(nCameraID):
    """
    get the image size of the ROI area

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        int: the width of current camera image
        int: the height of current camera image
    """
    dll.POAGetImageSize.restype = POAErrors
    pWidth=c_int(0)
    pHeight=c_int(0)
    Status = dll.POAGetImageSize(nCameraID,byref(pWidth),byref(pHeight))
    return Status,pWidth.value,pHeight.value

def SetImageSize(nCameraID,width,height):
    """
    set the image size of the ROI area, note: should stop exposure first if is exposing,
    recommended to call POAGetImageSize to get the current image size after setting image size.

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        width (int): the image width, the width must divide 4 and no remainder, means: width % 4 == 0,
                     if width does not comply with this rule, this function automatically adjusts.
        height (int): the image height, the height must divide 2 and no remainder, means: height % 2 == 0,
                      if height does not comply with this rule, this function automatically adjusts.

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POASetImageSize.restype = POAErrors
    return dll.POASetImageSize(nCameraID,width,height)

def GetImageBin(nCameraID):
    """
    get the pixel binning of current image

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        int: the pixel binning
    """
    dll.POAGetImageBin.restype = POAErrors
    pBin=c_int(0)
    Status = dll.POAGetImageBin(nCameraID,byref(pBin))
    return Status,pBin.value

def SetImageBin(nCameraID,Bin):
    """
    set the pixel binning, note: should stop exposure first if is exposing,
    recommended to call POAGetImageSize to get the current image size after setting pixel binning

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        Bin (int): the pixel binning, eg: 1, 2...

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POASetImageBin.restype = POAErrors
    return dll.POASetImageBin(nCameraID,Bin)

def GetImageFormat(nCameraID):
    """
    get image data format

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        POAImgFormat: image data format, such as POA_RAW8, POA_RGB24
    """
    dll.POAGetImageFormat.restype = POAErrors
    pImgFormat=c_int(0)
    Status = dll.POAGetImageFormat(nCameraID,byref(pImgFormat))
    return Status,POAImgFormat(pImgFormat.value)

def SetImageFormat(nCameraID,imgFormat):
    """
    set image data format

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        imgFormat (POAImgFormat): image data format, such as POA_RAW8, POA_RGB24

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POASetImageFormat.restype = POAErrors
    return dll.POASetImageFormat(nCameraID,imgFormat.value)

def GetImageFlip(nCameraID):
    """
    get the flipped state of image

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        bool: whether the image is flipped horizontally
        bool: whether the image is flipped vertically
    """
    dll.POAGetConfig.restype = POAErrors
    confValue=c_long(0)
    isAuto=c_int(0)
    
    Error = dll.POAGetConfig(nCameraID,25,byref(confValue),byref(isAuto)) # flip both
    if Error.value != 0:
        return Error, False, False
    
    if confValue.value==1:
        return Error, True, True
    
    dll.POAGetConfig(nCameraID,24,byref(confValue),byref(isAuto)) # flip vert
    if confValue.value==1:
        return Error, False, True
    
    dll.POAGetConfig(nCameraID,23,byref(confValue),byref(isAuto)) # flip hori
    if confValue.value==1:
        return Error, True, False
    
    return Error, False, False

def SetImageFlip(nCameraID,isFlipH,isFlipV):
    """
    set the flipped state of image

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        isFlipH (bool): image flipped horizontally
        isFlipV (bool): image flipped vertically

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POASetConfig.restype = POAErrors
    if isFlipH and isFlipV:
        confID=c_int(25)
    elif isFlipH and not isFlipV:
        confID=c_int(23)
    elif not isFlipH and isFlipV:
        confID=c_int(24)
    else:
        confID=c_int(22)
        
    return dll.POASetConfig(nCameraID,confID,1,0)

def StartExposure(nCameraID,isSignalFrame):
    """
    start camera exposure

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        isSignalFrame (bool): True: SnapMode, after the exposure, will not continue(Single Shot), False: VideoMode, continuous exposure

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POAStartExposure.restype = POAErrors
    return dll.POAStartExposure(nCameraID,isSignalFrame)

def StopExposure(nCameraID):
    """
    stop camera exposure

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POAStopExposure.restype = POAErrors
    return dll.POAStopExposure(nCameraID)

def GetCameraState(nCameraID):
    """
    get the camera current state

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        POACameraState: camera current state
    """
    dll.POAGetCameraState.restype = POAErrors
    pCameraState=c_int(0)
    Status = dll.POAGetCameraState(nCameraID,byref(pCameraState))
    return Status,POACameraState(pCameraState.value)

def ImageReady(nCameraID):
    """
    get the image data is available, if is ready, you can call POAGetImageData to get image data

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        bool: the image data is available
    """
    dll.POAImageReady.restype = POAErrors
    pIsReady=c_int(0)
    Status = dll.POAImageReady(nCameraID,byref(pIsReady))
    return Status,bool(pIsReady.value)

def GetImageData(nCameraID: int, imgData: np.ndarray, nTimeoutms: int) -> POAErrors:
    """
    get image data after exposure, this function will block and waiting for timeout,
    Note: recommended to use POAImageReady function for waiting, if image data 'Is Ready', calling this function will return immediately

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        imgData (np.ndarray): an ndarray(1d and uint8) to fill with image data, please make sure imagedata is ready to allocate memory, eg: np.zeros(image_buf_size, dtype = np.uint8)
        nTimeoutms (int): wait time (ms), recommend set it to exposure+500ms, -1 means infinite waiting

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    if not isinstance(imgData, np.ndarray):
        return POAErrors.POA_ERROR_INVALID_ARGU
    
    dll.POAGetImageData.restype = POAErrors
    c_ptr = imgData.ctypes.data_as(POINTER(c_uint8)) # the image data is always C-contiguous
    dataSize = imgData.size
    return dll.POAGetImageData(nCameraID,c_ptr,dataSize,nTimeoutms)


def GetImage(nCameraID,nTimeoutms):
    """
    get image ndarray with 3d [rows, cols, channel], this function will block and waiting for timeout, 
    this function allocates memory the size of the image every time called, it is recommended to use the GetImageData function for good memory management.
    Note: recommended to use POAImageReady function for waiting, if image data 'Is Ready', calling this function will return immediately

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        nTimeoutms (int): wait time (ms), recommend set it to exposure+500ms, -1 means infinite waiting

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        numpy.ndarray: image data ndarray with 3d [rows, cols, channel]
    """
    dll.POAGetImageData.restype = POAErrors
    
    Status,imgWidth,imgHeight = GetImageSize(nCameraID)
    Status,imgFormat = GetImageFormat(nCameraID)
    imgSize = ImageCalcSize(imgHeight,imgWidth,imgFormat)
    buf = c_char*imgSize
    pBuf=buf()
    Status = dll.POAGetImageData(nCameraID,byref(pBuf),imgSize,nTimeoutms)
    pBufArray = np.frombuffer(pBuf, dtype=np.uint8)
    img = ImageDataConvert(pBufArray,imgHeight,imgWidth,imgFormat)
    return Status,img

def GetDroppedImagesCount(nCameraID):
    """
    get the dropped image count in SDK, reset it to 0 after stop capture

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        int: dropped image count
    """
    dll.POAGetDroppedImagesCount.restype = POAErrors
    pDroppedCount=c_int(0)
    Status = dll.POAGetDroppedImagesCount(nCameraID,byref(pDroppedCount))
    return Status,pDroppedCount.value

def SetGuideST4(nCameraID,direction,isOnOff):
    """
    set ST4 guide

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        direction (POAGuideDirection): guide direction
        isOnOff (bool): True: start to guide, False: stop guiding

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POASetConfig.restype = POAErrors
    if direction == POAGuideDirection.GUIDE_NORTH:
        confID=c_int(11)
    elif direction == POAGuideDirection.GUIDE_SOUTH:
        confID=c_int(12)
    elif direction == POAGuideDirection.GUIDE_EAST:
        confID=c_int(13)
    elif direction == POAGuideDirection.GUIDE_WEST:
        confID=c_int(14)
    else:
        return POAErrors.POA_ERROR_INVALID_ARGU
      
    return dll.POASetConfig(nCameraID,confID,int(isOnOff),0)

def GetSensorModeCount(nCameraID):
    """
    get the sensor mode count supported by this camera

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        int: sensor mode count, 0 means camera don't supported sensor mode selection
    """
    dll.POAGetSensorModeCount.restype = POAErrors
    pModeCount=c_int(0)
    Status = dll.POAGetSensorModeCount(nCameraID,byref(pModeCount))
    return Status,pModeCount.value

def GetSensorModeInfo(nCameraID,modeIndex):
    """
    get the camera sensor mode information by the index

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        modeIndex (int): the range: [0, mode count)

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        POASensorModeInfo: sensor mode information
    """
    SensorModeInfo = POASensorModeInfo()
    Func = dll.POAGetSensorModeInfo
    Func.argtypes = [c_int,c_int,POINTER(POASensorModeInfo)]
    Func.restype = POAErrors
    Status = Func(nCameraID,modeIndex,byref(SensorModeInfo))
    return Status,SensorModeInfo

def SetSensorMode(nCameraID,modeIndex):
    """
    set the camera sensor mode by the index, Note: should stop exposure first if exposing

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        modeIndex (int): the range: [0, mode count)

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POASetSensorMode.restype = POAErrors
    return dll.POASetSensorMode(nCameraID,modeIndex)

def GetSensorMode(nCameraID):
    """
    get camera the current sensor mode

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        int: the current sensor mode index
    """
    dll.POAGetSensorMode.restype = POAErrors
    pModeIndex=c_int(0)
    Status = dll.POAGetSensorMode(nCameraID,byref(pModeIndex))
    return Status,pModeIndex.value

def GetCameraEgain(nCameraID):
    """
    get camera current egain(e/ADU)

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        float: camera current egain(e/ADU)
    """
    dll.POAGetConfig.restype = POAErrors
    confValue=c_double(0)
    isAuto=c_int(0)
    Status = dll.POAGetConfig(nCameraID,15,byref(confValue),byref(isAuto))
    return Status,confValue.value

def SetUserCustomID(nCameraID,strCustomID):
    """
    set user custom ID into camera flash, if set successfully, reacquire the information of this camera to get the custom ID,
    Note: this operation will interrupt the exposure, if start a Signal Frame exposure , the exposure progress will be terminated.

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        strCustomID (str): custom ID string, max len is 16, if len > 16, the extra part will be cut off, if empty, the previous setting will be cleared

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    bufLen = len(strCustomID)
    dll.POASetUserCustomID.restype = POAErrors
    Buf = strCustomID.encode('utf-8')
    if bufLen > 16:
        bufLen = 16
    return dll.POASetUserCustomID(nCameraID,Buf,bufLen)

def GetGainsAndOffsets(nCameraID):
    """
    get some preset values of gain and offset

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        POAGainsAndOffsets: gains and offsets
    """
    dll.POAGetGainsAndOffsets.restype = POAErrors
    GainHighestDR=c_int(0)
    HCGain=c_int(0)
    UnityGain=c_int(0)
    GainLowestRN=c_int(0)
    OffsetHighestDR=c_int(0)
    OffsetHCGain=c_int(0)
    OffsetUnityGain=c_int(0)
    OffsetLowestRN=c_int(0)
    GainsAndOffsets = POAGainsAndOffsets()
    Status = dll.POAGetGainsAndOffsets(nCameraID,byref(GainHighestDR),byref(HCGain),byref(UnityGain),byref(GainLowestRN), \
        byref(OffsetHighestDR),byref(OffsetHCGain),byref(OffsetUnityGain),byref(OffsetLowestRN))
    GainsAndOffsets.pGainHighestDR=GainHighestDR
    GainsAndOffsets.pHCGain=HCGain
    GainsAndOffsets.pUnityGain=UnityGain
    GainsAndOffsets.pGainLowestRN=GainLowestRN
    GainsAndOffsets.pOffsetHighestDR=OffsetHighestDR
    GainsAndOffsets.pOffsetHCGain=OffsetHCGain
    GainsAndOffsets.pOffsetUnityGain=OffsetUnityGain
    GainsAndOffsets.pOffsetLowestRN=OffsetLowestRN
    return Status, GainsAndOffsets

def GetErrorString(err):
    """
    convert POAErrors enum to string, it is convenient to print or display errors

    Args:
        err (POAErrors): a error returned by the API function

    Returns:
        str: string error
    """
    Func = dll.POAGetErrorString
    Func.restype = POINTER(c_ubyte)
    buf = Func(err.value)
    list = []
    for n in range(255):
        if buf[n]==0: break;
        list.append(chr(buf[n]))
    list = ''.join(list)
    return list

def GetAPIVersion():
    """
    get the API version

    Returns:
        int: API version, easy to do version comparison, eg: 20240420
    """
    dll.POAGetAPIVersion.restype = c_int
    return dll.POAGetAPIVersion()

def GetSDKVersion():
    """
    get the sdk version

    Returns:
        str: sdk version string(major.minor.patch), eg: "1.0.1"
    """
    Func = dll.POAGetSDKVersion
    Func.restype = POINTER(c_ubyte)
    buf = Func()
    list = []
    for n in range(255):
        if buf[n]==0: break;
        list.append(chr(buf[n]))
    list = ''.join(list)
    return list

#************************High Level************************
def GetGain(nCameraID):
    """
    get current gain of camera

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        int: current gain of camera
        bool: whether gain is auto
    """
    Status,Gain,isAuto = GetConfig(nCameraID,POAConfig.POA_GAIN)
    return Status,Gain,isAuto

def GetExp(nCameraID):
    """
    get current exposure time(microsecond (us)) of camera

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        int: current exposure time(microsecond (us)) of camera
        bool: whether exposure is auto
    """
    Status,Exp,isAuto = GetConfig(nCameraID,POAConfig.POA_EXPOSURE)
    return Status,Exp,isAuto

def GetExp_S(nCameraID):
    """
    get current exposure time(second (s)) of camera

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
        float: current exposure time(second (s)) of camera
        bool: whether exposure is auto
    """
    dll.POAGetConfig.restype = POAErrors
    confValue=c_double(0)
    isAuto=c_int(0)
    Status = dll.POAGetConfig(nCameraID,31,byref(confValue),byref(isAuto))
    return Status,confValue.value,bool(isAuto.value)

def SetGain(nCameraID,Gain,isAuto):
    """
    set gain

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        Gain (int): gain value
        isAuto (bool): is auto

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    Status = SetConfig(nCameraID,POAConfig.POA_GAIN,Gain,isAuto)
    return Status

def SetExp(nCameraID,Exp,isAuto):
    """
    set exposure time(microsecond (us)), max 2000000000us(2000s)

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        Exp (int): exposure time(microsecond (us)) value
        isAuto (bool): is auto

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    Status = SetConfig(nCameraID,POAConfig.POA_EXPOSURE,Exp,isAuto)
    return Status

def SetExp_S(nCameraID,Exp_s,isAuto):
    """
    set exposure time(second (s)), max 7200.0s

    Args:
        nCameraID (int): camera ID, get it from in the POACameraProperties
        Exp_s (float): exposure time(second (s)) value
        isAuto (bool): is auto

    Returns:
        POAErrors: error code returned by calling this function, POA_OK indicates success
    """
    dll.POASetConfig.restype = POAErrors
    dll.POASetConfig.argtypes = [c_int, c_int, c_double, c_int]
    c_exp_s=c_double(Exp_s)
    return dll.POASetConfig(nCameraID,31,c_exp_s,int(isAuto))

def ImageCalcSize(imgHeight,imgWidth,imgFormat):  
    """
    calculate the size of image data(uint8)

    Args:
        imgHeight (int): image height
        imgWidth (int): image width
        imgFormat (POAImgFormat): image format

    Returns:
        int: the size of image data(uint8)
    """
    if imgFormat == POAImgFormat.POA_RAW8:
        Size = imgWidth*imgHeight*1
    elif imgFormat == POAImgFormat.POA_RAW16:
        Size = imgWidth*imgHeight*2
    elif imgFormat == POAImgFormat.POA_RGB24:
        Size = imgWidth*imgHeight*3
    elif imgFormat == POAImgFormat.POA_MONO8:
        Size = imgWidth*imgHeight*1
    else:
        Size = 0
        
    return Size

def ImageDataConvert(bufArray,imgHeight,imgWidth,imgFormat):
    """
    convert uint8 numpy 1dArray to 3dArray

    Args:
        bufArray (numpy.1dArray): a numpy array with image data
        imgHeight (int): image height
        imgWidth (int): image width
        imgFormat (POAImgFormat): image format

    Returns:
        numpy.ndarray: the numpy array after converting with 3d[rows, cols, channel]
    """
    if imgFormat == POAImgFormat.POA_RAW8:
        Img=np.reshape(bufArray,(imgHeight,imgWidth,1))
    elif imgFormat == POAImgFormat.POA_RAW16:
        #Size = imgWidth*imgHeight*2
        #ArrayA = bufArray[slice(0,Size,2)]
        #ArrayB = bufArray[slice(1,Size,2)]
        #ArrayC = np.add(np.multiply(ArrayB,256),ArrayA)
        ArrayUInt16 = np.frombuffer(bufArray.tobytes(), dtype='<u2', count=len(bufArray)//2) # image data always little-endian
        Img=np.reshape(ArrayUInt16,(imgHeight,imgWidth,1))
    elif imgFormat == POAImgFormat.POA_RGB24:
        Img=np.reshape(bufArray,(imgHeight,imgWidth,3))
    elif imgFormat == POAImgFormat.POA_MONO8:
        Img=np.reshape(bufArray,(imgHeight,imgWidth,1))
    else:
        Img = None
    return Img
