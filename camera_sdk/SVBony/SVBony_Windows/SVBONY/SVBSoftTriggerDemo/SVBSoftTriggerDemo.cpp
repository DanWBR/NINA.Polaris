// SVBSoftTriggerDemo.cpp : 定义控制台应用程序的入口点。
//

#include "stdafx.h"
#include "SVBCameraSDK.h"

#include <assert.h>

int main()
{
	int cameraNum = SVBGetNumOfConnectedCameras();
	printf("Scan camera number: %d\r\n", cameraNum);

	SVB_ERROR_CODE ret;
	int cameraID = -1;
	for (int i = 0; i < cameraNum; i++)
	{
		SVB_CAMERA_INFO cameraInfo;
		ret = SVBGetCameraInfo(&cameraInfo, i);
		if (ret == SVB_SUCCESS)
		{
			printf("Friendly name: %s\r\n", cameraInfo.FriendlyName);
			printf("Port type: %s\r\n", cameraInfo.PortType);
			printf("SN: %s\r\n", cameraInfo.CameraSN);
			printf("Device ID: 0x%x\r\n", cameraInfo.DeviceID);
			printf("Camera ID: %d\r\n", cameraInfo.CameraID);
			cameraID = cameraInfo.CameraID;
		}
	}

	if (cameraID == -1)
		return -1;

	//////////////////////////////////////
	// open the camera
	ret = SVBOpenCamera(cameraID);
	if (ret != SVB_SUCCESS)
	{
		printf("open camera failed.\r\n");
		return -1;
	}

	/////////////////////////////////////
	SVB_SN sn;
	ret = SVBGetSerialNumber(cameraID, &sn);
	assert(ret == SVB_SUCCESS);
	printf("sn: %s\r\n", sn.id);

	/////////////////////////////////////
	// get camera's property and print the information.
	SVB_CAMERA_PROPERTY cameraProperty;
	ret = SVBGetCameraProperty(cameraID, &cameraProperty);
	if (ret != SVB_SUCCESS)
	{
		printf("get camera property failed\r\n");
		SVBCloseCamera(cameraID);
		return -1;
	}
	printf("camera maximum width %ld\r\n", cameraProperty.MaxWidth);
	printf("camera maximum height %ld\r\n", cameraProperty.MaxHeight);
	printf("camera color space: %s\r\n", cameraProperty.IsColorCam ? "color" : "mono");
	printf("camera bayer pattern: %d\r\n", cameraProperty.BayerPattern);
	for (int i = 0; i < sizeof(cameraProperty.SupportedBins) / sizeof(cameraProperty.SupportedBins[0]); i++)
	{
		printf("support bin: %d\r\n", cameraProperty.SupportedBins[i]);
		if (cameraProperty.SupportedBins[i] == 0)
			break;
	}
	for (int i = 0; i < sizeof(cameraProperty.SupportedVideoFormat) / sizeof(cameraProperty.SupportedVideoFormat[0]); i++)
	{
		printf("support img type: %d\r\n", cameraProperty.SupportedVideoFormat[i]);
		if (cameraProperty.SupportedVideoFormat[i] == SVB_IMG_END)
			break;
	}
	printf("Max depth: %d\r\n", cameraProperty.MaxBitDepth);
	printf("Is trigger camera: %s\r\n", cameraProperty.IsTriggerCam ? "YES" : "NO");

	///////////////////////////////////////////////
	int controlsNum = 0;
	ret = SVBGetNumOfControls(cameraID, &controlsNum);
	assert(ret == SVB_SUCCESS);

	for (int i = 0; i < controlsNum; i++)
	{
		SVB_CONTROL_CAPS caps;
		ret = SVBGetControlCaps(cameraID, i, &caps);
		assert(ret == SVB_SUCCESS);
		printf("=================================\r\n");
		printf("control type: %d\r\n", caps.ControlType);
		printf("control name: %s\r\n", caps.Name);
		printf("control Description: %s\r\n", caps.Description);
		printf("Maximum value: %ld\r\n", caps.MaxValue);
		printf("minimum value: %ld\r\n", caps.MinValue);
		printf("default value: %ld\r\n", caps.DefaultValue);
		printf("is auto supported: %s\r\n", caps.IsAutoSupported ? "YES" : "NO");
		printf("is writable: %s\r\n", caps.IsWritable ? "YES" : "NO");
		printf(">>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>\r\n");
		long value;
		SVB_BOOL isAuto;
		ret = SVBGetControlValue(cameraID, caps.ControlType, &value, &isAuto);
		assert(ret == SVB_SUCCESS);
		printf("control type %d value %ld\r\n", caps.ControlType, value);
		printf("<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<\r\n");
	}

	////////////////////////////////////////////
	ret = SVBSetROIFormat(cameraID, 0, 0, cameraProperty.MaxWidth, cameraProperty.MaxHeight, 1);
	assert(ret == SVB_SUCCESS);
	int startX, startY, width, height, bin;
	ret = SVBGetROIFormat(cameraID, &startX, &startY, &width, &height, &bin);
	assert(ret == SVB_SUCCESS);
	printf("camera ROI, startX %d, startY %d, width %d, height %d, bin %d\r\n", startX, startY
		, width, height, bin);
	ret = SVBSetROIFormat(cameraID, 0, 0, 800, 600, 1);
	assert(ret == SVB_SUCCESS);
	ret = SVBGetROIFormat(cameraID, &startX, &startY, &width, &height, &bin);
	assert(ret == SVB_SUCCESS);
	printf("camera ROI, startX %d, startY %d, width %d, height %d, bin %d\r\n", startX, startY
		, width, height, bin);

	//////////////////////////////////////////////////
	float pixelSize;
	ret = SVBGetSensorPixelSize(cameraID, &pixelSize);
	if (ret == SVB_SUCCESS)
		printf("sensor pixel size %f\r\n", pixelSize);
	else if (ret == SVB_ERROR_UNKNOW_SENSOR_TYPE)
		printf("unknow sensor type\r\n");
	else if (ret == SVB_ERROR_INVALID_ID)
		printf("invalid camera id\r\n");
	else
		printf("other error\r\n");

	////////////////////////////////////////////////////////////////
	SVB_CAMERA_MODE cameraMode;
	ret = SVBGetCameraMode(cameraID, &cameraMode);
	assert(ret == SVB_SUCCESS);
	printf("cameraMode: %d\r\n", cameraMode);
	ret = SVBSetCameraMode(cameraID, SVB_MODE_TRIG_SOFT);
	assert(ret == SVB_SUCCESS);
	printf("set to soft trigger mode\r\n");

	////////////////////////////////////////////////////////////////
	SVB_IMG_TYPE imgType;
	ret = SVBGetOutputImageType(cameraID, &imgType);
	assert(ret == SVB_SUCCESS);
	printf("the current image type is %d\r\n", imgType);
	printf("prepare to set output image type to %d\r\n", cameraProperty.SupportedVideoFormat[0]);
	ret = SVBSetOutputImageType(cameraID, cameraProperty.SupportedVideoFormat[0]);
	assert(ret == SVB_SUCCESS);

	////////////////////////////////////////////////////////////////
	// start video capture
	ret = SVBStartVideoCapture(cameraID);
	assert(ret == SVB_SUCCESS);

	/////////////////////////////////////////////////////////////
	int bufferSize = (cameraProperty.MaxBitDepth + 7) / 8 * width * height;
	unsigned char *pBuffer = new unsigned char[bufferSize];
	int captureNum = 0;
	while (true)
	{
		/////////////////////////////////////////////////////////
		// send a soft trigger signal
		printf("send a soft trigger\r\n");
		ret = SVBSendSoftTrigger(cameraID);
		if (ret != SVB_SUCCESS)
		{
			printf("send soft trigger failed\r\n");
			continue;
		}

		////////////////////////////////////////////////////////
		// get video data
		ret = SVBGetVideoData(cameraID, pBuffer, bufferSize, 1000);
		if (ret != SVB_SUCCESS) {
			printf("get a image timeout\r\n");
			continue;
		}
		char path[256];
		sprintf_s(path, "SVB_image_%d.raw", captureNum);
		FILE *fp = NULL;
		fopen_s(&fp, path, "w");
		if (fp == NULL)
		{
			printf("open file %s failed\r\n", path);
			continue;
		}
		fwrite(pBuffer, bufferSize, 1, fp);
		fclose(fp);
		printf("write image file %s\r\n", path);

		////////////////////////////////////////////////////////////////
		int dropFrames = 0;
		ret = SVBGetDroppedFrames(cameraID, &dropFrames);
		assert(ret == SVB_SUCCESS);
		printf("drop frames: %d\r\n", dropFrames);

		captureNum++;
		if (captureNum >= 10)
			break;
	}

	/////////////////////////////////////////////////////////////
	// stop video capture
	printf("stop camera capture\r\n");
	ret = SVBStopVideoCapture(cameraID);
	assert(ret == SVB_SUCCESS);

	printf("close camera\r\n");
	SVBCloseCamera(cameraID);

	printf("press any key to quit\r\n");
	getchar();
	return 0;
}

