#include <stdio.h>
#include <stdlib.h>
#include "toupcam.h"

HToupcam g_hcam = NULL;
void* g_pImageData = NULL;
unsigned g_total = 0;

static void __stdcall EventCallback(unsigned nEvent, void* pCallbackCtx)
{
    printf("event callback: 0x%04x\n", nEvent);
}

int main(int, char**)
{
    g_hcam = Toupcam_Open(NULL);
    if (NULL == g_hcam)
    {
        printf("no camera found or open failed\n");
        return -1;
    }
    if ((Toupcam_query_Model(g_hcam)->flag & TOUPCAM_FLAG_TRIGGER_SOFTWARE) == 0)
        printf("camera do NOT support software trigger, fallback to simulated trigger\n");

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
            Toupcam_put_AutoExpoEnable(g_hcam, 0);
            Toupcam_put_Option(g_hcam, TOUPCAM_OPTION_TRIGGER, 1);
            Toupcam_IoControl(g_hcam, 0, TOUPCAM_IOCONTROLTYPE_SET_TRIGGERSOURCE, 5, NULL);
            hr = Toupcam_StartPullModeWithCallback(g_hcam, EventCallback, NULL);
            if (FAILED(hr))
                printf("failed to start camera, hr = 0x%08x\n", hr);
            else
            {
                printf("'x' to exit, other to triggersync\n");
                do {
                    char str[1024];
                    int n = 1;
                    if (fgets(str, 1023, stdin))
                    {
                        if (('x' == str[0]) || ('X' == str[0]))
                            break;
                        else if ('\n' != str[0])
                            n = atoi(str);
                    }
                    
                    while (--n >= 0)
                    {
                        ToupcamFrameInfoV3 info = { 0 };
                        const HRESULT hr = Toupcam_TriggerSync(g_hcam, 0, g_pImageData, 24, 0, &info);
                        if (FAILED(hr))
                        {
                            printf("failed to triggersync, hr = 0x%08x\n", hr);
                            break;
                        }
                        else
                        {
                            /* After we get the image data, we can do anything for the data we want to do */
                            printf("triggersync ok, total = %u, res = %u x %u\n", ++g_total, info.width, info.height);
                        }
                    }
                } while (true);
            }
        }
    }
    
    /* cleanup */
    Toupcam_Close(g_hcam);
    if (g_pImageData)
        free(g_pImageData);
    return 0;
}
