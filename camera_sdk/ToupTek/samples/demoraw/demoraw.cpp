#include <stdio.h>
#include <stdlib.h>
#if defined(_WIN32)
#include <conio.h>
#else
#include <curses.h>
#endif
#include "toupcam.h"

HToupcam g_hcam = NULL;
void* g_pImageData = NULL;
unsigned g_total = 0, g_totalstill = 0;
bool g_bBitdepth = false, g_bSave = false;

static void SaveRaw(const char* filePrefix, unsigned num, unsigned width, unsigned height, const void* pData, unsigned length)
{
    char filename[1024];
    sprintf(filename, "%s_%ux%u_%u.raw", filePrefix, width, height, num);
    FILE* fp = fopen(filename, "wb");
    if (fp)
    {
        fwrite(pData, 1, length, fp);
        fclose(fp);
    }
    printf("saveraw: %s\n", filename);
}

static void __stdcall EventCallback(unsigned nEvent, void* pCallbackCtx)
{
    if (TOUPCAM_EVENT_IMAGE == nEvent)
    {
        ToupcamFrameInfoV4 info = { 0 };
        const HRESULT hr = Toupcam_PullImageV4(g_hcam, g_pImageData, 0, 0, 0, &info);
        if (FAILED(hr))
            printf("failed to pull image, hr = 0x%08x\n", hr);
        else
        {
            printf("pull image ok, total = %u, resolution = %u x %u\n", ++g_total, info.v3.width, info.v3.height);
            if (g_bSave)
            {
                g_bSave = false;
                SaveRaw("demoraw", g_total, info.v3.width, info.v3.height, g_pImageData, info.v3.width * info.v3.height * (g_bBitdepth ? 2 : 1));
            }
        }
    }
    else if (TOUPCAM_EVENT_STILLIMAGE == nEvent)
    {
        ToupcamFrameInfoV4 info = { 0 };
        const HRESULT hr = Toupcam_PullImageV4(g_hcam, g_pImageData, 1, 0, 0, &info);
        if (FAILED(hr))
            printf("failed to pull still image, hr = 0x%08x\n", hr);
        else
        {
            printf("pull still image ok, total = %u, resolution = %u x %u\n", ++g_totalstill, info.v3.width, info.v3.height);
            SaveRaw("demorawstill", g_totalstill, info.v3.width, info.v3.height, g_pImageData, info.v3.width * info.v3.height * (g_bBitdepth ? 2 : 1));
        }
    }
    else
    {
        printf("event callback: 0x%04x\n", nEvent);
    }
}

int main(int, char**)
{
    Toupcam_GigeEnable(NULL, NULL); /* Enable GigE */
    do { /* wait to discover GigE camera and then open it */
        g_hcam = Toupcam_Open(NULL);
        if (g_hcam)
        {
#if defined(_WIN32)
            wprintf(L"open camera ok, model: %s\n", Toupcam_query_Model(g_hcam)->name);
#else
            printf("open camera ok, model: %s\n", Toupcam_query_Model(g_hcam)->name);
#endif
            break;
        }
        printf("wait to find camera\n");
#if defined(_WIN32)
        Sleep(1000);
#else
        sleep(1);
#endif
    } while (1);

    HRESULT hr = Toupcam_put_Option(g_hcam, TOUPCAM_OPTION_RAW, -1); /* use raw image data */
    printf("put option raw: hr = 0x%08x\n", hr);

    Toupcam_put_Option(g_hcam, TOUPCAM_OPTION_BITDEPTH, 0); /* enable bitdepth */    

    int nMaxWidth = 0, nMaxHeight = 0;
    hr = Toupcam_get_Resolution(g_hcam, 0, &nMaxWidth, &nMaxHeight);
    if (FAILED(hr))
        printf("failed to get size, hr = 0x%08x\n", hr);
    else
    {
        g_pImageData = malloc(nMaxWidth * nMaxHeight * (g_bBitdepth ? 2 : 1));
        if (NULL == g_pImageData)
            printf("failed to malloc\n");
        else
        {
            hr = Toupcam_StartPullModeWithCallback(g_hcam, EventCallback, NULL);
            if (FAILED(hr))
                printf("failed to start camera, hr = 0x%08x\n", hr);
            else
            {
                bool bloop = true;
                while (bloop)
                {
                    printf("press s to save raw, any other key to exit\n");
                    switch (getch())
                    {
                    case 'x':
                    case 'X':
                        bloop = false;
                        break;
                    case 's':
                    case 'S':
                        g_bSave = true;
                        break;
                    default:
                            bloop = false;
                        break;
                    }
                }
            }
        }
    }
    
    /* cleanup */
    Toupcam_Close(g_hcam);
    if (g_pImageData)
        free(g_pImageData);
    return 0;
}
