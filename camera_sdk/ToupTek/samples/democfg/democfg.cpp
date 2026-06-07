#include <stdio.h>
#include <stdlib.h>
#include "toupcam.h"

HToupcam g_hcam = NULL;
void* g_pImageData = NULL;
unsigned g_total = 0;

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
            printf("pull image ok, total = %u, res = %u x %u\n", ++g_total, info.v3.width, info.v3.height);
        }
    }
    else
    {
        printf("event callback: 0x%04x\n", nEvent);
    }
}

int main(int, char**)
{
    ToupcamDeviceV2 arr[TOUPCAM_MAX];
    unsigned cnt = Toupcam_EnumV2(arr);
    if (0 == cnt)
    {
        printf("no camera found\n");
        return -1;
    }

#if defined(_WIN32)
    wchar_t camId[MAX_PATH];
    swprintf(camId, L"%s;ini=%s", arr[0].id, L"c:\\a.ini");
#else
    char camId[PATH_MAX];
    sprintf(camId, "%s;ini=%s", arr[0].id, "/usr/local/a.ini");
#endif
    g_hcam = Toupcam_Open(camId);
    if (NULL == g_hcam)
    {
        printf("open failed\n");
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
            if (FAILED(hr))
                printf("failed to start camera, hr = 0x%08x\n", hr);
            else
            {
                printf("press ENTER to exit\n");
                getc(stdin);
            }
        }
    }
    
    /* cleanup */
    Toupcam_Close(g_hcam);
    if (g_pImageData)
        free(g_pImageData);
    return 0;
}
