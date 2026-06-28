#include <stdio.h>
#include <stdlib.h>
#include "altaircam.h"

HAltaircam g_hcam = NULL;
unsigned g_total = 0;

static void __stdcall EventCallback(unsigned nEvent, void* pCallbackCtx)
{
    if (ALTAIRCAM_EVENT_IMAGE == nEvent)
    {
        AltaircamImagePtr ptr = { 0 };
        const HRESULT hr = Altaircam_PullImagePtr(g_hcam, 0, &ptr);
        if (FAILED(hr))
            printf("failed to pull imageptr, hr = 0x%08x\n", hr);
        else
        {
            /* After we get the image data, we can do anything for the data we want to do */
            printf("pull imageptr ok, total = %u, res = %u x %u, id = %u\n", ++g_total, ptr.ptrInfo->v3.width, ptr.ptrInfo->v3.height, ptr.id);
            Altaircam_PushImagePtr(g_hcam, ptr.id);
        }
    }
    else
    {
        printf("event callback: 0x%04x\n", nEvent);
    }
}

int main(int, char**)
{
    g_hcam = Altaircam_Open(NULL);
    if (NULL == g_hcam)
    {
        printf("no camera found or open failed\n");
        return -1;
    }

    HRESULT hr = Altaircam_StartPullModeWithCallback(g_hcam, EventCallback, NULL);
    if (FAILED(hr))
        printf("failed to start camera, hr = 0x%08x\n", hr);
    else
    {
        printf("press ENTER to exit\n");
        getc(stdin);
    }

    /* cleanup */
    Altaircam_Close(g_hcam);
    return 0;
}
