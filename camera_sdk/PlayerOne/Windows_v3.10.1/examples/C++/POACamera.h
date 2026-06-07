#ifndef POACAMERA_H
#define POACAMERA_H

#include <map>
#include <string>

using namespace std;

/*******************************************************************************
This is a simple class that shows how to customize a your own camera class,
For more information about camera setting , please refer to the C example.
*******************************************************************************/


struct ROIArea //custom a ROI rectangle area
{
    int startX;
    int startY;
    int width;
    int height;

    ROIArea()
    {
        startX = 0;
        startY = 0;
        width = 0;
        height = 0;
    }
};

class POACamera
{
public:
    POACamera();

    POACamera(int nCameraID);

    virtual ~POACamera();

public:
    static map<int, string> getALLCameraIDName();

    enum ImageFormat
    {
        RAW8,
        RAW16,
        RGB888,
        MONO8
    };


    bool openCamera();

    bool initCamera();

    void getAllConfigAttributes();

    bool setROIArea(const ROIArea &roiArea);

    ROIArea getROIArea();

    bool setImageSize(int width, int height);

    bool setImageStartPos(int startX, int startY);

    bool setImageFormat(ImageFormat imgFmt);

    ImageFormat getImageFormat();

    bool setExposure(long expoUs, bool isAuto); //Microsecond

    long getExposure();

    bool setGain(long gain, bool isAuto);

    long getGain();

    bool startExposure();

    bool isImgDataAvailable();

    bool getImageData(unsigned char *pDataBuffer, unsigned long size);

    bool stopExposure();

    bool closeCamera();

    int getCameraID() const;

    void setCameraID(int nCameraID);

private:
    int m_nCameraID;
};

#endif // POACAMERA_H
