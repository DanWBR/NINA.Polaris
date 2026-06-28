#include <stdio.h>
#include <stdlib.h>
#include "altaircam.h"

HAltaircam g_hcam = NULL;
void* g_pImageData = NULL;
unsigned g_total = 0;

int main(int, char**)
{
    g_hcam = Altaircam_Open(NULL);
    if (NULL == g_hcam)
    {
        printf("no camera found or open failed\n");
        return -1;
    }
    
    int nWidth = 0, nHeight = 0;
    HRESULT hr = Altaircam_get_Size(g_hcam, &nWidth, &nHeight);
    if (FAILED(hr))
        printf("failed to get size, hr = 0x%08x\n", hr);
    else
    {
        g_pImageData = malloc(TDIBWIDTHBYTES(24 * nWidth) * nHeight);
        if (NULL == g_pImageData)
            printf("failed to malloc\n");
        else
        {
            hr = Altaircam_StartPullModeWithCallback(g_hcam, NULL, NULL);
            if (FAILED(hr))
                printf("failed to start camera, hr = 0x%08x\n", hr);
            else
            {
                printf("press Ctrl-C to exit\n");
                while (true)
                {
                    AltaircamFrameInfoV3 info = { 0 };
                    const HRESULT hr = Altaircam_WaitImageV3(g_hcam, 1000, g_pImageData, 0, 24, 0, &info);
                    if (hr == 0x8001011f)
                        printf("pull image timeout\n");
                    else if (FAILED(hr))
                        printf("failed to pull image, hr = 0x%08x\n", hr);
                    else
                    {
                        /* After we get the image data, we can do anything for the data we want to do */
                        printf("pull image ok, total = %u, res = %u x %u\n", ++g_total, info.width, info.height);
                    }
                }
            }
        }
    }
    
    /* cleanup */
    Altaircam_Close(g_hcam);
    if (g_pImageData)
        free(g_pImageData);
    return 0;
}
