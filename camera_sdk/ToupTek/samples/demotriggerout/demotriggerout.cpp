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
    ToupcamDeviceV2 arr[TOUPCAM_MAX] = { 0 };
    unsigned cnt = Toupcam_EnumV2(arr);
    if (0 == cnt)
    {
        printf("no camera found or open failed\n");
        printf("press ENTER to exit\n");
        getc(stdin);
        return -1;
    }
    for (unsigned i = 0; i < cnt; ++i)
    {
        if (arr[i].model->flag & TOUPCAM_FLAG_TRIGGER_EXTERNAL)
        {
#if defined(_WIN32)
            printf("%ls\n", arr[i].displayname);
#else
            printf("%s\n", arr[i].displayname);
#endif
            g_hcam = Toupcam_Open(arr[i].id);
            if (NULL == g_hcam)
            {
                printf("failed to open camera\n");
                printf("press ENTER to exit\n");
                getc(stdin);
                return -1;
            }
            break;
        }
    }
    if (NULL == g_hcam)
    {
        printf("no camera supports external trigger\n");
        printf("press ENTER to exit\n");
        getc(stdin);
        return -1;
    }

    int nWidth = 0, nHeight = 0, trigger_source = -1, external_source = -1, outputMode = -1;
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
            Toupcam_put_ExpoTime(g_hcam, 250000);
            hr = Toupcam_put_Option(g_hcam, TOUPCAM_OPTION_TRIGGER, 2);
            if (FAILED(hr))
                printf("failed to set external trigger mode, hr = 0x%08x\n", hr);
            else
            {
                char str[1024];
                printf("select trigger source:\n0-> Opto-isolated input\n1-> Software\n");
                do {
                    if (fgets(str, 1023, stdin))
                    {
                        switch (str[0])
                        {
                        case '0': trigger_source = 0; break;
                        case '1': trigger_source = 5; break;
                        default: trigger_source = -1; break;
                        }
                        if (-1 == trigger_source)
                            printf("bad input\n");
                        else
                        {
                            Toupcam_IoControl(g_hcam, 0, TOUPCAM_IOCONTROLTYPE_SET_TRIGGERSOURCE, trigger_source, NULL);//select trigger source
                            Toupcam_IoControl(g_hcam, 2, TOUPCAM_IOCONTROLTYPE_SET_GPIODIR, 0x01, NULL);//GPIO_0 -> output
                            Toupcam_IoControl(g_hcam, 3, TOUPCAM_IOCONTROLTYPE_SET_GPIODIR, 0x01, NULL);//GPIO_1 -> output
                            Toupcam_IoControl(g_hcam, 1, TOUPCAM_IOCONTROLTYPE_SET_OUTPUTMODE, 0x00, NULL);//opt_out -> Trigger wait signal
                            Toupcam_IoControl(g_hcam, 2, TOUPCAM_IOCONTROLTYPE_SET_OUTPUTMODE, 0x01, NULL);//GPIO_0 -> Exposure effective signal
                            Toupcam_IoControl(g_hcam, 3, TOUPCAM_IOCONTROLTYPE_SET_OUTPUTMODE, 0x02, NULL);//GPIO_1 -> Strobe signal
                            Toupcam_IoControl(g_hcam, 3, TOUPCAM_IOCONTROLTYPE_SET_STROBEDELAYMODE, 0x01, NULL);//strobe delay mode
                            Toupcam_IoControl(g_hcam, 3, TOUPCAM_IOCONTROLTYPE_SET_STROBEDELAYTIME, 500000, NULL);//strobe delay time
                            break;
                        }
                    }
                } while (true);

                hr = Toupcam_StartPullModeWithCallback(g_hcam, EventCallback, NULL);
                if (FAILED(hr))
                    printf("failed to start camera, hr = 0x%08x\n", hr);
                else
                {
                    if (5 == trigger_source)
                        printf("'x' to exit, number to trigger\n");
                    else
                        printf("'x' to exit\n");
                    do {
                        if (fgets(str, 1023, stdin))
                        {
                            if (('x' == str[0]) || ('X' == str[1]))
                                break;
                            if (5 == trigger_source)
                            {
                                int n = atoi(str);
                                if (n > 0)
                                    Toupcam_Trigger(g_hcam, n);
                            }
                        }
                    } while (true);
                }
            }
        }
    }

    printf("press ENTER to exit\n");
    getc(stdin);

    /* cleanup */
    Toupcam_Close(g_hcam);
    if (g_pImageData)
        free(g_pImageData);
    return 0;
}
