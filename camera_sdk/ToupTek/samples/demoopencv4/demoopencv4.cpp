#include <stdio.h>
#include <string.h>
#include "opencv2/core/core_c.h"
#include "opencv2/imgcodecs.hpp"
#include "opencv2/highgui.hpp"
#include "opencv2/imgproc.hpp"
#include "toupcam.h"

HToupcam g_hcam = NULL;
void* g_pImageData = NULL;
unsigned g_total = 0;
bool g_bSave = false;
unsigned g_maxExpoTime = 0, g_defExpoTime = 0, g_minExpoTime = 0;
cv::Mat g_image;

//Convert data stream to Mat format then save image
bool Convert2Mat(unsigned char* pData, cv::Mat& srcImage, unsigned nWidth, unsigned nHeight)
{
    if (NULL == pData)
    {
        printf("NULL info or data.\n");
        return false;
    }
    srcImage = cv::Mat(nHeight, nWidth, CV_8UC3, pData);

    if (NULL == srcImage.data)
    {
        printf("Creat Mat failed.\n");
        return false;
    }

    return true;
}

static void removeln(char* str)
{
    char* endstr = strchr(str, '\n');
    if (endstr)
        *endstr = '\0';
}

//rackbar callback function
static void on_Expotime(int pos, void*) {
    if (pos * 1000 < g_minExpoTime)
        Toupcam_put_ExpoTime(g_hcam, g_minExpoTime);
    else
        Toupcam_put_ExpoTime(g_hcam, pos * 1000);
}

static void __stdcall EventCallback(unsigned nEvent, void* pCallbackCtx)
{
    if (TOUPCAM_EVENT_IMAGE == nEvent)
    {
        ToupcamFrameInfoV4 info = { 0 };
        const HRESULT hr = Toupcam_PullImageV4(g_hcam, g_pImageData, 0, 24, 0, &info);
        if (FAILED(hr))
            printf("failed to pull image, hr = 0x%08x\n", hr);
        else
        {
            /* After we get the image data, we can do anything for the data we want to do */
            ++g_total;
            if (Convert2Mat((unsigned char*)g_pImageData, g_image, info.v3.width, info.v3.height))
            {
                if (g_total == 1)
                {
                    int def = 2;
                    cv::namedWindow("demoOpenCV4", cv::WINDOW_NORMAL);
                    cv::createTrackbar("Expotime", "demoOpenCV4", &def, 350, on_Expotime);
                }
                cv::imshow("demoOpenCV4", g_image);
                cv::waitKey(1);
            }
            if (g_bSave)
            {
                try 
                {
                    //Save converted image in a local file
                    char path[128];
                    sprintf(path, "Image_%d.bmp", g_total);
                    cv::imwrite(path, g_image);
                    printf("succesd to save image\n");
                }
                catch (cv::Exception& ex) 
                {
                    printf("Exception in saving mat image: %s\n", ex.what());
                }
                g_bSave = false;
            }
        }
    }
}

int main(int, char**)
{
    g_hcam = Toupcam_Open(NULL);
    if (NULL == g_hcam)
    {
        printf("no camera found or open failed\n");
        return -1;
    }
    int nWidth = 0, nHeight = 0;
    HRESULT hr = Toupcam_get_Size(g_hcam, &nWidth, &nHeight);
    if (FAILED(hr))
        printf("failed to get size, hr = 0x%08x\n", hr);
    else
    {
        g_pImageData = malloc(TDIBWIDTHBYTES(24 * nWidth) * nHeight);
        if (NULL == g_pImageData)
            printf("failed to malloc\n");
        else
        {
            hr = Toupcam_StartPullModeWithCallback(g_hcam, EventCallback, NULL);
            
            Toupcam_get_ExpTimeRange(g_hcam, &g_minExpoTime, &g_maxExpoTime, &g_defExpoTime);
            
            if (FAILED(hr))
                printf("failed to start camera, hr = 0x%08x\n", hr);
            char str[1024];
            do {
                printf("Please input [s/S] to save image ([x/X] to exit):\n");
                if (fgets(str, 1023, stdin))
                {
                    removeln(str);
                    if ('s' == str[0] || 'S' == str[0])
                        g_bSave = true;
                    else if ('x' == str[0] || 'X' == str[0])
                        break;
                }
            } while (true);
        }
    }
    /* cleanup */
    Toupcam_Close(g_hcam);
    cv::destroyAllWindows();
    if (g_pImageData)
        free(g_pImageData);
    return 0;
}