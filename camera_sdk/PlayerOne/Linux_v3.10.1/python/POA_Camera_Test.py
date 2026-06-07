# -*- coding: utf-8 -*-
import time
import cv2
import numpy as np
import pyPOACamera


Camera_ID = 0
Camera_Error = pyPOACamera.POAErrors.POA_OK
Config_Count = 0
Camera_Opened = 0

# get camera API version and SDK version
APIver = pyPOACamera.GetAPIVersion()
print("APIver:",APIver)

SDKver = pyPOACamera.GetSDKVersion()
print("SDKver:",SDKver)

# ----------------get camera count----------------
Camera_Count = pyPOACamera.GetCameraCount()
print("Connected POA Camera Count:",Camera_Count)

if (Camera_Count>0):
    
    # get first(index is 0) camera properties
    Camera_Error,CameraProperties = pyPOACamera.GetCameraProperties(0)
    Error_String = pyPOACamera.GetErrorString(Camera_Error) # can use this function to get error string
    print("Get Camera Properties:",Error_String)
    
    Camera_ID = CameraProperties.cameraID

    print("cameraModelName:",CameraProperties.cameraModelName)
    print("userCustomID:",CameraProperties.userCustomID)
    print("cameraID:",CameraProperties.cameraID)
    print("maxWidth:",CameraProperties.maxWidth)
    print("maxHeight:",CameraProperties.maxHeight)
    print("bitDepth:",CameraProperties.bitDepth)
    print("isColorCamera:",bool(CameraProperties.isColorCamera))
    print("isHasST4Port:",bool(CameraProperties.isHasST4Port))
    print("isHasCooler:",bool(CameraProperties.isHasCooler))
    print("isUSB3Speed:",bool(CameraProperties.isUSB3Speed))
    print("bayerPattern:",CameraProperties.bayerPattern)        # do NOT use bayerPattern_
    print("pixelSize:",CameraProperties.pixelSize)
    print("SN:",CameraProperties.SN)
    print("sensorModelName:",CameraProperties.sensorModelName)
    print("localPath:",CameraProperties.localPath)
    print("bins:",CameraProperties.bins)                        # do NOT use bins_
    print("imgFormats:",CameraProperties.imgFormats)            # do NOT use imgFormats_    
    print("isSupportHardBin:",bool(CameraProperties.isSupportHardBin))
    print("pID:",hex(CameraProperties.pID))
    print("reserved:",CameraProperties.reserved)

    # ----------------open camera----------------
    Camera_Error = pyPOACamera.OpenCamera(Camera_ID)
    print("OpenCameraStatus:",Camera_Error)
    if Camera_Error == pyPOACamera.POAErrors.POA_OK:
        Camera_Opened = 1
        print("Camera_Opened:",Camera_Opened)
    else:
        Camera_Opened = 0
        print("Camera_Opened:",Camera_Opened)
        Error_String = pyPOACamera.GetErrorString(Camera_Error) # can use this function to get error string
        print("Opened camera failed:",Error_String)
        exit()

    # ----------------init camera----------------
    Camera_Error = pyPOACamera.InitCamera(Camera_ID) # after opening camera
    #print("InitCameraStatus:",Camera_Error)
    if Camera_Error != pyPOACamera.POAErrors.POA_OK:
        Error_String = pyPOACamera.GetErrorString(Camera_Error) # can use this function to get error string
        print("Init camera failed:",Error_String)
        exit()

    # ----------------get camera all configs and its attributes----------------
    print("----------------------------------------")
    Camera_Error, CfgsAttrs = pyPOACamera.GetAllConfigsAttributes(Camera_ID)
    #print("GetAllConfigsAttributes Status:",Camera_Error)
    for aCfgAttrs in CfgsAttrs:
        print("szConfName:",aCfgAttrs.szConfName)
        print("szDescription:",aCfgAttrs.szDescription)
        print("isSupportAuto:",bool(aCfgAttrs.isSupportAuto))
        print("isWritable:",bool(aCfgAttrs.isWritable))
        print("isReadable:",bool(aCfgAttrs.isReadable))
        print("configID:",aCfgAttrs.configID)           # do NOT use configID_  
        print("valueType:",aCfgAttrs.valueType)
        print("maxValue:",aCfgAttrs.maxValue)           # do NOT use maxValue_  
        print("minValue:",aCfgAttrs.minValue)           # do NOT use minValue_
        print("defaultValue:",aCfgAttrs.defaultValue)   # do NOT use defaultValue_
        print("----------------------------------------")
    
#############################################################################################################
    # ----------------get camera temperature----------------
    Temp = 0.0
    Camera_Error, Temp = pyPOACamera.GetCameraTEMP(Camera_ID)
    print("camera temperature: {:.1f}C".format(Temp))
    
    # ----------------get and set camera config----------------
    Camera_Error, aCfgAttr = pyPOACamera.GetConfigAttributesByConfigID(Camera_ID,pyPOACamera.POAConfig.POA_EXPOSURE)
    print("exposure time range(us):",aCfgAttr.minValue,"~",aCfgAttr.maxValue) # get exposure time range
    
    Camera_Error = pyPOACamera.SetExp(Camera_ID,20000,False) # set exposure to 20ms (20000us), not auto, this maximum is 2000000000 us(2000s)
    Camera_Error, exp, auto = pyPOACamera.GetExp(Camera_ID) # get exposure and auto
    print("exposure (us):",exp,", auto:",auto)
    
    Camera_Error = pyPOACamera.SetExp_S(Camera_ID,7.2,False) # set exposure to 7.2s, not auto, recommended to use SetExp_S for setting exposure, this maximum is 7200.0s
    Camera_Error, exp_s, auto = pyPOACamera.GetExp_S(Camera_ID) # get exposure and auto
    print("exposure (s): {:.2f}, auto: {}".format(exp_s, auto))
    
    Camera_Error = pyPOACamera.SetGain(Camera_ID,180,True) # set gain to 180, auto
    Camera_Error, gain, auto = pyPOACamera.GetGain(Camera_ID) # get gain and auto
    print("gain:",gain,", auto:",auto)
    
    Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_OFFSET, 20, False) # set offset to 20, not auto
    Camera_Error, offset, auto = pyPOACamera.GetConfig(Camera_ID, pyPOACamera.POAConfig.POA_OFFSET) # get offset and auto
    print("offset:",offset,", auto:",auto)
    
    Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_USB_BANDWIDTH_LIMIT, 90, False) # set usb bandwidth percentage to 90%
    
    if CameraProperties.isColorCamera: # set white balance
        Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_WB_R, 120, False)
        Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_WB_G, 0, False)
        Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_WB_B, 150, False)
        # if want to set white balance to auto, just set one of the three to auto, like : pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_WB_R, 0, True)
    
    if CameraProperties.isSupportHardBin: # set hardware binning
        Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_HARDWARE_BIN, 1, False) # enable hardware binning   
        Camera_Error, isHardBin, auto = pyPOACamera.GetConfig(Camera_ID, pyPOACamera.POAConfig.POA_HARDWARE_BIN)
        print("is hardware bin:", bool(isHardBin))
        
        Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_HARDWARE_BIN, 0, False) # disable hardware binning
    else:
        print("hardware binning is not supported")  
        
    Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_PIXEL_BIN_SUM, 1, False) # enable pixel binning sum
    Camera_Error, isPixelBinSum, auto = pyPOACamera.GetConfig(Camera_ID, pyPOACamera.POAConfig.POA_PIXEL_BIN_SUM)
    print("is pixel bin sum:", bool(isPixelBinSum))
    
    Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_PIXEL_BIN_SUM, 0, False) # disable pixel binning sum
    
    if CameraProperties.isHasCooler: #cooling settings if cooled camera
        Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_TARGET_TEMP, -10, False) # set target temperature to -10C
        Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_FAN_POWER, 90, False) # set fan power percentage to 90%
        Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_HEATER_POWER, 20, False) # set heater power percentage to 20%, POA_HEATER is deprecated
        Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_COOLER, 1, False) # turn on the cooler
        #Camera_Error = pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_COOLER, 0, False) # turn off the cooler
        Camera_Error, coolerPower, auto = pyPOACamera.GetConfig(Camera_ID, pyPOACamera.POAConfig.POA_COOLER_POWER) # get the current cooler power
        print("cooler power:", coolerPower)
    else:
        print("is not a cooled camera")    
        
    
    # ----------------set image parameters(ROI)----------------
    Camera_Error = pyPOACamera.SetImageStartPos(Camera_ID,0,0) # set ROI start point (0,0)
    print("SetImageStartPosStatus:",Camera_Error)
    
    Camera_Error,StartX,StartY = pyPOACamera.GetImageStartPos(Camera_ID) # get ROI start point
    #print("GetImageStartPosStatus:",Camera_Error)
    print("StartX:",StartX)
    print("StartY:",StartY)
    
    Camera_Error = pyPOACamera.SetImageSize(Camera_ID,CameraProperties.maxWidth,CameraProperties.maxHeight) # set ROI size to max width and max height
    print("SetImageSizeStatus:",Camera_Error)
    
    Camera_Error,imgWidth,imgHeight = pyPOACamera.GetImageSize(Camera_ID) # get ROI size
    #print("GetImageSizeStatus:",Camera_Error)
    print("imgWidth:",imgWidth)
    print("imgHeight:",imgHeight)

    Camera_Error = pyPOACamera.SetImageBin(Camera_ID,CameraProperties.bins[0]) # set img bin to 1, in most cases, bins have 1, 2, 3, 4
    print("SetImageBinStatus:",Camera_Error)
    
    Camera_Error,imgPixBin = pyPOACamera.GetImageBin(Camera_ID) #get img bin
    #print("GetImageBinStatus:",Camera_Error)
    print("imgPixBin:",imgPixBin)
    
    Camera_Error = pyPOACamera.SetImageFormat(Camera_ID, CameraProperties.imgFormats[0]) # set image format to POAImgFormat.POA_RAW8, Mono camera: RAW8, RAW16, Color camera: RAW8, RAW16, RGB24, MONO8
    print("SetImageFormatStatus:",Camera_Error)
    
    Camera_Error,imgFormat = pyPOACamera.GetImageFormat(Camera_ID) # get image format
    #print("GetImageFormatStatus:",Camera_Error)
    print("imgFormat:",imgFormat)
    
    Camera_Error = pyPOACamera.SetImageFlip(Camera_ID, True, False) # set image flip horizontal
    print("SetImageFlipStatus:",Camera_Error)
    
    Camera_Error, isFlipH, isFlipV = pyPOACamera.GetImageFlip(Camera_ID) # get image flip
    #print("GetImageFlipStatus:",Camera_Error)
    print("isFlipH:",isFlipH,"isFlipV:",isFlipV)
    
    
    # ----------------start exposure and get image data----------------
    
    pyPOACamera.SetImageFormat(Camera_ID, pyPOACamera.POAImgFormat.POA_RAW16) # set image format to raw16
    pyPOACamera.SetImageBin(Camera_ID, 1) # set bin1
    pyPOACamera.SetImageSize(Camera_ID, 512 , 512) # set image size to 512 * 512
    
    # 1. Snap Exposure(single frame)
    print('single frame exposure (Snap Mode)')
    exp_val_us = 2000000
    pyPOACamera.SetExp(Camera_ID, exp_val_us, False) # set exposure to 2s
    
    Camera_Error = pyPOACamera.StartExposure(Camera_ID,True)
    print("StartExposureStatus:",Camera_Error)
    if Camera_Error == pyPOACamera.POAErrors.POA_OK:    # start exposure successfully
        while(True):                                    # wait for exposure to finish
            # if breakTrigger:
            #   break
            Camera_Error,cameraState = pyPOACamera.GetCameraState(Camera_ID)
            if(cameraState == pyPOACamera.POACameraState.STATE_OPENED): # STATE_OPENED is mean camera is Idle, not exposuring
                break
            
        Camera_Error,pIsReady = pyPOACamera.ImageReady(Camera_ID)        
        if pIsReady:
            Camera_Error,Img = pyPOACamera.GetImage(Camera_ID, int(exp_val_us/1000)) # GetImage will get a 3d image array
            print("single frame img type: ",Img.shape, ", the image data type is:", Img.dtype)
        else:
            print("single frame exposure failed: image is not ready!")
            
    # 2. continuously exposure(Video Mode) 
    print('continuously exposure (Video Mode)')
    # make some preparations
    pyPOACamera.SetExp(Camera_ID, 30000, False) # set exposure to 30ms
    pyPOACamera.SetGain(Camera_ID, 0, True) # set gain to auto
    if CameraProperties.isColorCamera:
        pyPOACamera.SetConfig(Camera_ID, pyPOACamera.POAConfig.POA_WB_R, 0, True) # if color camera, set auto white balance
        pyPOACamera.SetImageFormat(Camera_ID, pyPOACamera.POAImgFormat.POA_RGB24) # if color camera, set image format to RGB24
    else:
        pyPOACamera.SetImageFormat(Camera_ID, pyPOACamera.POAImgFormat.POA_RAW8) # if not color camera, set image format to RAW8
    
    # before starting the exposure, you need to prepare an ndarray for loading image data
    Camera_Error,imgWidth,imgHeight = pyPOACamera.GetImageSize(Camera_ID) # get the image size
    Camera_Error,imgFormat = pyPOACamera.GetImageFormat(Camera_ID) # get image format
    
    imgSize = pyPOACamera.ImageCalcSize(imgHeight,imgWidth,imgFormat)
    bufArray = np.zeros(imgSize, dtype = np.uint8) # this array only for loading image data from camera
    Img = None
    
    WindowTitle = "pyPOACamera_Video_Mode_Test"
    cv2.namedWindow(WindowTitle,cv2.WINDOW_NORMAL)
    cv2.resizeWindow(WindowTitle,imgWidth,imgHeight)
    
    Camera_Error = pyPOACamera.StartExposure(Camera_ID,False)
    print("StartExposureStatus:",Camera_Error)
    if Camera_Error == pyPOACamera.POAErrors.POA_OK:    # start exposure successfully
        while(True):
            # if breakTrigger:
            #   break
            while(True):
                # if breakTrigger:
                #   break 
                Camera_Error,pIsReady = pyPOACamera.ImageReady(Camera_ID) # In Video Mode, only need to check if the image is ready, NO need to check camera state
                if pIsReady:
                    break
            Camera_Error = pyPOACamera.GetImageData(Camera_ID, bufArray, 1000) # GetImageData needs to prepare the bufArray first
            Img=pyPOACamera.ImageDataConvert(bufArray,imgHeight,imgWidth,imgFormat)
            #print("type",Img.shape)
            cv2.imshow(WindowTitle,Img)
      
            key=cv2.waitKey(10)
            if key==ord('s'):
                print(key)
            elif key==ord('q'):
                print('Finish') 
                break
            
        Camera_Error, dropCount = pyPOACamera.GetDroppedImagesCount(Camera_ID) # get dropped images count in SDK
        print("Dropped images:", dropCount)
        
        # need to stop exposure
        Camera_Error = pyPOACamera.StopExposure(Camera_ID)
        print("StopExposureStatus:",Camera_Error)

        cv2.destroyAllWindows()
    
    
    # ----------------other miscellaneous settings----------------
    
    # set camera sensor mode
    Camera_Error, sensorModeCount = pyPOACamera.GetSensorModeCount(Camera_ID)
    print("sensorModeCount:",sensorModeCount)
    
    if sensorModeCount > 0: # 0 means camera don't supported mode selection
        modeIndex = 0
        while modeIndex < sensorModeCount:
            Camera_Error, SenModeInfo = pyPOACamera.GetSensorModeInfo(Camera_ID,modeIndex)
            #print("GetSensorModeInfoStatus:",Camera_Error)
            print("SenModeInfoName:",SenModeInfo.name)
            print("SenModeInfoDesc:",SenModeInfo.desc)
            modeIndex += 1
            
        Camera_Error = pyPOACamera.SetSensorMode(Camera_ID,1) # Normally, 0 is Normal and 1 is LowNosie
        print("SetSensorMode Status:",Camera_Error)
    else:
        print("camera don't supported sensor mode selection")
        
    # ST4 guide
    if CameraProperties.isHasST4Port:
        print('test ST4 guide(On/Off):') # recommended to do this in a thread
        pyPOACamera.SetGuideST4(Camera_ID, pyPOACamera.POAGuideDirection.GUIDE_NORTH, True)
        print('guide North start!')
        time.sleep(1) # guide north lasts for 1s
        pyPOACamera.SetGuideST4(Camera_ID, pyPOACamera.POAGuideDirection.GUIDE_NORTH, False)
        print('guide North stop!')
    else:
        print('this camera do not support ST4 guide')
    
    #  get camera preset gains and offsets
    Camera_Error, GainsAndOffsets = pyPOACamera.GetGainsAndOffsets(Camera_ID)
    #print("GetGainsAndOffsetsStatus:",Camera_Error)
    print("pGainHighestDR:",GainsAndOffsets.pGainHighestDR)
    print("pHCGain:",GainsAndOffsets.pHCGain)
    print("pUnityGain:",GainsAndOffsets.pUnityGain)
    print("pGainLowestRN:",GainsAndOffsets.pGainLowestRN)
    print("pOffsetHighestDR:",GainsAndOffsets.pOffsetHighestDR)
    print("pOffsetHCGain:",GainsAndOffsets.pOffsetHCGain)
    print("pOffsetUnityGain:",GainsAndOffsets.pOffsetUnityGain)
    print("pOffsetLowestRN:",GainsAndOffsets.pOffsetLowestRN)
    
    # get egain
    Camera_Error, Egain = pyPOACamera.GetCameraEgain(Camera_ID)
    print("Egain(e/ADU):{:.2f}".format(Egain))
    
    # set user custom name (Custom ID)
    UserID = "POA"
    Camera_Error = pyPOACamera.SetUserCustomID(Camera_ID,UserID) # max len is 16 and must stop exposure first if is exposing
    print("SetUserCustomIDStatus:",Camera_Error)
    Camera_Error, CameraProperties = pyPOACamera.GetCameraPropertiesByID(Camera_ID) # check what changes
    print("cameraModelName:",CameraProperties.cameraModelName)
    print("userCustomID:",CameraProperties.userCustomID)

    Camera_Error = pyPOACamera.SetUserCustomID(Camera_ID,'') # clear the userCustomID

    # close the camera
    Camera_Error = pyPOACamera.CloseCamera(Camera_ID)
    print("CloseCameraStatus:",Camera_Error)
