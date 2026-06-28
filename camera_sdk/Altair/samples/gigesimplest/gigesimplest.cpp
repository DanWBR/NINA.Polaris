#if !defined(_WIN32)
#include <unistd.h>
#endif
#include <stdio.h>
#include <stdlib.h>
#include "altaircam.h"

HAltaircam g_hcam = NULL;
void* g_pImageData = NULL;
unsigned g_total = 0;

static void __stdcall EventCallback(unsigned nEvent, void* pCallbackCtx)
{
	if (ALTAIRCAM_EVENT_IMAGE == nEvent)
	{
		AltaircamFrameInfoV4 info = { 0 };
		const HRESULT hr = Altaircam_PullImageV4(g_hcam, g_pImageData, 0, 24, 0, &info);
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
	Altaircam_GigeEnable(NULL, NULL); /* Enable GigE */
	do { /* wait to discover GigE camera and then open it */
		g_hcam = Altaircam_Open(NULL);
		if (g_hcam)
		{
#if defined(_WIN32)
			wprintf(L"open camera ok, model: %s\n", Altaircam_query_Model(g_hcam)->name);
#else
			printf("open camera ok, model: %s\n", Altaircam_query_Model(g_hcam)->name);
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
	
	Altaircam_put_Option(g_hcam, ALTAIRCAM_OPTION_RAW, 1);
	int nWidth = 0, nHeight = 0;
	HRESULT hr = Altaircam_get_FinalSize(g_hcam, &nWidth, &nHeight);
	if (FAILED(hr))
		printf("failed to get size, hr = 0x%08x\n", hr);
	else
	{
		g_pImageData = malloc(TDIBWIDTHBYTES(24 * nWidth) * nHeight);
		if (NULL == g_pImageData)
			printf("failed to malloc\n");
		else
		{
			hr = Altaircam_StartPullModeWithCallback(g_hcam, EventCallback, NULL);
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
	Altaircam_Close(g_hcam);
	if (g_pImageData)
		free(g_pImageData);
	return 0;
}
