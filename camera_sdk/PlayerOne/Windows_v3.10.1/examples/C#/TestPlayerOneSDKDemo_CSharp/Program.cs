using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.IO;
using static POA.PlayerOneSDK.PlayerOneCameraDLL;

//Please put the "PlayerOneCamera.dll" and "PlayerOneCamera_x64.dll" into the executable directory(bin/debug, bin/release)
//----->For more information, please read 'PlayerOneCamera.h' and development manual

namespace TestPlayerOneSDKDemo_CSharp
{
    class Program
    {
        static void Main(string[] args)
        {
            int cameraCount = POAGetCameraCount();

            Console.WriteLine("camera count: {0}", cameraCount);

            if(cameraCount <= 0)
            {
                return;
            }

            POAErrors error;

            List<POACameraProperties> camPropList = new List<POACameraProperties>();

            for(int i = 0; i < cameraCount; i++) // get all camera properties
            {
                POACameraProperties camProp;
                error = POAGetCameraProperties(i, out camProp); //get camaera properties

                if(error != POAErrors.POA_OK)
                {
                    Console.WriteLine("Get camera properties failed, index: {0}, error code: {1}", i, POAGetErrorString(error));

                    continue;
                }
                else
                {
                    Console.WriteLine("Camera ID: {0}, Camera Name: {1}, Camera SN: {2}, Camera Sensor Name: {3}", camProp.cameraID, camProp.cameraModelName, camProp.SN, camProp.sensorModelName); //print camera ID and camera name

                    Console.WriteLine("Max width: {0}, Max height: {1}", camProp.maxWidth, camProp.maxHeight);

                    Console.WriteLine("Is color camera: {0}, Is cool camera: {1}", Convert.ToBoolean(camProp.isColorCamera), Convert.ToBoolean(camProp.isHasCooler));
                }

                camPropList.Add(camProp);
            }

            
            //operate the first camera

            ////////////////////////////////////////////////open camera////////////////////////////////////////////////
            error = POAOpenCamera(camPropList[0].cameraID);
            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("Open camera failed！, error code:{0}", POAGetErrorString(error));
                return;
            }


            ////////////////////////////////////////////////init camera////////////////////////////////////////////////
            error = POAInitCamera(camPropList[0].cameraID);
            if (error != POAErrors.POA_OK) //This is just an example, regarding error handling, you can use your own method.
            {
                Console.WriteLine("Init camera failed！, error code: {0}", POAGetErrorString(error));
                return;
            }


            ////////////////////////////////////////////////get config Attributes////////////////////////////////////////////////
            int configCount = 0;
            error = POAGetConfigsCount(camPropList[0].cameraID, out configCount);
            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("Get config count failed！, error code: {0}", POAGetErrorString(error));
                return;
            }

            Console.WriteLine("Get config count: {0}", configCount);
            
            List<POAConfigAttributes> confAttriList = new List<POAConfigAttributes>();

            for (int i = 0; i < configCount; i++)
            {
                POAConfigAttributes confAttri;

                error = POAGetConfigAttributes(camPropList[0].cameraID, i, out confAttri);

                if (error == POAErrors.POA_OK)
                {
                    Console.WriteLine("");
                    Console.WriteLine("config name: {0}, config description: {1}", confAttri.szConfName, confAttri.szDescription);

                    Console.WriteLine("is writable: {0}", Convert.ToBoolean(confAttri.isWritable));

                    Console.WriteLine("is readable: {0}", Convert.ToBoolean(confAttri.isReadable));

                    if (confAttri.valueType == POAValueType.VAL_INT)
                    {
                        Console.WriteLine("min: {0}, max: {1}, default: {2}", confAttri.minValue.intValue, confAttri.maxValue.intValue, confAttri.defaultValue.intValue);
                    }
                    else if (confAttri.valueType == POAValueType.VAL_FLOAT)
                    {
                        Console.WriteLine("min: {0}, max: {1}, default: {2}", confAttri.minValue.floatValue, confAttri.maxValue.floatValue, confAttri.defaultValue.floatValue);
                    }
                    else if (confAttri.valueType == POAValueType.VAL_BOOL) // The maxValue and minValue values of this VAL_BOOL type are meaningless
                    {
                        Console.WriteLine("default is on: {0}", Convert.ToBoolean(confAttri.defaultValue.boolValue));
                    }
                }
                else
                {
                    Console.WriteLine("Get config attributes failed, index: {0}, error code: {1}", i, POAGetErrorString(error));
                }
            }
            Console.WriteLine("");

            ////////////////////////////////////////////////get config current value////////////////////////////////////////////////
            POAConfigValue exposureValue = new POAConfigValue();
            POABool isAuto;
            error = POAGetConfig(camPropList[0].cameraID, POAConfig.POA_EXPOSURE, out exposureValue, out isAuto);
            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("Get Exposure error: {0}", POAGetErrorString(error));
            }
            else
            {
                Console.WriteLine("Exposure: {0}", exposureValue.intValue);
            }

            POAConfigValue gainValue = new POAConfigValue();
            error = POAGetConfig(camPropList[0].cameraID, POAConfig.POA_GAIN, out gainValue, out isAuto);
            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("Get Gain error: {0}", POAGetErrorString(error));
            }
            else
            {
                Console.WriteLine("Gain: {0}", gainValue.intValue);
            }

            POAConfigValue offsetValue = new POAConfigValue();
            error = POAGetConfig(camPropList[0].cameraID, POAConfig.POA_OFFSET, out offsetValue, out isAuto);
            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("Get Offset error: {0}", POAGetErrorString(error));
            }
            else
            {
                Console.WriteLine("Offset: {0}", offsetValue.intValue);
            }

            int nExposure;
            bool bIsAuto;
            error = POAGetConfig(camPropList[0].cameraID, POAConfig.POA_EXPOSURE, out nExposure, out bIsAuto); //using the overload function
            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("Get Exposure error: {0}", POAGetErrorString(error));
            }
            else
            {
                Console.WriteLine("Exposure Again: {0}", nExposure);
            }

            ////////////////////////////////////////////////set camera parameters////////////////////////////////////////////////

            //Set image parameters, if exposing, please stop exposure first
            POACameraState cameraState;

            POAGetCameraState(camPropList[0].cameraID, out cameraState);

            if (cameraState == POACameraState.STATE_EXPOSING)
            {
                POAStopExposure(camPropList[0].cameraID);
            }

            //set bin, note: after setting bin, please get the image size and start position
            error = POASetImageBin(camPropList[0].cameraID, camPropList[0].bins[1]); // set bin to 2, default bin is 1

            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("set bin failed, error code: {0}", POAGetErrorString(error));
            }

            int startX = 0, startY = 0;
            int width = 0, height = 0;

            error = POAGetImageStartPos(camPropList[0].cameraID, out startX, out startY);
            if (error != POAErrors.POA_OK)
            {
                // if get image start postion failed, set startX and startY to 0
                startX = 0;
                startY = 0;
                Console.WriteLine("Get Image Start Pos failed, error code: {0}", POAGetErrorString(error));
            }

            error = POAGetImageSize(camPropList[0].cameraID, out width, out height);
            if (error != POAErrors.POA_OK)
            {
                // if get image size failed, set width and height to the maximum value under current bin
                width = camPropList[0].maxWidth / camPropList[0].bins[1]; // Maximum width under current bin
                height = camPropList[0].maxHeight / camPropList[0].bins[1]; // Maximum height under current bin
                Console.WriteLine("Get Image Size failed, error code: {0}", POAGetErrorString(error));
            }

            // set image size
            width -= 50;
            height -= 20;

            width = width / 4 * 4; // make sure width % 4 == 0;
            height = height / 2 * 2; // make sure height % 2 == 0;

            error = POASetImageSize(camPropList[0].cameraID, width, height); //default resolution is maxWidth * maxHeight
            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("set resolution failed, error code: {0}", POAGetErrorString(error));
            }

            // set start position
            startX += 20;
            startY += 10;
            error = POASetImageStartPos(camPropList[0].cameraID, startX, startY); //default start position is (0, 0)
            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("set start position failed, error code: {0}", POAGetErrorString(error));
            }


            //set image format, if exposing, please stop exposure first
            error = POASetImageFormat(camPropList[0].cameraID, POAImgFormat.POA_RAW16); //default image format is POA_RAW8
            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("set image format failed, error code: {0}", POAGetErrorString(error));
            }


            //set exposure (POA_EXPOSURE)，recommended to use POA_EXP for setting exposure, this maximum is 2000s
            int exposure_us = 1000000; //1000ms
            POAConfigValue exposure_value = new POAConfigValue();
            exposure_value.intValue = exposure_us;
            POABool isAutoExpo = POABool.POA_FALSE;
            error = POASetConfig(camPropList[0].cameraID, POAConfig.POA_EXPOSURE, exposure_value, isAutoExpo);
            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("set exposure failed, error code: {0}", POAGetErrorString(error));
            }
            
            //set exposure (POA_EXP)，recommended to use this for setting exposure, this maximum is 7200.0s
            double exp_s = 1.0; //1s
            POAConfigValue exp_value = new POAConfigValue();
            exp_value.floatValue = exp_s;
            error = POASetConfig(camPropList[0].cameraID, POAConfig.POA_EXP, exp_value, isAutoExpo);
            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("set exposure failed, error code: {0}", POAGetErrorString(error));
            }

            //set gain
            int gain = 100; //100
            POAConfigValue gain_value = new POAConfigValue();
            gain_value.intValue = gain;
            POABool isAutoGain = POABool.POA_FALSE;
            error = POASetConfig(camPropList[0].cameraID, POAConfig.POA_GAIN, gain_value, isAutoGain);
            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("set gain failed, error code: {0}", POAGetErrorString(error));
            }



            ////////////////////////////////////////////////start exposure and get image data////////////////////////////////////////////////
            // start exposure
            int buffer_size = width * height * 2; //raw16

            // https://docs.microsoft.com/zh-cn/dotnet/api/system.runtime.interopservices.marshal.allochglobal?redirectedfrom=MSDN&view=netframework-3.5#overloads
            IntPtr pBuf = Marshal.AllocHGlobal(buffer_size);

            POABool isSignalFrame = POABool.POA_FALSE;
            error = POAStartExposure(camPropList[0].cameraID, isSignalFrame); // continuously exposure(Video Mode)

            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("start exposure failed, error code: {0}", POAGetErrorString(error));
            }

            //get image data
            int img_cout = 10; //get image count

            byte[] managedArray = new byte[buffer_size];

            while (img_cout > 0) //or while(true),this is recommended to do in another thread
            {
                POABool pIsReady = POABool.POA_FALSE;
                while (pIsReady == POABool.POA_FALSE)
                {
                    //sleep(exposure_us /1000 / 10); //ms
                    POAImageReady(camPropList[0].cameraID, out pIsReady);
                }

                error = POAGetImageData(camPropList[0].cameraID, pBuf, buffer_size, exposure_us / 1000 + 500);
                if (error != POAErrors.POA_OK)
                {
                    Console.WriteLine("get image data failed, error code: {0}", POAGetErrorString(error));
                    continue;
                }

                //write the data as a binary file, you can use 3rdparty lib(opencv, libtiff or cfitsio) save data to a image

                BinaryWriter bw;
                string fileName = img_cout + "_raw16_image_data.bin";
                // create file
                try
                {
                    bw = new BinaryWriter(new FileStream(fileName,
                                    FileMode.Create));
                }
                catch (IOException e)
                {
                    Console.WriteLine(e.Message + "\n Cannot create file.");
                    return;
                }

                // write data buffer to file 
                try
                {
                  
                    Marshal.Copy(pBuf, managedArray, 0, buffer_size); //https://docs.microsoft.com/zh-cn/dotnet/api/system.runtime.interopservices.marshal.copy?view=netframework-3.5#System_Runtime_InteropServices_Marshal_Copy_System_IntPtr_System_Byte___System_Int32_System_Int32_
                    bw.Write(managedArray);
                    Console.WriteLine("saving data: {0}", fileName);

                }
                catch (IOException e)
                {
                    Console.WriteLine(e.Message + "\n Cannot write to file.");
                    return;
                }

                bw.Close();
                          
                img_cout--;
            }

            //stop exposure
            POAStopExposure(camPropList[0].cameraID);
            Console.WriteLine("Exposure Stopped !");

            // if long exposure and single frame(Snap Mode)
            exposure_us = 10000000; //10s
            exposure_value.intValue = exposure_us;
            error = POASetConfig(camPropList[0].cameraID, POAConfig.POA_EXPOSURE, exposure_value, POABool.POA_FALSE); //set exposure to 10s

            if (error != POAErrors.POA_OK)
            {
                Console.WriteLine("set exposure failed, error code: {0} \n", POAGetErrorString(error));
            }

            Console.WriteLine("start long exposure, single frame: \n");
            POAStartExposure(camPropList[0].cameraID, POABool.POA_TRUE); // single frame(Snap mode)
            Console.WriteLine("Please waiting...: \n");


            POACameraState cmeraState;
            do
            {
                //sleep(exposure_us / 10);
                //            if(breakTrigger)
                //            {
                //                break;
                //            }
                POAGetCameraState(camPropList[0].cameraID, out cmeraState);
            } while (cmeraState == POACameraState.STATE_EXPOSING);

            POABool pIsImgReady = POABool.POA_FALSE;

            POAImageReady(camPropList[0].cameraID, out pIsImgReady);

            if (pIsImgReady == POABool.POA_TRUE)
            {
                Console.WriteLine("single frame exposure success \n");
                error = POAGetImageData(camPropList[0].cameraID, pBuf, buffer_size, 500);
                if (error != POAErrors.POA_OK)
                {
                    Console.WriteLine("get image data failed, error code: {0} \n", POAGetErrorString(error));
                }
            }
            else
            {
                Console.WriteLine("single frame exposure failed \n");
            }

            // cool camera setting
            if(camPropList[0].isHasCooler == POABool.POA_TRUE) // if cool camera
            {
                // set cooler on
                bool bCoolerOne = true;
                POASetConfig(camPropList[0].cameraID, POAConfig.POA_COOLER, bCoolerOne);  // call the overload function
                Console.WriteLine("Set cooler on! \n");

                // set fan power to 80%, range:[0-100]
                int fanPower = 80;
                POASetConfig(camPropList[0].cameraID, POAConfig.POA_FAN_POWER, fanPower, false);
                Console.WriteLine("Set fan power to {0} \n", fanPower);

                // set lens heater on
                bool bHeaterOn = true;
                POASetConfig(camPropList[0].cameraID, POAConfig.POA_HEATER, bHeaterOn);
                Console.WriteLine("Set lens heater on! \n");

                // set lens heater power to 30% range:[0-100]
                int heaterPower = 30;
                POASetConfig(camPropList[0].cameraID, POAConfig.POA_HEATER_POWER, heaterPower, false);
                Console.WriteLine("Set heater power to {0} \n", heaterPower);

                // get cooler power (read only)
                int coolerPower = 0;
                bool isAutoCool = false;
                POAGetConfig(camPropList[0].cameraID, POAConfig.POA_COOLER_POWER, out coolerPower, out isAutoCool);
                Console.WriteLine("Get current cool power {0} \n", coolerPower);

            }
            else
            {
                Console.WriteLine("This camera is not cool camera \n");
            }

            // set sensor mode
            int senModeCount = 0;
            POAGetSensorModeCount(camPropList[0].cameraID, out senModeCount);
            if (senModeCount <= 0) // <=0 means camera does not support sensor mode setting
            {
                Console.WriteLine("This camera does not support sensor mode setting \n");
            }
            else
            {
                // In general, there are at least two sensor modes[Normal, LowNoise, ...]
                // get all sensor mode info
                for (int ii = 0; ii < senModeCount; ii++)
                {
                    POASensorModeInfo senModeInfo = new POASensorModeInfo();

                    if (POAGetSensorModeInfo(camPropList[0].cameraID, ii, out senModeInfo) == POAErrors.POA_OK)
                    {
                        Console.WriteLine("index: {0}, sensor mode name: {1}, description: {2} \n", ii, senModeInfo.name, senModeInfo.desc);
                    }
                }

                // get current sensor mode index
                int modeIndex = 0;
                POAGetSensorMode(camPropList[0].cameraID, out modeIndex);
                Console.WriteLine("Get current sensor mode index: {0} \n", modeIndex); // default index is 0

                // set mode index to 1
                modeIndex = 1;
                POASetSensorMode(camPropList[0].cameraID, modeIndex); //please stop exposure first, or this operation will abort the exposure
                Console.WriteLine("Set sensor mode index to {0} \n", modeIndex);
            }


            //close camera
            POACloseCamera(camPropList[0].cameraID);
            Console.WriteLine("Camera Closed!");

            //free memory
            Marshal.FreeHGlobal(pBuf);


            Console.ReadKey();
        }
    }
}
