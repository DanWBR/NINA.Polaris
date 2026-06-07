#include <iostream>
#include "POACamera.h"

#include "PlayerOneCamera.h"

POACamera::POACamera()
{
    m_nCameraID = -1;
}

POACamera::POACamera(int nCameraID)
{
    m_nCameraID = nCameraID;
}

POACamera::~POACamera()
{

}

map<int, string> POACamera::getALLCameraIDName()
{
    map<int, string> cameraIDName;

    int camera_count = POAGetCameraCount();

    for(int i = 0; i < camera_count; ++i)
    {
       POACameraProperties cameraProp;

       POAErrors error = POAGetCameraProperties(i, &cameraProp);

       if(error != POA_OK)
       {
           continue;
       }

       cameraIDName.insert(pair<int, string>(cameraProp.cameraID, string(cameraProp.cameraModelName)));
    }

    return cameraIDName;

}

bool POACamera::openCamera()
{
    POAErrors error = POAOpenCamera(m_nCameraID);

    if(error != POA_OK)
    {
        cerr << "Open camera failed！, error code: " << POAGetErrorString(error) << endl;
    }

    return error == POA_OK ? true : false;
}

bool POACamera::initCamera()
{
    POAErrors error = POAInitCamera(m_nCameraID);

    if(error != POA_OK)
    {
        cerr << "Init camera failed！, error code: " << POAGetErrorString(error) << endl;
    }

    return error == POA_OK ? true : false;
}

void POACamera::getAllConfigAttributes()
{
    int config_count = 0;
    POAErrors error;
    error = POAGetConfigsCount(m_nCameraID, &config_count);
    if(error != POA_OK)
    {
        cerr << "Get config count failed！, error code: " << POAGetErrorString(error) << endl;
        return;
    }

    for(int i = 0; i < config_count; i++)
    {
        POAConfigAttributes confAttributes;

        error = POAGetConfigAttributes(m_nCameraID, i, &confAttributes);

        if(error != POA_OK)
        {
            cerr << "Get config attributes failed！, index: " << i << ", error code: " << POAGetErrorString(error) << endl;
            continue;
        }

        cout << endl;

        cout << "config name: " << confAttributes.szConfName << ", config description: " << confAttributes.szDescription << endl;

        cout << "is writable: " << confAttributes.isWritable << endl;

        cout << "is readable: " << confAttributes.isReadable << endl;

        if(confAttributes.valueType == VAL_INT)
        {
            cout << "min: " << confAttributes.minValue.intValue << ", max: " << confAttributes.maxValue.intValue << ", default: " << confAttributes.defaultValue.intValue << endl;
        }
        else if(confAttributes.valueType == VAL_FLOAT)
        {
            cout << "min: " << confAttributes.minValue.floatValue << ", max: " << confAttributes.maxValue.floatValue << ", default: " << confAttributes.defaultValue.floatValue << endl;
        }
        else if(confAttributes.valueType == VAL_BOOL) // The maxValue and minValue values of this VAL_BOOL type are meaningless
        {
            cout <<  "default is on: " << confAttributes.defaultValue.boolValue << endl;
        }

    }
}

bool POACamera::setROIArea(const ROIArea &roiArea)
{
    //set ROI Area, if exposing, please stop exposure first

    POACameraState cameraState;

    POAGetCameraState(m_nCameraID, &cameraState);

    if(cameraState == STATE_EXPOSING)
    {
        POAStopExposure(m_nCameraID);
    }

    POAErrors error;
    // set resolution
    error = POASetImageSize(m_nCameraID, roiArea.width, roiArea.height); //default resolution is maxWidth * maxHeight
    if(error != POA_OK)
    {
        cerr << "set resolution failed, error code: " << POAGetErrorString(error) << endl;
        return false;
    }

    // set start position
    error = POASetImageStartPos(m_nCameraID, roiArea.startX, roiArea.startY); //default start position is (0, 0)
    if(error != POA_OK)
    {
        cerr << "set start position failed, error code: " << POAGetErrorString(error) << endl;
        return false;
    }

    return true;
}

ROIArea POACamera::getROIArea()
{
    ROIArea roiArea;

    POAErrors error;
    error = POAGetImageStartPos(m_nCameraID, &roiArea.startX, &roiArea.startY);
    if(error != POA_OK)
    {
        cerr << "get start position failed, error code: " << POAGetErrorString(error) << endl;
    }

    error = POAGetImageSize(m_nCameraID, &roiArea.width, &roiArea.height);
    if(error != POA_OK)
    {
        cerr << "get resolution failed, error code: " << POAGetErrorString(error) << endl;
    }

    return roiArea;
}

bool POACamera::setImageSize(int width, int height)
{

    POACameraState cameraState;

    POAGetCameraState(m_nCameraID, &cameraState);

    if(cameraState == STATE_EXPOSING)
    {
        POAStopExposure(m_nCameraID);
    }

    // set resolution
    POAErrors error = POASetImageSize(m_nCameraID, width, height); //default resolution is maxWidth * maxHeight
    if(error != POA_OK)
    {
        cerr << "set resolution failed, error code: " << POAGetErrorString(error) << endl;
        return false;
    }

    return true;
}

bool POACamera::setImageStartPos(int startX, int startY)
{
    // set start position
    POAErrors error = POASetImageStartPos(m_nCameraID, startX, startY); //default start position is (0, 0)
    if(error != POA_OK)
    {
        cerr << "set start position failed, error code: " << POAGetErrorString(error) << endl;
        return false;
    }

    return true;
}

bool POACamera::setImageFormat(POACamera::ImageFormat imgFmt)
{
    POACameraState cameraState;

    POAGetCameraState(m_nCameraID, &cameraState);

    if(cameraState == STATE_EXPOSING) //should stop exposure first if exposing
    {
        POAStopExposure(m_nCameraID);
    }

    POAImgFormat poaImgFmt = POA_RAW8;

    switch (imgFmt)
    {
    case RAW8:
        poaImgFmt = POA_RAW8;
        break;
    case RAW16:
        poaImgFmt = POA_RAW16;
        break;
    case RGB888:
        poaImgFmt = POA_RGB24;
        break;
    case MONO8:
        poaImgFmt = POA_MONO8;
        break;
    }

    POAErrors error = POASetImageFormat(m_nCameraID, poaImgFmt); //default image format is POA_RAW8
    if(error != POA_OK)
    {
        cerr << "set image format failed, error code: " << POAGetErrorString(error) << endl;
        return false;
    }

    return true;
}

POACamera::ImageFormat POACamera::getImageFormat()
{
    POAImgFormat poaImgFmt = POA_RAW8;

    POAErrors error = POAGetImageFormat(m_nCameraID, &poaImgFmt);

    if(error != POA_OK)
    {
        cerr << "get image format failed, error code: " << POAGetErrorString(error) << endl;
    }

    ImageFormat imgFmt;
    switch (poaImgFmt)
    {
    case POA_RAW8:
        imgFmt = RAW8;
        break;
    case POA_RAW16:
        imgFmt = RAW16;
        break;
    case POA_RGB24:
        imgFmt = RGB888;
        break;
    case POA_MONO8:
        imgFmt = MONO8;
        break;
    case POA_END:
        imgFmt = RAW8;
        break;
    }

    return imgFmt;
}

bool POACamera::setExposure(long expoUs, bool isAuto)
{
    POAConfigValue exposValue;
    exposValue.intValue = expoUs;

    POAErrors error = POASetConfig(m_nCameraID, POA_EXPOSURE, exposValue, isAuto ? POA_TRUE : POA_FALSE);

    if(error != POA_OK)
    {
        cerr << "set exposure failed, error code: " << POAGetErrorString(error) << endl;
        return false;
    }

    return true;
}

long POACamera::getExposure()
{
    POAConfigValue exposValue;

    POABool boolValue;

    POAErrors error = POAGetConfig(m_nCameraID, POA_EXPOSURE, &exposValue, &boolValue);

    if(error != POA_OK)
    {
        cerr << "get exposure failed, error code: " << POAGetErrorString(error) << endl;
        return -1;
    }

    return exposValue.intValue;
}

bool POACamera::setGain(long gain, bool isAuto)
{
    POAConfigValue gainValue;
    gainValue.intValue = gain;

    POAErrors error = POASetConfig(m_nCameraID, POA_GAIN, gainValue, isAuto ? POA_TRUE : POA_FALSE);

    if(error != POA_OK)
    {
        cerr << "set gain failed, error code: " << POAGetErrorString(error) << endl;
        return false;
    }

    return true;
}

long POACamera::getGain()
{
    POAConfigValue gainValue;

    POABool boolValue;

    POAErrors error = POAGetConfig(m_nCameraID, POA_GAIN, &gainValue, &boolValue);

    if(error != POA_OK)
    {
        cerr << "get gain failed, error code: " << POAGetErrorString(error) << endl;
        return -1;
    }

    return gainValue.intValue;
}

bool POACamera::startExposure()
{
    POAErrors error = POAStartExposure(m_nCameraID, POA_FALSE); // continuously exposure

    if(error != POA_OK)
    {
        cerr << "start exposure failed, error code: " << POAGetErrorString(error) << endl;
        return false;
    }

    return true;
}

bool POACamera::isImgDataAvailable()
{
    POABool pIsReady = POA_FALSE;

    POAErrors error = POAImageReady(m_nCameraID, &pIsReady);

    if(error != POA_OK)
    {
        return false;
    }

    return pIsReady == POA_TRUE ? true : false;
}

bool POACamera::getImageData(unsigned char *pDataBuffer, unsigned long size)
{
    long exposureUs = getExposure();
    POAErrors error = POAGetImageData(m_nCameraID, pDataBuffer, size, exposureUs /1000 + 500);

    return error == POA_OK ? true : false;
}

bool POACamera::stopExposure()
{
    POAErrors error = POAStopExposure(m_nCameraID);

    if(error != POA_OK)
    {
        cerr << "stop exposure failed, error code: " << POAGetErrorString(error) << endl;
        return false;
    }

    return true;
}

bool POACamera::closeCamera()
{
    POAErrors error = POACloseCamera(m_nCameraID);

    if(error != POA_OK)
    {
        cerr << "clsoe camera failed, error code: " << POAGetErrorString(error) << endl;
        return false;
    }

    return true;
}

int POACamera::getCameraID() const
{
    return m_nCameraID;
}

void POACamera::setCameraID(int nCameraID)
{
    m_nCameraID = nCameraID;
}
