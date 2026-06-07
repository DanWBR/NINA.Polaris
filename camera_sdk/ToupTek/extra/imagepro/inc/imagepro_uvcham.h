#ifndef __imagepro_uvcham_H__
#define __imagepro_uvcham_H__

#if defined(_WIN32)
#pragma pack(push, 8)

#ifdef __cplusplus
extern "C"
{
#endif

IMAGEPRO_API(HRESULT) imagepro_stitch_pullham(HImageproStitch handle, HUvcham h, int bFeed, int width, int height, void* pFrameBuffer);
IMAGEPRO_API(HRESULT) imagepro_edf_pullham(HImageproEdf handle, HUvcham h, int bFeed, int width, int height, void* pFrameBuffer);

#ifdef __cplusplus
}
#endif

#pragma pack(pop)
#endif
#endif
