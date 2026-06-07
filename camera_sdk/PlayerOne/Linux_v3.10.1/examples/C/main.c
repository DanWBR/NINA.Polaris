#include <stdio.h>
#include <stdlib.h>

#include "PlayerOneCamera.h"

/******************************************************************
 * if want to run this code using gcc or clang command,
 * please add a dependency of PlayerOneCamera library.
*******************************************************************/

int main()
{

    /*Note: please pay attention to the allocation and release of memory in your program*/

    int camera_count = POAGetCameraCount();

    printf("camera count: %d\n", camera_count);
    if(camera_count <= 0)
    { 
		printf("there is no Player One Camera!\n");

        getchar();

        return 0;
    }

    if(camera_count > 0)
    {
        //malloc memory first
        POACameraProperties **ppPOACamProp = (POACameraProperties **)malloc(sizeof(POACameraProperties *) * camera_count);

        int i;
        for(i = 0; i < camera_count; i++)
        {
            ppPOACamProp[i] = (POACameraProperties *)malloc(sizeof (POACameraProperties)); //malloc the POACameraProperties memory
            POAErrors error = POAGetCameraProperties(i, ppPOACamProp[i]); //get camaera properties
            if(error == POA_OK)
            {
                //print camera ID and name
                printf("camera ID: %d, camera name: %s \n", ppPOACamProp[i]->cameraID, ppPOACamProp[i]->cameraModelName);
                //print camera SN and Sensor Name
                printf("camera SN: %s, camera Sensor Name: %s \n", ppPOACamProp[i]->SN, ppPOACamProp[i]->sensorModelName);
            }
            else
            {
                printf("get camera properties failed, index: %d, error code: %s \n", i, POAGetErrorString(error));
            }
        }

        //operate the first camera

        POAErrors error;

        ////////////////////////////////////////////////open camera////////////////////////////////////////////////
        error = POAOpenCamera(ppPOACamProp[0]->cameraID);
        if(error != POA_OK)
        {
            printf("Open camera failed！, error code: %s \n", POAGetErrorString(error));
            return 0;
        }

        ////////////////////////////////////////////////init camera////////////////////////////////////////////////
        error = POAInitCamera(ppPOACamProp[0]->cameraID);
        if(error != POA_OK) //This is just an example, regarding error handling, you can use your own method.
        {
            printf("Init camera failed！, error code: %s \n", POAGetErrorString(error));
            return 0;
        }

        ////////////////////////////////////////////////get config Attribute////////////////////////////////////////////////
        int config_count = 0;
        error = POAGetConfigsCount(ppPOACamProp[0]->cameraID, &config_count);
        if(error != POA_OK)
        {
            printf("Get config count failed！, error code: %s \n", POAGetErrorString(error));
            return 0;
        }

        POAConfigAttributes **ppConfAttr = (POAConfigAttributes **)malloc(sizeof(POAConfigAttributes *) * config_count);

        for(i = 0; i < config_count; i++)
        {
            ppConfAttr[i] = (POAConfigAttributes *)malloc(sizeof (POAConfigAttributes));

            error = POAGetConfigAttributes(ppPOACamProp[0]->cameraID, i, ppConfAttr[i]);

            if(error == POA_OK)
            {
                printf("\n");
                printf("config name: %s, config description: %s \n", ppConfAttr[i]->szConfName, ppConfAttr[i]->szDescription);

                printf("is writable: %d \n", (int)ppConfAttr[i]->isWritable);

                printf("is readable: %d \n", (int)ppConfAttr[i]->isReadable);

                if(ppConfAttr[i]->valueType == VAL_INT)
                {
                    printf("min: %ld, max: %ld, default: %ld \n", ppConfAttr[i]->minValue.intValue, ppConfAttr[i]->maxValue.intValue, ppConfAttr[i]->defaultValue.intValue);
                }
                else if(ppConfAttr[i]->valueType == VAL_FLOAT)
                {
                    printf("min: %lf, max: %lf, default: %lf \n", ppConfAttr[i]->minValue.floatValue, ppConfAttr[i]->maxValue.floatValue, ppConfAttr[i]->defaultValue.floatValue);
                }
                else if(ppConfAttr[i]->valueType == VAL_BOOL) // The maxValue and minValue values of this VAL_BOOL type are meaningless
                {
                    printf("default is on: %d \n",  (int)ppConfAttr[i]->defaultValue.boolValue);
                }
            }
            else
            {
                printf("get config attributes failed, index: %d, error code: %s \n", i, POAGetErrorString(error));
            }
        }

        ////////////////////////////////////////////////set camera parameters////////////////////////////////////////////////

        //Set image parameters, if exposing, please stop exposure first

        POACameraState cameraState;

        POAGetCameraState(ppPOACamProp[0]->cameraID, &cameraState);

        if(cameraState == STATE_EXPOSING)
        {
            POAStopExposure(ppPOACamProp[0]->cameraID);
        }

        //set bin, note: after setting bin, please get the image size and start position
        error = POASetImageBin(ppPOACamProp[0]->cameraID, ppPOACamProp[0]->bins[1]); // set bin to 2, default bin is 1

        if(error != POA_OK)
        {
            printf("set bin failed, error code: %s \n", POAGetErrorString(error));
        }

        int startX = 0, startY = 0;
        int width = 0, height = 0;

        error = POAGetImageStartPos(ppPOACamProp[0]->cameraID, &startX, &startY);
        if(error != POA_OK)
        {
            // if get image start postion failed, set startX and startY to 0
            startX = 0;
            startY = 0;
            printf("Get Image Start Pos failed, error code: %s \n", POAGetErrorString(error));
        }

        error = POAGetImageSize(ppPOACamProp[0]->cameraID, &width, &height);
        if(error != POA_OK)
        {
            // if get image size failed, set width and height to the maximum value under current bin
            width = ppPOACamProp[0]->maxWidth / ppPOACamProp[0]->bins[1]; // Maximum width under current bin
            height = ppPOACamProp[0]->maxHeight / ppPOACamProp[0]->bins[1]; // Maximum height under current bin
            printf("Get Image Size failed, error code: %s \n", POAGetErrorString(error));
        }

        // set image size
        width -= 50;
        height -= 20;

        width = width / 4 * 4; // make sure width % 4 == 0;
        height = height / 2 * 2; // make sure height % 2 == 0;

        error = POASetImageSize(ppPOACamProp[0]->cameraID, width, height); //default resolution is maxWidth * maxHeight
        if(error != POA_OK)
        {
            printf("set resolution failed, error code: %s \n", POAGetErrorString(error));
        }

        // set start position
        startX += 20;
        startY += 10;
        error = POASetImageStartPos(ppPOACamProp[0]->cameraID, startX, startY); //default start position is (0, 0)
        if(error != POA_OK)
        {
            printf("set start position failed, error code: %s \n", POAGetErrorString(error));
        }


        //set image format, if exposing, please stop exposure first
        error = POASetImageFormat(ppPOACamProp[0]->cameraID, POA_RAW16); //default image format is POA_RAW8
        if(error != POA_OK)
        {
            printf("set image format failed, error code: %s \n", POAGetErrorString(error));
        }

        //set exposure
        int exposure_us = 100000; //100ms
        POAConfigValue exposure_value;
        exposure_value.intValue = exposure_us;
        error = POASetConfig(ppPOACamProp[0]->cameraID, POA_EXPOSURE, exposure_value, POA_FALSE); // set exposure to 100ms (100000us), not auto, this maximum is 2000000000 us(2000s), recommended to use POA_EXP
        
        double exp_s = 0.1; // 100ms
        POAConfigValue exp_s_value;
        exp_s_value.floatValue = exp_s;
		error = POASetConfig(ppPOACamProp[0]->cameraID, POA_EXP, exp_s_value, POA_FALSE); // set exposure to 0.1s (100000us), not auto, this maximum is (7200s), recommended to use this for setting exposure
		
        if(error != POA_OK)
        {
            printf("set exposure failed, error code: %s \n", POAGetErrorString(error));
        }

        //set gain
        int gain = 100; //100
        POAConfigValue gain_value;
        gain_value.intValue = gain;
        error = POASetConfig(ppPOACamProp[0]->cameraID, POA_GAIN, gain_value, POA_FALSE);

        if(error != POA_OK)
        {
            printf("set exposure failed, error code: %s \n", POAGetErrorString(error));
        }


        ////////////////////////////////////////////////start exposure and get image data////////////////////////////////////////////////
        // start exposure
        long buffer_size = width * height * 2; //raw16
        unsigned char* data_buffer = (unsigned char*) malloc(buffer_size);

        error = POAStartExposure(ppPOACamProp[0]->cameraID, POA_FALSE); // continuously exposure

        if(error != POA_OK)
        {
            printf("start exposure failed, error code: %s \n", POAGetErrorString(error));
        }


        //get image data
        int img_cout = 10; //get image count

        while(img_cout > 0) //or while(1),this is recommended to do in another thread
        {
            POABool pIsReady = POA_FALSE;
            while(pIsReady == POA_FALSE)
            {
                //sleep(exposure_us /1000 / 10); //ms
                POAImageReady(ppPOACamProp[0]->cameraID, &pIsReady);
            }

            error = POAGetImageData(ppPOACamProp[0]->cameraID, data_buffer, buffer_size, exposure_us /1000 + 500);
            if(error != POA_OK)
            {
                printf("get image data failed, error code: %s \n", POAGetErrorString(error));
                continue;
            }

            //write the data as a binary file, you can use 3rdparty lib(opencv, libtiff or cfitsio) save data to a image
            char str[80];
            sprintf(str, "%d_raw16_image_data.bin", img_cout);

            printf("saving data: %s \n", str);


            FILE *fp = fopen(str,"wb+");

            fwrite(data_buffer,  1, buffer_size, fp);
            fclose(fp);

            img_cout--;
        }

        //stop exposure
        POAStopExposure(ppPOACamProp[0]->cameraID);

        // if long exposure and single frame(Snap mode)
        exposure_us = 5000000; //5s
        exposure_value.intValue = exposure_us;
        error = POASetConfig(ppPOACamProp[0]->cameraID, POA_EXPOSURE, exposure_value, POA_FALSE); //set exposure to 5s
        
        exp_s = 5.0; // 5s
        exp_s_value.floatValue = exp_s;
		error = POASetConfig(ppPOACamProp[0]->cameraID, POA_EXP, exp_s_value, POA_FALSE); // recommended to use POA_EXP for setting exposure, this maximum is 7200.0s

        if(error != POA_OK)
        {
            printf("set exposure failed, error code: %s \n", POAGetErrorString(error));
        }

        printf("start long exposure, single frame: \n");
        POAStartExposure(ppPOACamProp[0]->cameraID, POA_TRUE); // single frame(Snap mode)
        printf("Please waiting 5s...: \n");

        POACameraState cmeraState;
        do
        {
            //sleep(exposure_us / 10);
//            if(breakTrigger)
//            {
//                break;
//            }
            POAGetCameraState(ppPOACamProp[0]->cameraID, &cmeraState);
        }while(cmeraState == STATE_EXPOSING);

        POABool pIsReady = POA_FALSE;

        POAImageReady(ppPOACamProp[0]->cameraID, &pIsReady);

        if(pIsReady == POA_TRUE)
        {
            printf("single frame exposure success \n");
            error = POAGetImageData(ppPOACamProp[0]->cameraID, data_buffer, buffer_size, exposure_us /1000 + 500);
            if(error != POA_OK)
            {
                printf("get image data failed, error code: %s \n", POAGetErrorString(error));
            }
        }
        else
        {
            printf("single frame exposure failed \n");
        }

        exposure_us = 100000; //100ms
        exposure_value.intValue = exposure_us;
        error = POASetConfig(ppPOACamProp[0]->cameraID, POA_EXPOSURE, exposure_value, POA_FALSE); //set exposure to 100ms

        printf("set exposure to: %d ms\n", exposure_us/1000);
        
        error = POAStartExposure(ppPOACamProp[0]->cameraID, POA_FALSE); //continuously exposure(Video Mode)
		if(error == POA_OK)
		{
            int pixelBytes = 2; //POA_RAW8, POA_MONO8: pixelBytes = 1, POA_RAW16: pixelBytes = 2, POA_RGB24: pixelBytes = 3
            unsigned long buffer_size = width * height * pixelBytes;
			unsigned char* data_buffer = (unsigned char*) malloc(buffer_size); //Memory must be allocated first
			
			int count = 0; //if get image count > 20, will exit loop
			while(1) 
			{
				if(count++ >20)
				{
					break;
				}
                 printf("get image data: %d \n", count);
				 POABool pIsReady = POA_FALSE;
				 while(pIsReady == POA_FALSE)
				 {
					 //if(bIsStop) //Triggered by external conditions, such as clicking a button
					 //{break;}
					 //sleep(exposure_us /1000 / 10); //ms
					 POAImageReady(ppPOACamProp[0]->cameraID, &pIsReady);
				 }
				 //if(bIsStop) ////Triggered by external conditions, such as clicking a button
				 //{break;}
				 // mutex.lock: It is recommended to use a lock to protect data_buffer
				 error = POAGetImageData(ppPOACamProp[0]->cameraID, data_buffer, buffer_size, exposure_us /1000 + 500);
				 // mutex.unlock
				 if(error != POA_OK)
				 {
					 ////Handling errors
					 printf("get image data failed, error code: %s \n", POAGetErrorString(error));
					 continue;
				 }
			}
		}
		else
		{
			printf("Video Mode: start exposure failed, error code: %s \n", POAGetErrorString(error));
		}

        POAStopExposure(ppPOACamProp[0]->cameraID);

        // cool camera setting
        if(ppPOACamProp[0]->isHasCooler == POA_TRUE) // if cool camera
        {
            // set cooler on
            POAConfigValue cooler_on_value;
            cooler_on_value.boolValue = POA_TRUE;
            POASetConfig(ppPOACamProp[0]->cameraID, POA_COOLER, cooler_on_value, POA_FALSE);
            printf("Set cooler on! \n");

            // set fan power to 80%, range:[0-100]
            POAConfigValue fan_power_value;
            fan_power_value.intValue = 80;
            POASetConfig(ppPOACamProp[0]->cameraID, POA_FAN_POWER, fan_power_value, POA_FALSE);
            printf("Set fan power to %ld \n", fan_power_value.intValue);

            // set lens heater on (deprecated)
            //POAConfigValue heater_on_value;
            //heater_on_value.boolValue = POA_TRUE;
            //POASetConfig(ppPOACamProp[0]->cameraID, POA_HEATER, heater_on_value, POA_FALSE);
            //printf("Set lens heater on! \n");

            // set lens heater power to 30% range:[0-100]
            POAConfigValue heater_power_value;
            heater_power_value.intValue = 30;
            POASetConfig(ppPOACamProp[0]->cameraID, POA_HEATER_POWER, heater_power_value, POA_FALSE);
            printf("Set heater power to %ld \n", heater_power_value.intValue);

            // get cooler power (read only)
            POAConfigValue cooler_power_value;
            cooler_power_value.intValue = 0;
            POABool isAutoCool = POA_FALSE;
            POAGetConfig(ppPOACamProp[0]->cameraID, POA_COOLER_POWER, &cooler_power_value, &isAutoCool);
            printf("Get current cool power %ld \n", cooler_power_value.intValue);

        }
        else
        {
            printf("This camera is not cool camera \n");
        }

        // set sensor mode
        int senModeCount = 0;
        POAGetSensorModeCount(ppPOACamProp[0]->cameraID, &senModeCount);
        if (senModeCount <= 0) // <=0 means camera does not support sensor mode setting
        {
            printf("This camera does not support sensor mode setting \n");
        }
        else
        {
            // In general, there are at least two sensor modes[Normal, LowNoise, ...]
            // get all sensor mode info
            int ii;
            POASensorModeInfo *pSenModeInfo = (POASensorModeInfo *)malloc(sizeof(POASensorModeInfo));
            for (ii = 0; ii < senModeCount; ii++)
            {
                if (POAGetSensorModeInfo(ppPOACamProp[0]->cameraID, ii, pSenModeInfo) == POA_OK)
                {
                    printf("index: %d, sensor mode name: %s, description: %s \n", ii, pSenModeInfo->name, pSenModeInfo->desc);
                }
            }
            free(pSenModeInfo);

            // get current sensor mode index
            int modeIndex = 0;
            POAGetSensorMode(ppPOACamProp[0]->cameraID, &modeIndex);
            printf("Get current sensor mode index: %d \n", modeIndex); // default index is 0

            // set mode index to 1
            modeIndex = 1;
            POASetSensorMode(ppPOACamProp[0]->cameraID, modeIndex); //please stop exposure first, or this operation will abort the exposure
            printf("Set sensor mode index to %d \n", modeIndex);
        }

        //close camera
        POACloseCamera(ppPOACamProp[0]->cameraID);
        printf("camera closed! \n");

        // free the data buffer
        free(data_buffer);

    }


    getchar();

    return 0;
}
