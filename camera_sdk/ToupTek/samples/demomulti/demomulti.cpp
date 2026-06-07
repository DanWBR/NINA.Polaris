#include <stdio.h>
#include <stdlib.h>
#if defined(_WIN32)
#include <tchar.h>
#endif
#include "toupcam.h"

#if !defined(_WIN32)
#define _tprintf    printf
#define _T(x)       x
#endif

ToupcamDeviceV2 g_dev[TOUPCAM_MAX] = { 0 };
struct ctxCam {
    ToupcamDeviceV2* dev;
    HToupcam hcam;
    void* data;
    unsigned total;
} g_ctx[TOUPCAM_MAX] = { 0 };

static void __stdcall EventCallback(unsigned nEvent, void* pCallbackCtx)
{
    ctxCam* pctx = (ctxCam*)pCallbackCtx;
    if (TOUPCAM_EVENT_IMAGE == nEvent)
    {
        ToupcamFrameInfoV4 info = { 0 };
        const HRESULT hr = Toupcam_PullImageV4(pctx->hcam, pctx->data, 0, 24, 0, &info);
        if (FAILED(hr))
            _tprintf(_T("%s: failed to pull image, hr = 0x%08x\n"), pctx->dev->displayname, hr);
        else
        {
            /* After we get the image data, we can do anything for the data we want to do */
             _tprintf(_T("%s: pull image ok, total = %u, res = %u x %u\n"), pctx->dev->displayname, ++(pctx->total), info.v3.width, info.v3.height);
        }
    }
    else
    {
         _tprintf(_T("%s: event callback: 0x%04x\n"), pctx->dev->displayname, nEvent);
    }
}

int main(int, char**)
{
    unsigned num = Toupcam_EnumV2(g_dev);
    if (0 == num)
    {
        _tprintf(_T("no camera found\n"));
        return -1;
    }
    for (unsigned i = 0; i < num; ++i)
		_tprintf(_T("%s\n"), g_dev[i].displayname);

    for (unsigned i = 0; i < num; ++i)
    {
        g_ctx[i].dev = &g_dev[i];
        g_ctx[i].hcam = Toupcam_Open(g_dev[i].id);
        if (NULL == g_ctx[i].hcam)
            _tprintf(_T("%s: open failed\n"), g_ctx[i].dev->displayname);
        else
        {
            int nWidth = 0, nHeight = 0;
            HRESULT hr = Toupcam_get_Size(g_ctx[i].hcam, &nWidth, &nHeight);
            if (FAILED(hr))
                _tprintf(_T("%s: failed to get size, hr = 0x%08x\n"), g_ctx[i].dev->displayname, hr);
            else
            {
                g_ctx[i].data = malloc(TDIBWIDTHBYTES(24 * nWidth) * nHeight);
                if (NULL == g_ctx[i].data)
                    _tprintf(_T("%s: failed to malloc\n"), g_ctx[i].dev->displayname);
                else
                {
                    hr = Toupcam_StartPullModeWithCallback(g_ctx[i].hcam, EventCallback, (void*)&g_ctx[i]);
                    if (FAILED(hr))
                        _tprintf(_T("%s: failed to start camera, hr = 0x%08x\n"), g_ctx[i].dev->displayname, hr);
                }
            }
        }
    }

    _tprintf(_T("press ENTER to exit\n"));
    getc(stdin);

    /* cleanup */
    for (unsigned i = 0; i < num; ++i)
    {
        Toupcam_Close(g_ctx[i].hcam);
        if (g_ctx[i].data)
            free(g_ctx[i].data);
    }
    return 0;
}
