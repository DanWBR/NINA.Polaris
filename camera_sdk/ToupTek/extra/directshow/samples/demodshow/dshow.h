#ifndef __dshow_h__
#define __dshow_h__

#include <vector>
#include <string>
#include <dshow.h>
#include <Dshowasf.h>
#include <wmsdk.h>
#include <InitGuid.h>
#include "toupcam_dshow.h"

#define SAFE_RELEASE(x)	\
do {					\
	if ((x))			\
	{					\
		(x)->Release();	\
		(x) = NULL;		\
	}					\
} while(0)

interface ISampleGrabberCB : public IUnknown
{
	virtual STDMETHODIMP SampleCB(double SampleTime, IMediaSample *pSample) = 0;
	virtual STDMETHODIMP BufferCB(double SampleTime, BYTE *pBuffer, long BufferLen) = 0;
};

static const IID IID_ISampleGrabberCB = { 0x0579154A, 0x2B53, 0x4994, { 0xB0, 0xD0, 0xE7, 0x73, 0x14, 0x8E, 0xFF, 0x85 } };
interface ISampleGrabber: public IUnknown
{
	virtual HRESULT STDMETHODCALLTYPE SetOneShot(BOOL OneShot) = 0;
	virtual HRESULT STDMETHODCALLTYPE SetMediaType(const AM_MEDIA_TYPE *pType) = 0;
	virtual HRESULT STDMETHODCALLTYPE GetConnectedMediaType( AM_MEDIA_TYPE *pType) = 0;
	virtual HRESULT STDMETHODCALLTYPE SetBufferSamples(BOOL BufferThem) = 0;
	virtual HRESULT STDMETHODCALLTYPE GetCurrentBuffer(long *pBufferSize, long *pBuffer) = 0;
	virtual HRESULT STDMETHODCALLTYPE GetCurrentSample(IMediaSample **ppSample) = 0;
	virtual HRESULT STDMETHODCALLTYPE SetCallback(ISampleGrabberCB *pCallback, long WhichMethodToCallback) = 0;
};

#ifndef __ICaptureGraphBuilder2_INTERFACE_DEFINED__
#define __ICaptureGraphBuilder2_INTERFACE_DEFINED__
MIDL_INTERFACE("93E5A4E0-2D50-11d2-ABFA-00A0C9C6E38D")
ICaptureGraphBuilder2 : public IUnknown
{
public:
    virtual HRESULT STDMETHODCALLTYPE SetFiltergraph(
        /* [in] */ IGraphBuilder *pfg) = 0;
    
    virtual HRESULT STDMETHODCALLTYPE GetFiltergraph(
        /* [out] */
        __out  IGraphBuilder **ppfg) = 0;
    
    virtual HRESULT STDMETHODCALLTYPE SetOutputFileName(
        /* [in] */ const GUID *pType,
        /* [in] */ LPCOLESTR lpstrFile,
        /* [out] */
        __out  IBaseFilter **ppf,
        /* [out] */
        __out  IFileSinkFilter **ppSink) = 0;
    
    virtual /* [local] */ HRESULT STDMETHODCALLTYPE FindInterface(
        /* [in] */
        __in_opt  const GUID *pCategory,
        /* [in] */
        __in_opt  const GUID *pType,
        /* [in] */ IBaseFilter *pf,
        /* [in] */ REFIID riid,
        /* [out] */
        __out  void **ppint) = 0;
    
    virtual HRESULT STDMETHODCALLTYPE RenderStream(
        /* [in] */
        __in_opt  const GUID *pCategory,
        /* [in] */ const GUID *pType,
        /* [in] */ IUnknown *pSource,
        /* [in] */ IBaseFilter *pfCompressor,
        /* [in] */ IBaseFilter *pfRenderer) = 0;
    
    virtual HRESULT STDMETHODCALLTYPE ControlStream(
        /* [in] */ const GUID *pCategory,
        /* [in] */ const GUID *pType,
        /* [in] */ IBaseFilter *pFilter,
        /* [in] */
        __in_opt  REFERENCE_TIME *pstart,
        /* [in] */
        __in_opt  REFERENCE_TIME *pstop,
        /* [in] */ WORD wStartCookie,
        /* [in] */ WORD wStopCookie) = 0;
    
    virtual HRESULT STDMETHODCALLTYPE AllocCapFile(
        /* [in] */ LPCOLESTR lpstr,
        /* [in] */ DWORDLONG dwlSize) = 0;
    
    virtual HRESULT STDMETHODCALLTYPE CopyCaptureFile(
        /* [in] */
        __in  LPOLESTR lpwstrOld,
        /* [in] */
        __in  LPOLESTR lpwstrNew,
        /* [in] */ int fAllowEscAbort,
        /* [in] */ IAMCopyCaptureFileProgress *pCallback) = 0;
    
    virtual HRESULT STDMETHODCALLTYPE FindPin(
        /* [in] */ IUnknown *pSource,
        /* [in] */ PIN_DIRECTION pindir,
        /* [in] */
        __in_opt  const GUID *pCategory,
        /* [in] */
        __in_opt  const GUID *pType,
        /* [in] */ BOOL fUnconnected,
        /* [in] */ int num,
        /* [out] */
        __out  IPin **ppPin) = 0;
};
#endif

static const IID IID_ISampleGrabber = { 0x6B652FFF, 0x11FE, 0x4fce, { 0x92, 0xAD, 0x02, 0x66, 0xB5, 0xD7, 0xC7, 0x8F } };
static const CLSID CLSID_SampleGrabber = { 0xC1F400A0, 0x3F08, 0x11d3, { 0x9F, 0x0B, 0x00, 0x60, 0x08, 0x03, 0x9E, 0x37 } };
static const CLSID CLSID_NullRenderer = { 0xC1F400A4, 0x3F08, 0x11d3, { 0x9F, 0x0B, 0x00, 0x60, 0x08, 0x03, 0x9E, 0x37 } };

class TDshowContext
{
	const std::wstring		m_devname;
	const HWND				m_hParent;
	const HWND				m_hOwner;
	const UINT				m_NotifyMsg;
	
	std::wstring			m_strFullPath;

	IMoniker*				m_pVideoMoniker;
	
	ICaptureGraphBuilder2*	m_pBuilder;
    IVideoWindow*			m_pVW;
    IMediaEventEx*			m_pME;
    IAMVfwCaptureDialogs*	m_pDlg;

    IAMStreamConfig*		m_pAMStreamConfig;      // for video cap
    IBaseFilter*			m_pRender;
    IBaseFilter*			m_pSource;
    IGraphBuilder*			m_pFg;
	IBaseFilter*			m_pSampleGrabber;

	IAMVideoControl*		m_pStillImageVideoControl;
	IPin*					m_pStillImagePin;
	IBaseFilter*			m_pStillSampleGrabber;
	IAMStreamConfig*		m_pStillImageAMStreamConfig;

    int						m_iVCapDialogPos;
    int						m_iVCapCapturePinDialogPos;
	int						m_iVStillImagePinDialogPos;

    BOOL					m_fPreviewGraphBuilt;
    BOOL					m_fPreviewing;
    BOOL					m_fCaptureGraphBuilt;
    BOOL					m_fCapturing;

	BOOL					m_bFullscreen;
public:
	TDshowContext(const std::wstring& devname, HWND hParent, HWND hOwner, IMoniker* pVideoMoniker, UINT NotifyMsg);
public:
	BOOL IsCapturing()const { return m_fCapturing; }
	BOOL IsPreviewing()const { return m_fPreviewing; }
public:
	HRESULT start(HMENU hMenuSub1, UINT pos, UINT times, UINT cmdID);
	void stop();
	HRESULT startcapture(const wchar_t* szFullPath);
	void stopcapture();

	void on_notify();
	void on_dialog(int id);
	BOOL video_size(LONG* lWidth, LONG* lHeight);
	void resize_window();
	BOOL full_screen();
	void move_window();
	double get_framerate();

	HRESULT set_roi(unsigned xOffset, unsigned yOffset, unsigned xWidth, unsigned yHeight);
	HRESULT get_roi(unsigned* pxOffset, unsigned* pyOffset, unsigned* pxWidth, unsigned* pyHeight);

	BOOL preview_snapshot(const wchar_t* filename);
	BOOL stillimage_supported();
	BOOL stillimage_snapshot(const wchar_t* filename);

	HRESULT queryinterface(const IID& riid,  void** ppvObj);
private:
	BOOL MakeBuilder();
	BOOL MakeGraph();
	HRESULT BuildPreviewGraph();
	BOOL BuildCaptureGraph();
	BOOL StartPreview();
	BOOL StartCapture();
	BOOL RenderStillPin();
	HRESULT InitCapFilter();
	BOOL StopPreview();
	BOOL StopCapture();
	void TearDownGraph();
	void FreeCapFilter();
	void NukeDownStream(IBaseFilter* pf);
	void MakeMenuOption(BOOL bAdd, HMENU hMenuSub1, UINT pos, UINT times, UINT cmdID);
	BOOL Snapshot(IBaseFilter* pSampleGrabber, const wchar_t* filename);
};

typedef struct {
	std::wstring	DisplayName;
	std::wstring	FriendlyName;
} TDshowDevice;
extern std::vector<TDshowDevice> dscap_enum_device();

extern TDshowContext* NewDshowContext(HWND hParent, HWND hOwner, const std::wstring& devname, UINT NotifyMsg);
extern BOOL savebitmap(const BITMAPINFOHEADER* pHeader, const char* data, long nSize, const wchar_t* filename);

#endif
