#include <iostream>
#include <sstream>
#include <fstream>

#include "POACamera.h"

/******************************************************************
 * if want to run this code,
 * please add a dependency of PlayerOneCamera library into your project
 * For more information about camera setting , please refer to the C example.
*******************************************************************/


int main()
{

    /*Note: please pay attention to the allocation and release of memory in your program*/

    map<int, string> allCameraIDName = POACamera::getALLCameraIDName();

    map<int, string>::iterator  iter;
    for(iter = allCameraIDName.begin(); iter != allCameraIDName.end(); iter++)
    {
        std::cout<<iter->first<<"  "<<iter->second<< std::endl;
    }

    if(allCameraIDName.size() > 0)
    {
        //operate the first camera
        map<int, string>::iterator  iter = allCameraIDName.begin();
        POACamera *pCamera = new POACamera(iter->first);

        //print all config attributes
        if(!pCamera->openCamera())
        {
            //handle error
            std::cout << "open camera failed!" << std::endl;
        }

        if(!pCamera->initCamera())
        {
            //handle error
            std::cout << "init camera failed!" << std::endl;
        }

        pCamera->getAllConfigAttributes();

        if(!pCamera->setImageSize(800, 480)) //set image sieze: 800 * 480
        {
            std::cout << "set image size failed!" << std::endl;
        }

        if(!pCamera->setImageStartPos(100, 100)) //set start position:(100, 100)
        {
            std::cout << "set start position failed!" << std::endl;
        }

        if(!pCamera->setImageFormat(POACamera::RAW8)) //set image format raw8)
        {
            std::cout << "set image format failed!" << std::endl;
        }

        if(!pCamera->setExposure(100000, false)) //set exposure 100ms
        {
            std::cout << "set exposure failed!" << std::endl;
        }

        if(!pCamera->setGain(50, false)) //set gain 50
        {
            std::cout << "set gain failed!" << std::endl;
        }

        if(!pCamera->startExposure())
        {
            std::cout << "start exposure failed!" << std::endl;
        }

        unsigned char *pDataBuffer = new  unsigned char[800 * 480]; // width * height *1(RAW8)

        //get image data
        int img_cout = 10; //get image count

        while(img_cout > 0) //or while(true),this is recommended to do in another thread,eg: std::thread(c++11)
        {
            while(!pCamera->isImgDataAvailable())
            {
                // better to sleep
                //sleep(exposure_us /1000 / 10); //ms
            }

            if(!pCamera->getImageData(pDataBuffer, 800 * 480))
            {
                std::cout << "get image data failed!" << std::endl;
                continue;
            }

            std::stringstream fileNameStream;
            fileNameStream << img_cout << "_raw8_image_data.bin";
            std::string fileName = fileNameStream.str();

            //write the data as a binary file, you can use 3rdparty lib(opencv, libtiff or cfitsio) save data to a image
            std::ofstream outFile(fileName, std::ios::out | std::ios::binary);
            std::cout << "writing: " << fileName << std::endl;
            outFile.write(reinterpret_cast<char*>(pDataBuffer), 800 * 480);
            outFile.close();

            img_cout--;
        }

        pCamera->closeCamera();

        std::cout << "camera closed!" << std::endl;

        delete [] pDataBuffer;
        pDataBuffer = nullptr;

        delete pCamera;
        pCamera = nullptr;
    }


    getchar();
    return 0;
}
