#ifndef __imagepro_toupcam_H__
#define __imagepro_toupcam_H__

#if defined(_WIN32)
#pragma pack(push, 8)
#endif

#ifdef __cplusplus
extern "C"
{
#endif

IMAGEPRO_API(HRESULT) imagepro_stitch_pull(HImageproStitch handle, HToupcam h, int bFeed, void* pImageData, int bits, int rowPitch, ToupcamFrameInfoV2* pInfo);
IMAGEPRO_API(HRESULT) imagepro_stitch_pullV3(HImageproStitch handle, HToupcam h, int bFeed, void* pImageData, int bits, int rowPitch, ToupcamFrameInfoV3* pInfo);
IMAGEPRO_API(HRESULT) imagepro_stitch_pullV4(HImageproStitch handle, HToupcam h, int bFeed, void* pImageData, int bits, int rowPitch, ToupcamFrameInfoV4* pInfo);

IMAGEPRO_API(HRESULT) imagepro_edf_pull(HImageproEdf handle, HToupcam h, int bFeed, void* pImageData, int bits, int rowPitch, ToupcamFrameInfoV2* pInfo);
IMAGEPRO_API(HRESULT) imagepro_edf_pullV3(HImageproEdf handle, HToupcam h, int bFeed, void* pImageData, int bits, int rowPitch, ToupcamFrameInfoV3* pInfo);
IMAGEPRO_API(HRESULT) imagepro_edf_pullV4(HImageproEdf handle, HToupcam h, int bFeed, void* pImageData, int bits, int rowPitch, ToupcamFrameInfoV4* pInfo);

IMAGEPRO_API(HRESULT) imagepro_spot_pull(HImageproSpot handle, HToupcam h, int bFeed, void* pImageData, int bits, int rowPitch, ToupcamFrameInfoV2* pInfo, BeamPacket* result);

#ifdef __cplusplus
}
#endif

#if defined(_WIN32)
#pragma pack(pop)
#endif
#endif
