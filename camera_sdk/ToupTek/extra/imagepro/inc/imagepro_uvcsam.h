#ifndef __imagepro_uvcsam_H__
#define __imagepro_uvcsam_H__

#if defined(_WIN32)
#pragma pack(push, 8)

#ifdef __cplusplus
extern "C"
{
#endif

IMAGEPRO_API(HRESULT) imagepro_stitch_pullsam(HImageproStitch handle, HUvcsam h, int bFeed, int width, int height, void* pFrameBuffer);
IMAGEPRO_API(HRESULT) imagepro_edf_pullsam(HImageproEdf handle, HUvcsam h, int bFeed, int width, int height, void* pFrameBuffer);

#ifdef __cplusplus
}
#endif

#pragma pack(pop)
#endif
#endif
