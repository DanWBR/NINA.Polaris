#ifndef __imagepro_H__
#define __imagepro_H__

#if defined(_WIN32)
#ifdef IMAGEPRO_EXPORTS
#define IMAGEPRO_API(r) __declspec(dllexport) r __cdecl
#else
#define IMAGEPRO_API(r) __declspec(dllimport) r __cdecl
#endif
#pragma pack(push, 8)
#else
#define __cdecl
#define IMAGEPRO_API(r) r
#endif

#ifdef __cplusplus
extern "C"
{
#endif

/* pfun_imagepro_malloc(size_t) used by imagepro.dll to malloc memory:
    
    static void* ipmalloc(size_t size)
    {
        return malloc(size);
    }
    
    imagepro_init(ipmalloc);
*/
typedef void* (__cdecl *pfun_imagepro_malloc)(size_t);
IMAGEPRO_API(void) imagepro_init(pfun_imagepro_malloc pfun);

#if !defined(_WIN32)
#ifndef __BITMAPINFOHEADER_DEFINED__
#define __BITMAPINFOHEADER_DEFINED__
typedef struct {
    unsigned        biSize;
    int             biWidth;
    int             biHeight;
    unsigned short  biPlanes;
    unsigned short  biBitCount;
    unsigned        biCompression;
    unsigned        biSizeImage;
    int             biXPelsPerMeter;
    int             biYPelsPerMeter;
    unsigned        biClrUsed;
    unsigned        biClrImportant;
} BITMAPINFOHEADER;
#endif
#endif

/* inputImage, outputImage: pointer to BITMAPINFOHEADER
    method:
        AUTO      = -1, (AREA for shrink, CUBIC for enlarge)
        NN        = 0,
        LINEAR    = 1,
        CUBIC     = 2,
        AREA      = 3,
        LANCZOS4  = 4
*/
IMAGEPRO_API(HRESULT) imagepro_resizeV1(void* srcImage, void* destImage, int method);
IMAGEPRO_API(HRESULT) imagepro_resizeV2(BITMAPINFOHEADER* pSrc, int srcStep, void* srcImage, BITMAPINFOHEADER* pDest, int destStep, void* destImage, int method);
/*
* informat: fourcc
* outformat: BGR(0), BGRA(1), 2(RGB), 3(RGBA)
* method:
*   LINEAR  = 0
*   VNG     = 1
*   EA      = 2
*/
IMAGEPRO_API(HRESULT) imagepro_demosaic(const void* inputImage, void* ouptImage, unsigned width, unsigned height, unsigned bitdepth, unsigned informat, unsigned outformat, unsigned method);

enum eImageproFormat {
    eImageproFormat_RGB24,
    eImageproFormat_RGB48,
    eImageproFormat_RGBA32,
    eImageproFormat_RGBA64
};

enum eImageproStitchRet {
    eImageproStitchRet_EMPTY,
    eImageproStitchRet_NORMAL,
    eImageproStitchRet_NORMAL_REF
};

enum eImageproStitchQuality {
    eImageproStitchQ_ZERO,
    eImageproStitchQ_GOOD,
    eImageproStitchQ_CAUTION,
    eImageproStitchQ_BAD,
    eImageproStitchQ_WARNING
};

enum eImageproStitchStatus {
    eImageproStitchS_NONE,
    eImageproStitchS_NORMAL,
    eImageproStitchS_AREPAIR, //auto repair
    eImageproStitchS_MREPAIR, //manual repair
    eImageproStitchS_RESET,
    eImageproStitchS_RESTART
};

enum eImageproStitchDirection {
    eImageproStitchD_STILL,
    eImageproStitchD_PLUS,
    eImageproStitchD_MINUS
};

enum eImageproStitchEvent {
    eImageproStitchE_NONE,
    eImageproStitchE_ERROR,
    eImageproStitchE_NOMEM, /* out of memory */
    eImageproStitchE_EXPAND,
    eImageproStitchE_EXPAND_FAILURE,
    eImageproStitchE_EXPAND_SUCCESS,
    eImageproStitchE_ENTER_NORMAL,
    eImageproStitchE_ENTER_AREPAIR,
    eImageproStitchE_LEAVE_AREPAIR,
    eImageproStitchE_ENTER_MREPAIR,
    eImageproStitchE_LEAVE_MREPAIR,
    eImageproStitchE_ENTER_RESET,
    eImageproStitchE_LEAVE_RESET,
    eImageproStitchE_ENTER_RESTART,
    eImageproStitchE_LEAVE_RESTART,
    eImageproStitchE_AREPAIR_STOP_X,
    eImageproStitchE_AREPAIR_STOP_Y,
    eImageproStitchE_AREPAIR_KEEP_X,
    eImageproStitchE_AREPAIR_KEEP_Y,
    eImageproStitchE_AREPAIR_REVERSE_X,
    eImageproStitchE_AREPAIR_REVERSE_Y,
    eImageproStitchE_AREPAIR_RIGHT_DIR,
    eImageproStitchE_MREPAIR_START_MOVING,
    eImageproStitchE_MREPAIR_REF_FAILURE,
    eImageproStitchE_MREPAIR_RETRY,
    eImageproStitchE_RESTART_START
};

enum eImageproStitchPrecision {
    eImageproStitchP_Lower,
    eImageproStitchP_Low,
    eImageproStitchP_Medium,
    eImageproStitchP_High,
    eImageproStitchP_Higher
};

enum eImageproStitchThreshold {
    eImageproStitchT_Smaller,
    eImageproStitchT_Small,
    eImageproStitchT_Medium,
    eImageproStitchT_Large,
    eImageproStitchT_Larger
};

typedef void (__cdecl *IMAGEPRO_STITCH_CALLBACK)(void* ctx, void* outData, int stride, int outW, int outH, int curW, int curH, int curType,
                                        int posX, int posY, eImageproStitchQuality quality, float sharpness, int bUpdate, int bSize);
typedef void (__cdecl *IMAGEPRO_STITCH_ECALLBACK)(void* ctx, eImageproStitchEvent evt);

typedef struct ImageproStitch_t { int unused; } *HImageproStitch;

/*
    Create new stitch handle
    format: Buffer pixel format
    bGlobalShutter: Whether the camera use global shutter, this parameter is currently ineffective
    videoW, viedoH: Buffer width and height
    background: Output image background, 0: transparent, 1: white, 2: black
    pFun, pEFun: Stitch handle callback function
    ctx: User context pointer, passed in when calling callback function
    precision: The precision level of the input image during stitching, affects both the processing speed and the stitching precision
    threshold: Stitch threshold level, larger threshold improves output quality, but reduces success rate and requires higher-quality input images
*/
IMAGEPRO_API(HImageproStitch) imagepro_stitch_new(int bGlobalShutter, int videoW, int videoH, int background, IMAGEPRO_STITCH_CALLBACK pFun, IMAGEPRO_STITCH_ECALLBACK pEFun, void* ctx);
IMAGEPRO_API(HImageproStitch) imagepro_stitch_newV2(eImageproFormat format, int bGlobalShutter, int videoW, int videoH, int background, IMAGEPRO_STITCH_CALLBACK pFun, IMAGEPRO_STITCH_ECALLBACK pEFun, void* ctx);
IMAGEPRO_API(HImageproStitch) imagepro_stitch_newV3(eImageproFormat format, int bGlobalShutter, int videoW, int videoH, int background, eImageproStitchPrecision precision, eImageproStitchThreshold threshold, IMAGEPRO_STITCH_CALLBACK pFun, IMAGEPRO_STITCH_ECALLBACK pEFun, void* ctx);
IMAGEPRO_API(void) imagepro_stitch_cropparams(HImageproStitch handle, int* roix, int* roiy, int* roiw, int* roih);
IMAGEPRO_API(void) imagepro_stitch_delete(HImageproStitch handle);
IMAGEPRO_API(void) imagepro_stitch_start(HImageproStitch handle);
IMAGEPRO_API(void*) imagepro_stitch_stop(HImageproStitch handle, int normal, int crop);
IMAGEPRO_API(void) imagepro_stitch_readdata(HImageproStitch handle, void* data, int w, int h, int roix = 0, int roiy = 0, int roiw = 0, int roih = 0);

enum eImageproEdfMethod {
    eImageproEdfM_Pyr_Max,
    eImageproEdfM_Pyr_Weighted,
    eImageproEdfM_Stack
};

enum eImageproEdfEvent {
    eImageproEdf_NONE,
    eImageproEdf_ERROR,
    eImageproEdf_NOMEM /* out of memory */
};

enum eImageproEdfMode {
    eImageproEdf_Auto,
    eImageproEdf_Manual
};

typedef void (__cdecl *IMAGEPRO_EDF_CALLBACK)(void* ctx, int result, void* outData, int stride, int outW, int outH, int outType);
typedef void (__cdecl *IMAGEPRO_EDF_ECALLBACK)(void* ctx, eImageproEdfEvent evt);

typedef struct ImageproEdf_t { int unused; } *HImageproEdf;
IMAGEPRO_API(HImageproEdf) imagepro_edf_new(eImageproEdfMethod method, IMAGEPRO_EDF_CALLBACK pEdfFun, IMAGEPRO_EDF_ECALLBACK pEventFun, void* ctx);
IMAGEPRO_API(HImageproEdf) imagepro_edf_newV2(eImageproFormat format, eImageproEdfMethod method, IMAGEPRO_EDF_CALLBACK pEdfFun, IMAGEPRO_EDF_ECALLBACK pEventFun, void* ctx);
IMAGEPRO_API(void) imagepro_edf_delete(HImageproEdf handle);
IMAGEPRO_API(void) imagepro_edf_start(HImageproEdf handle);
IMAGEPRO_API(void) imagepro_edf_stop(HImageproEdf handle);
IMAGEPRO_API(void) imagepro_edf_readdata(HImageproEdf handle, void* data, int stride);

typedef struct{
    double x, y;
    double rx, ry;
} ApertureSt;

typedef struct{
    double allenergy;
    double centroidX;
    double centroidY;
    double beamWidthX;
    double beamWidthY;
    double ellipticity;
    ApertureSt aperture;
} BeamPacket;

typedef struct ImageproSpot_t { int unused; } *HImageproSpot;
IMAGEPRO_API(HImageproSpot) imagepro_spot_new(int spotnum, int maxpacket);
IMAGEPRO_API(void) imagepro_spot_reset(HImageproSpot handle);
IMAGEPRO_API(void) imagepro_spot_setAperture(HImageproSpot handle, ApertureSt aperture);
IMAGEPRO_API(void) imagepro_spot_delete(HImageproSpot handle);

#if defined(_WIN32)
#define IMAGEPRO_LIVESTACK_NUM_MIN 1
#define IMAGEPRO_LIVESTACK_NUM_MAX 99
#define IMAGEPRO_LIVESTACK_NUM_DEF 10

typedef struct { unsigned unused; } *HLivestack;
typedef enum {
    eImageproLivestackErrorNONE,
    eImageproLivestackErrorEVALFAIL,
    eImageproLivestackErrorNOENOUGHSTARS,
    eImageproLivestackErrorNOENOUGHMATCHES
} eImageproLivestackError;

typedef void (*IMAGEPRO_LIVESTACK_CALLBACK)(void* ctx, int width, int height, int type, eImageproLivestackError err, void* data);

typedef enum {
    eImageproLivestackModeNONE,
    eImageproLivestackModeSTACK,
    eImageproLivestackModeMEAN
} eImageproLivestackMode;

typedef enum {
    eImageproLivestackTypeNONE,
    eImageproLivestackTypePLANET,
    eImageproLivestackTypeDEEPSKY
} eImageproLivestackType;

IMAGEPRO_API(HLivestack) imagepro_livestack_new(eImageproLivestackMode mode, eImageproLivestackType type, IMAGEPRO_LIVESTACK_CALLBACK pfun, void* ctx);
IMAGEPRO_API(void) imagepro_livestack_delete(HLivestack handle);
IMAGEPRO_API(void) imagepro_livestack_start(HLivestack handle);
IMAGEPRO_API(void) imagepro_livestack_stop(HLivestack handle);
IMAGEPRO_API(void) imagepro_livestack_ref(HLivestack handle, void* data, const int width, const int height, const int depth);
IMAGEPRO_API(void) imagepro_livestack_add(HLivestack handle, void* data, const int width, const int height, const int depth);
IMAGEPRO_API(void) imagepro_livestack_setalign(HLivestack handle, int align); /* align: true or false */
IMAGEPRO_API(void) imagepro_livestack_setnum(HLivestack handle, int num);
#endif

#ifdef __cplusplus
}
#endif

#if defined(_WIN32)
#pragma pack(pop)
#endif

#endif
