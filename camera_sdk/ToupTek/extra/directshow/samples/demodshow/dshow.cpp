#include "stdafx.h"
#include "dshow.h"
#include <memory>
#include <uuids.h>

#pragma comment(lib, "quartz.lib")
#pragma comment(lib, "winmm.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "uuid.lib")
#pragma comment(lib, "strmiids.lib")
#pragma comment(lib, "wmvcore.lib")

void FreeMediaType(AM_MEDIA_TYPE& mt)
{
    if (mt.cbFormat) {
        CoTaskMemFree((PVOID)mt.pbFormat);

        // Strictly unnecessary but tidier
        mt.cbFormat = 0;
        mt.pbFormat = NULL;
    }
    if (mt.pUnk) {
        mt.pUnk->Release();
        mt.pUnk = NULL;
    }
}

void DeleteMediaType(AM_MEDIA_TYPE* pmt)
{
    if (pmt == NULL)
        return;

    FreeMediaType(*pmt);
    CoTaskMemFree((PVOID)pmt);
}

BOOL IsEqualObject(IUnknown* pFirst, IUnknown* pSecond)
{
    /*  Different objects can't have the same interface pointer for
        any interface
    */
    if (pFirst == pSecond)
        return TRUE;
    /*  OK - do it the hard way - check if they have the same
        IUnknown pointers - a single object can only have one of these
    */
    LPUNKNOWN pUnknown1;     // Retrieve the IUnknown interface
    LPUNKNOWN pUnknown2;     // Retrieve the other IUnknown interface

    _ASSERTE(pFirst);
    _ASSERTE(pSecond);

    /* See if the IUnknown pointers match */

    HRESULT hr = pFirst->QueryInterface(IID_IUnknown, (void**)&pUnknown1);
    _ASSERTE(SUCCEEDED(hr));
    _ASSERTE(pUnknown1);

    hr = pSecond->QueryInterface(IID_IUnknown, (void**)&pUnknown2);
    _ASSERTE(SUCCEEDED(hr));
    _ASSERTE(pUnknown2);

    /* Release the extra interfaces we hold */

    pUnknown1->Release();
    pUnknown2->Release();
    return (pUnknown1 == pUnknown2);
}

BOOL WINAPI FindConnectedPin(IBaseFilter* pFilter, REFGUID type, IPin** ppPin1, IPin** ppPin2)
{
	IEnumPins* pEnum = NULL;
	IPin *pPin = NULL, *pTmp;
	PIN_DIRECTION ThisPinDir;

	_ASSERTE(pFilter);
	_ASSERTE(ppPin1 && ppPin2);
	if (NULL == pFilter || NULL == ppPin1 || NULL == ppPin2)
		return FALSE;
	*ppPin1 = *ppPin2 = NULL;

	if (FAILED(pFilter->EnumPins(&pEnum)))
		return FALSE;

	while (pEnum->Next(1, &pPin, NULL) == S_OK)
	{
		pPin->QueryDirection(&ThisPinDir);
		if (ThisPinDir == PINDIR_INPUT)
		{
			if (SUCCEEDED(pPin->ConnectedTo(&pTmp)))
			{
				AM_MEDIA_TYPE mtype = { 0 };
				if (SUCCEEDED(pPin->ConnectionMediaType(&mtype)))
				{
					if (::IsEqualGUID(type, mtype.majortype))
					{
						FreeMediaType(mtype);
						*ppPin1 = pTmp;
						*ppPin2 = pPin;
						pEnum->Release();
						return TRUE;
					}
					FreeMediaType(mtype);
				}
				pTmp->Release();
			}
		}
		pPin->Release();
	}

	pEnum->Release();
	return FALSE;
}

BOOL WINAPI GetInputMediaType(IBaseFilter* pFilter, AM_MEDIA_TYPE* pType)
{
	IEnumPins* pEnum = NULL;
	IPin* pPin = NULL;
	PIN_DIRECTION ThisPinDir;

	_ASSERTE(pFilter);
	if (NULL == pFilter)
		return FALSE;

	if (FAILED(pFilter->EnumPins(&pEnum)))
		return FALSE;

	while (pEnum->Next(1, &pPin, NULL) == S_OK)
	{
		pPin->QueryDirection(&ThisPinDir);
		if (ThisPinDir == PINDIR_INPUT)
		{
			if (SUCCEEDED(pPin->ConnectionMediaType(pType)))
			{
				pEnum->Release();
				pPin->Release();
				return TRUE;
			}
		}
		pPin->Release();
	}

	pEnum->Release();
	return FALSE;
}

BOOL WINAPI GetInputpinOutpin(IBaseFilter* pFilter, IPin** ppPin1, IPin** ppPin2)
{
	IEnumPins* pEnum = NULL;
	IPin* pPin = NULL;
	PIN_DIRECTION ThisPinDir;

	_ASSERTE(pFilter);
	_ASSERTE(ppPin1 && ppPin2);
	if (NULL == pFilter || NULL == ppPin1 || NULL == ppPin2)
		return FALSE;
	*ppPin1 = *ppPin2 = NULL;

	if (FAILED(pFilter->EnumPins(&pEnum)))
		return FALSE;

	while (pEnum->Next(1, &pPin, NULL) == S_OK)
	{
		pPin->QueryDirection(&ThisPinDir);
		if (ThisPinDir == PINDIR_INPUT)
		{
			if (*ppPin1 == NULL)
			{
				*ppPin1 = pPin;
				if (*ppPin1 && *ppPin2)
				{
					pEnum->Release();
					return TRUE;
				}
			}
			else
			{
				SAFE_RELEASE(pPin);
			}
		}
		else
		{
			if (*ppPin2 == NULL)
			{
				*ppPin2 = pPin;
				if (*ppPin1 && *ppPin2)
				{
					pEnum->Release();
					return TRUE;
				}
			}
			else
			{
				SAFE_RELEASE(pPin);
			}
		}
	}

	pEnum->Release();
	if (*ppPin1)
	{
		(*ppPin1)->Release();
		*ppPin1 = NULL;
	}
	if (*ppPin2)
	{
		(*ppPin2)->Release();
		*ppPin2 = NULL;
	}

	return FALSE;
}

BOOL WINAPI InsertFilter(IGraphBuilder* pGB, IPin* pPin1, IBaseFilter* pFilter, IPin* pPin2)
{
	_ASSERTE(pGB);
	_ASSERTE(pPin1 && pFilter && pPin2);
	if (NULL == pGB || NULL == pPin1 || NULL == pFilter || NULL == pPin2)
		return FALSE;

	if (FAILED(pGB->AddFilter(pFilter, NULL)))
		return FALSE;

	CComPtr<IPin> pFilterPin1, pFilterPin2;
	if (!GetInputpinOutpin(pFilter, &pFilterPin1, &pFilterPin2))
	{
		pGB->RemoveFilter(pFilter);
		return FALSE;
	}

	if (FAILED(pGB->Disconnect(pPin1)))
	{
		pGB->RemoveFilter(pFilter);
		return FALSE;
	}

	if (FAILED(pGB->Disconnect(pPin2)))
	{
		pGB->RemoveFilter(pFilter);
		pGB->ConnectDirect(pPin1, pPin2, NULL);
		return FALSE;
	}

	if (FAILED(pGB->Connect(pPin1, pFilterPin1)))
	{
		pGB->RemoveFilter(pFilter);
		pGB->ConnectDirect(pPin1, pPin2, NULL);
		return FALSE;
	}

	if (FAILED(pGB->Connect(pFilterPin2, pPin2)))
	{
		pGB->Disconnect(pPin1);
		pGB->Disconnect(pFilterPin2);
		pGB->RemoveFilter(pFilter);
		pGB->ConnectDirect(pPin1, pPin2, NULL);
		return FALSE;
	}

	return TRUE;
}

#ifdef USE_DSHOWOPENCAMERA
typedef unsigned  (__stdcall *PFUN_DSHOWENUMCAMERA)();
typedef const wchar_t* (__stdcall *PFUN_DSHOWGETMODELNAME)(unsigned index);
typedef IUnknown* (__stdcall *PFUN_DSHOWOPENCAMERA)(unsigned index);
PFUN_DSHOWENUMCAMERA g_pDshowEnumCamera = NULL;
PFUN_DSHOWGETMODELNAME g_pDshowGetModelName = NULL;
PFUN_DSHOWOPENCAMERA g_pDshowOpenCamera = NULL;
HMODULE g_hModuleAx = NULL;

static void InitAx(const wchar_t* clsid)
{
    if (g_hModuleAx)
        return;
    
    HKEY hKey = NULL;
    wchar_t regPath[MAX_PATH];
    wsprintfW(regPath, L"CLSID\\%s\\InprocServer32", clsid); /* construct regkey path */
    if (ERROR_SUCCESS == RegOpenKeyExW(HKEY_CLASSES_ROOT, regPath, 0, KEY_READ, &hKey))
    {
        wchar_t axPath[MAX_PATH + 1] = { 0 };
        DWORD cbData = MAX_PATH * sizeof(wchar_t);
        if (ERROR_SUCCESS == RegQueryValueExW(hKey, NULL, NULL, NULL, (PBYTE)axPath, &cbData)) /* query the full path of toupcam.ax */
        {
            g_hModuleAx = LoadLibraryW(axPath);
            if (g_hModuleAx)
            {
                g_pDshowEnumCamera = (PFUN_DSHOWENUMCAMERA)GetProcAddress(g_hModuleAx, "DshowEnumCamera");
                g_pDshowGetModelName = (PFUN_DSHOWGETMODELNAME)GetProcAddress(g_hModuleAx, "DshowGetModelName");
                g_pDshowOpenCamera = (PFUN_DSHOWOPENCAMERA)GetProcAddress(g_hModuleAx, "DshowOpenCamera");
            }
        }
        RegCloseKey(hKey);
    }
}

void InitAxMicro()
{
    /* {EA6387A5-60C7-41D3-B058-8D90580A7BE1} is the clsid of toupcam micro dshow object */
    InitAx(L"{EA6387A5-60C7-41D3-B058-8D90580A7BE1}");
}

void InitAxAstro()
{
    /* {B2190DBF-21E3-4580-98B2-0DB1E557A027} is the clsid of toupcam astro dshow object */
     InitAx(L"{B2190DBF-21E3-4580-98B2-0DB1E557A027}");
}
#endif

TDshowContext::TDshowContext(const std::wstring& devname, HWND hParent, HWND hOwner, IMoniker* pVideoMoniker, UINT NotifyMsg)
: m_devname(devname), m_hParent(hParent), m_hOwner(hOwner), m_pVideoMoniker(pVideoMoniker), m_NotifyMsg(NotifyMsg)
{
	m_pBuilder = NULL;
    m_pVW = NULL;
    m_pME = NULL;
    m_pDlg = NULL;

    m_pAMStreamConfig = NULL;
    m_pRender = NULL;
    m_pSource = NULL;
    m_pFg = NULL;

	m_pSampleGrabber = m_pStillSampleGrabber = NULL;
	m_pStillImageVideoControl = NULL;
	m_pStillImagePin = NULL;
	m_pStillImageAMStreamConfig = NULL;

    m_iVCapDialogPos = m_iVCapCapturePinDialogPos = m_iVStillImagePinDialogPos = -1;
    m_fPreviewGraphBuilt = m_fPreviewing = m_fCaptureGraphBuilt = m_fCapturing = FALSE;
	m_bFullscreen = FALSE;
}

std::vector<TDshowDevice> dscap_enum_device()
{
	std::vector<TDshowDevice> vec;

#ifdef USE_DSHOWOPENCAMERA

	InitToupcamAx();
	if (g_pDshowEnumCamera)
	{
		unsigned n = g_pDshowEnumCamera();
		for (unsigned i = 0; i < n; ++i)
		{
			TCHAR str[256];
			_stprintf(str, _T("Toupcam #%u"), i); /* Toupcam #0, Toupcam #1, etc */

			TDshowDevice dev;
			dev.DisplayName = str;
			dev.FriendlyName = str;
			vec.push_back(dev);
		}
	}

#else

    CComPtr<ICreateDevEnum> spCreateDevEnum;
    HRESULT hr = spCreateDevEnum.CoCreateInstance(CLSID_SystemDeviceEnum, NULL, CLSCTX_INPROC_SERVER);
    if (spCreateDevEnum)
	{
		CComPtr<IEnumMoniker> spEm;
		hr = spCreateDevEnum->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &spEm, 0);
		if (hr == NOERROR)
		{
			spEm->Reset();
    
			ULONG cFetched;
			IMoniker* pM;
			while (hr = spEm->Next(1, &pM, &cFetched), hr == S_OK)
			{
				WCHAR* wszDisplayName = NULL;
				if (SUCCEEDED(pM->GetDisplayName(NULL, NULL, &wszDisplayName)))
				{
					CComPtr<IPropertyBag> spBag;
					hr = pM->BindToStorage(0, 0, IID_IPropertyBag, (void**)&spBag);
					if (spBag)
					{
						CComVariant var;
						var.vt = VT_BSTR;
						hr = spBag->Read(L"FriendlyName", &var, NULL);
						if (hr == NOERROR)
						{
							TDshowDevice dev;
							dev.DisplayName = wszDisplayName;
							dev.FriendlyName = var.bstrVal;
							vec.push_back(dev);
						}
					}
					CoTaskMemFree(wszDisplayName);
				}

				pM->Release();
			}
		}
	}
#endif
	return vec;
}

TDshowContext* NewDshowContext(HWND hParent,  HWND hOwner, const std::wstring& devname, UINT NotifyMsg)
{
#ifdef USE_DSHOWOPENCAMERA

	TDshowContext* dev = new TDshowContext(devname, hParent, hOwner, NULL, NotifyMsg);
	if (NULL == dev)
		return NULL;

	return dev;
#else

	CComPtr<IMoniker> spMoniker;
	CComPtr<IBindCtx> spBC;
	HRESULT hr = CreateBindCtx(0, &spBC);
	if (FAILED(hr) || !spBC)
		return NULL;
	{
		DWORD dwEaten;
		if (FAILED(MkParseDisplayName(spBC, devname.c_str(), &dwEaten, &spMoniker)))
			return NULL;
		if (spMoniker == NULL)
			return NULL;
	}

	TDshowContext* dev = new TDshowContext(devname, hParent, hOwner, spMoniker.Detach(), NotifyMsg);
	if (NULL == dev)
		return NULL;

	return dev;
#endif
}

void TDshowContext::MakeMenuOption(BOOL bAdd, HMENU hMenuSub1, UINT pos, UINT times, UINT cmdID)
{
    HRESULT hr;

    // remove any old choices from the last device
	for (UINT i = 0; i < times; ++i)
		RemoveMenu(hMenuSub1, pos, MF_BYPOSITION);

	if (!bAdd)
		return;

    int zz = 0;
    m_iVCapDialogPos = m_iVCapCapturePinDialogPos = m_iVStillImagePinDialogPos = -1;
    if (m_pSource == NULL)
        return;

    // New WDM devices support new UI and new interfaces.
    // Your app can use some default property
    // pages for UI if you'd like (like we do here) or if you don't like our
    // dialog boxes, feel free to make your own and programmatically set
    // the capture options through interfaces like IAMCrossbar, IAMCameraControl
    // etc.

    // 1. the video capture filter itself
	{
		CComPtr<ISpecifyPropertyPages> pSpec;
		hr = m_pSource->QueryInterface(IID_ISpecifyPropertyPages, (void**)&pSpec);
		if (hr == S_OK)
		{
			CAUUID cauuid = { 0 };
			hr = pSpec->GetPages(&cauuid);
			if (hr == S_OK && cauuid.cElems > 0)
			{
				AppendMenu(hMenuSub1, MF_STRING, cmdID+zz, TEXT("Video Source Properties..."));
				m_iVCapDialogPos = zz++;
				CoTaskMemFree(cauuid.pElems);
			}
		}
    }

    // 2.  The video capture capture pin
	{
		CComPtr<IAMStreamConfig> pSC;
		hr = m_pBuilder->FindInterface(&PIN_CATEGORY_CAPTURE, &MEDIATYPE_Video, m_pSource, IID_IAMStreamConfig, (void**)&pSC);

		if (hr == S_OK)
		{
			CComPtr<ISpecifyPropertyPages> pSpec;
			hr = pSC->QueryInterface(IID_ISpecifyPropertyPages, (void**)&pSpec);
			if (hr == S_OK)
			{
				CAUUID cauuid = { 0 };
				hr = pSpec->GetPages(&cauuid);
				if (hr == S_OK && cauuid.cElems > 0)
				{
					AppendMenu(hMenuSub1, MF_STRING, cmdID+zz, TEXT("Video Stream Format..."));
					m_iVCapCapturePinDialogPos = zz++;
					CoTaskMemFree(cauuid.pElems);
				}
			}
        }
    }

	// 3. Still image pin
	if (m_pStillImagePin)
	{
		CComPtr<ISpecifyPropertyPages> pSpec;
		hr = m_pStillImagePin->QueryInterface(IID_ISpecifyPropertyPages, (void**)&pSpec);
		if (hr == S_OK)
		{
			CAUUID cauuid = { 0 };
			hr = pSpec->GetPages(&cauuid);
            if (hr == S_OK && cauuid.cElems > 0)
            {
                AppendMenu(hMenuSub1, MF_STRING, cmdID+zz, TEXT("Still Image Options..."));
                m_iVStillImagePinDialogPos = zz++;
                CoTaskMemFree(cauuid.pElems);
            }
		}
	}
}

BOOL TDshowContext::StopPreview()
{
    // way ahead of you
    if (!m_fPreviewing)
        return FALSE;

    // stop the graph
    CComPtr<IMediaControl> spMC;
    m_pFg->QueryInterface(IID_IMediaControl, (void**)&spMC);
    if (spMC)
		spMC->Stop();

    m_fPreviewing = FALSE;
    return TRUE;
}

void TDshowContext::NukeDownStream(IBaseFilter* pf)
{
    IPin *pP = 0, *pTo = 0;
    ULONG u;
    IEnumPins* pins = NULL;
    PIN_INFO pininfo;

    if (!pf)
        return;

    HRESULT hr = pf->EnumPins(&pins);
    pins->Reset();

    while (hr == NOERROR)
    {
        hr = pins->Next(1, &pP, &u);
        if (hr == S_OK && pP)
        {
            pP->ConnectedTo(&pTo);
            if (pTo)
            {
                hr = pTo->QueryPinInfo(&pininfo);
                if (hr == NOERROR)
                {
                    if (pininfo.dir == PINDIR_INPUT)
                    {
                        NukeDownStream(pininfo.pFilter);
                        m_pFg->Disconnect(pTo);
                        m_pFg->Disconnect(pP);
                        m_pFg->RemoveFilter(pininfo.pFilter);
                    }
                    pininfo.pFilter->Release();
                }
                pTo->Release();
            }
            pP->Release();
        }
    }

    if (pins)
        pins->Release();
}

void TDshowContext::TearDownGraph()
{
    SAFE_RELEASE(m_pRender);
    SAFE_RELEASE(m_pME);
	SAFE_RELEASE(m_pSampleGrabber);
	SAFE_RELEASE(m_pStillSampleGrabber);

    if (m_pVW)
    {
        // stop drawing in our window, or we may get wierd repaint effects
        m_pVW->put_Owner(NULL);
        m_pVW->put_Visible(OAFALSE);
        m_pVW->Release();
        m_pVW = NULL;
    }

    // destroy the graph downstream of our capture filters
    if (m_pSource)
		NukeDownStream(m_pSource);

    m_fPreviewGraphBuilt = FALSE;
}

void TDshowContext::FreeCapFilter()
{
    SAFE_RELEASE(m_pFg);
	SAFE_RELEASE(m_pBuilder);
    SAFE_RELEASE(m_pSource);
    SAFE_RELEASE(m_pAMStreamConfig);
	SAFE_RELEASE(m_pSampleGrabber);
    SAFE_RELEASE(m_pDlg);

	SAFE_RELEASE(m_pStillImageVideoControl);
	SAFE_RELEASE(m_pStillImagePin);
	SAFE_RELEASE(m_pStillImageAMStreamConfig);
	SAFE_RELEASE(m_pStillSampleGrabber);
}

void TDshowContext::stop()
{
    // Destroy the filter graph and cleanup
    StopPreview();
    TearDownGraph();
    FreeCapFilter();

    _ASSERTE(!m_fPreviewGraphBuilt);
    _ASSERTE(!m_fPreviewing);

    _ASSERTE(NULL == m_pAMStreamConfig);
    _ASSERTE(NULL == m_pRender);
    _ASSERTE(NULL == m_pSource);
    _ASSERTE(NULL == m_pFg);

	_ASSERTE(NULL == m_pStillImageVideoControl);
	_ASSERTE(NULL == m_pStillImagePin);
	_ASSERTE(NULL == m_pStillImageAMStreamConfig);

	_ASSERTE(NULL == m_pSampleGrabber);
	_ASSERTE(NULL == m_pStillSampleGrabber);

	m_iVCapDialogPos = m_iVCapCapturePinDialogPos = m_iVStillImagePinDialogPos = -1;
	
	SAFE_RELEASE(m_pVideoMoniker);

	delete this;
}

BOOL TDshowContext::MakeBuilder()
{
    // we have one already
    if (m_pBuilder)
        return TRUE;

    CoCreateInstance(CLSID_CaptureGraphBuilder2, NULL, CLSCTX_INPROC, IID_ICaptureGraphBuilder2, (void**)&m_pBuilder);
    if (NULL == m_pBuilder)
        return FALSE;
    
    return TRUE;
}

BOOL TDshowContext::MakeGraph()
{
    // we have one already
    if (m_pFg)
        return TRUE;

    HRESULT hr = CoCreateInstance(CLSID_FilterGraph, NULL, CLSCTX_INPROC, IID_IGraphBuilder, (LPVOID*)&m_pFg);
    return (hr == NOERROR) ? TRUE : FALSE;
}

HRESULT TDshowContext::InitCapFilter()
{
    HRESULT hr = S_OK;

    if (!MakeBuilder())
        return E_FAIL;
    m_pSource = NULL;

#ifdef USE_DSHOWOPENCAMERA
	if (g_pDshowOpenCamera)
	{
		unsigned nIndex = 0;
		const wchar_t* p = _tcschr(m_devname.c_str(), '#'); // extract the index from the name, such as 'Toupcam #0', 'Toupcam #1'
		if (p)
			nIndex = _ttoi(p + 1);
		IUnknown* pIUnknown = g_pDshowOpenCamera(nIndex);
		if (pIUnknown)
		{
			pIUnknown->QueryInterface(IID_IBaseFilter, (void**)&m_pSource); // query the IBaseFilter interface
			pIUnknown->Release();
		}
	}

#else
    
	if (m_pVideoMoniker)
        hr = m_pVideoMoniker->BindToObject(0, 0, IID_IBaseFilter, (void**)&m_pSource);
#endif

    if (m_pSource == NULL)
        goto InitCapFiltersFail;

    if (!MakeGraph())
        goto InitCapFiltersFail;

    hr = m_pBuilder->SetFiltergraph(m_pFg);
    if (hr != NOERROR)
        goto InitCapFiltersFail;

    // Add the video capture filter to the graph with its friendly name
    hr = m_pFg->AddFilter(m_pSource, L"Source");
    if (hr != NOERROR)
        goto InitCapFiltersFail;

	_ASSERTE(NULL == m_pAMStreamConfig);
    m_pBuilder->FindInterface(&PIN_CATEGORY_CAPTURE, &MEDIATYPE_Video, m_pSource, IID_IAMStreamConfig, (void**)&m_pAMStreamConfig);

    // we use this interface to bring up the 3 dialogs
    // NOTE:  Only the VfW capture filter supports this.  This app only brings
    // up dialogs for legacy VfW capture drivers, since only those have dialogs
	_ASSERTE(NULL == m_pDlg);
	m_pBuilder->FindInterface(&PIN_CATEGORY_CAPTURE, &MEDIATYPE_Video, m_pSource, IID_IAMVfwCaptureDialogs, (void**)&m_pDlg);

	_ASSERTE(NULL == m_pStillImageVideoControl);
	m_pSource->QueryInterface(IID_IAMVideoControl, (void**)&m_pStillImageVideoControl);

    return S_OK;

InitCapFiltersFail:
    FreeCapFilter();
    return hr;
}

BOOL TDshowContext::RenderStillPin()
{
	CComPtr<IBaseFilter> pNull;
	HRESULT hr = CoCreateInstance(CLSID_NullRenderer, NULL, CLSCTX_INPROC_SERVER, IID_IBaseFilter, (void**)&pNull);
	if (FAILED(hr))
		return FALSE;
	hr = m_pFg->AddFilter(pNull, L"NullRender");
	if (FAILED(hr))
		return FALSE;

	_ASSERTE(NULL == m_pStillSampleGrabber);
	hr = CoCreateInstance(CLSID_SampleGrabber, NULL, CLSCTX_INPROC_SERVER, IID_IBaseFilter, (void**)&m_pStillSampleGrabber);
	if (FAILED(hr))
		return FALSE;
	hr = m_pFg->AddFilter(m_pStillSampleGrabber, L"StillSampleGrabber");
	if (FAILED(hr))
		return FALSE;
	{
		CComPtr<ISampleGrabber> pGrabber;
		m_pStillSampleGrabber->QueryInterface(IID_ISampleGrabber, (void**)&pGrabber);
		if (pGrabber)
		{
			AM_MEDIA_TYPE mt = { 0 };
			mt.majortype = MEDIATYPE_Video;
			mt.subtype = MEDIASUBTYPE_RGB24;
			hr = pGrabber->SetMediaType(&mt);
			if (FAILED(hr))
				return hr;
			pGrabber->SetOneShot(FALSE);
			pGrabber->SetBufferSamples(FALSE);
		}
	}
	hr = m_pBuilder->RenderStream(&PIN_CATEGORY_STILL, &MEDIATYPE_Video, m_pSource, m_pStillSampleGrabber, pNull);
	if (FAILED(hr))
	{
		m_pFg->RemoveFilter(pNull);
		m_pFg->RemoveFilter(m_pStillSampleGrabber);
		return FALSE;
	}

	return TRUE;
}

HRESULT TDshowContext::BuildPreviewGraph()
{
    // we have one already
    if (m_fPreviewGraphBuilt)
        return S_FALSE;

    // No rebuilding while we're running
    if (m_fPreviewing)
        return E_FAIL;

	// We don't have the necessary capture filters
    if (m_pSource == NULL)
        return E_INVALIDARG;

	_ASSERTE(NULL == m_pSampleGrabber);
	HRESULT hr = CoCreateInstance(CLSID_SampleGrabber, NULL, CLSCTX_INPROC_SERVER, IID_IBaseFilter, (void**)&m_pSampleGrabber);
	if (FAILED(hr))
		return hr;
	hr = m_pFg->AddFilter(m_pSampleGrabber, L"SampleGrabber");
	if (FAILED(hr))
		return hr;
	{
		CComPtr<ISampleGrabber> pGrabber;
		m_pSampleGrabber->QueryInterface(IID_ISampleGrabber, (void**)&pGrabber);
		if (pGrabber)
		{
			AM_MEDIA_TYPE mt = { 0 };
			mt.majortype = MEDIATYPE_Video;
			mt.subtype = MEDIASUBTYPE_RGB24;
			hr = pGrabber->SetMediaType(&mt);
			if (FAILED(hr))
				return hr;
			pGrabber->SetOneShot(FALSE);
			pGrabber->SetBufferSamples(TRUE);
		}
	}
	//
    // Render the start pin - even if there is not start pin, the capture
    // graph builder will use a smart tee filter and provide a start.
    //
	hr = m_pBuilder->RenderStream(&PIN_CATEGORY_PREVIEW, &MEDIATYPE_Video, m_pSource, m_pSampleGrabber, NULL);
	if (hr == VFW_S_NOPREVIEWPIN)
    {
		// start was faked up for us using the (only) capture pin
	}
	else if (hr != S_OK)
	{
		m_fPreviewGraphBuilt = FALSE;
		goto fail;
    }
    // Get the start window to be a child of our app's window.

    // This will find the IVideoWindow interface on the renderer. It is
    // important to ask the filtergraph for this interface... do NOT use
    // ICaptureGraphBuilder2::FindInterface, because the filtergraph needs to
    // know we own the window so it can give us display changed messages, etc.
	_ASSERTE(NULL == m_pVW);
    hr = m_pFg->QueryInterface(IID_IVideoWindow, (void**)&m_pVW);
    if (hr != NOERROR)
 		goto fail;
	{
        m_pVW->put_Owner((OAHWND)m_hOwner);    // We own the window now
		m_pVW->put_MessageDrain((OAHWND)m_hOwner);
        m_pVW->put_WindowStyle(WS_CHILD | WS_CLIPSIBLINGS);    // you are now a child
	
		resize_window();
		m_pVW->put_Visible(OATRUE);
    }

    // make sure we process events while we're previewing!
	_ASSERTE(NULL == m_pME);
    hr = m_pFg->QueryInterface(IID_IMediaEventEx, (void**)&m_pME);
    if (hr == NOERROR)
        m_pME->SetNotifyWindow((OAHWND)m_hOwner, m_NotifyMsg, 0);

	if (m_pStillImageVideoControl)
	{
		if (NULL == m_pStillImagePin)
		{
			hr = m_pBuilder->FindPin(m_pSource, PINDIR_OUTPUT, &PIN_CATEGORY_STILL, &MEDIATYPE_Video, FALSE, 0, &m_pStillImagePin);
			if (FAILED(hr))
				SAFE_RELEASE(m_pStillImageVideoControl);
		}

		if (m_pStillImagePin)
		{
			if (NULL == m_pStillImageAMStreamConfig)
				m_pStillImagePin->QueryInterface(IID_IAMStreamConfig, (void**)&m_pStillImageAMStreamConfig);
			if (NULL == m_pStillImageAMStreamConfig)
			{
				SAFE_RELEASE(m_pStillImageVideoControl);
				SAFE_RELEASE(m_pStillImagePin);
			}
			else if (!RenderStillPin())
			{
				SAFE_RELEASE(m_pStillImageVideoControl);
				SAFE_RELEASE(m_pStillImagePin);
				SAFE_RELEASE(m_pStillImageAMStreamConfig);
				SAFE_RELEASE(m_pStillSampleGrabber);
			}
		}
	}

    // All done.
    m_fPreviewGraphBuilt = TRUE;
    return S_OK;

fail:
	TearDownGraph();
	return hr;
}

BOOL TDshowContext::BuildCaptureGraph()
{
    // we have one already
    if (m_fCaptureGraphBuilt)
        return TRUE;

    // No rebuilding while we're running
    if (m_fCapturing || m_fPreviewing)
        return FALSE;

    // We don't have the necessary capture filters
    if (m_pSource == NULL)
        return FALSE;

    // we already have another graph built... tear down the old one
    if (m_fPreviewGraphBuilt)
        TearDownGraph();

    //
    // We need a rendering section that will write the capture file out in AVI
    // file format
    //
	CComPtr<IFileSinkFilter> pSink;
	HRESULT hr = m_pBuilder->SetOutputFileName(&MEDIASUBTYPE_Avi, m_strFullPath.c_str(), &m_pRender, &pSink);
	if ((hr != NOERROR) || (NULL == m_pRender))
        goto SetupCaptureFail;

	//
    // Render the video capture and start pins - even if the capture filter only
    // has a capture pin (and no start pin) this should work... because the
    // capture graph builder will use a smart tee filter to provide both capture
    // and start. We don't have to worry. It will just work.
    //
    hr = m_pBuilder->RenderStream(&PIN_CATEGORY_CAPTURE, &MEDIATYPE_Video, m_pSource, NULL, m_pRender);
    if (hr != NOERROR)
         goto SetupCaptureFail;
	if (m_pRender)
	{
		AM_MEDIA_TYPE amtype = { 0 };
		if (GetInputMediaType(m_pRender, &amtype))
		{
			if (amtype.subtype != MEDIASUBTYPE_MJPG)	// it's MJPG already, nothint to do
			{
				CComPtr<IPin> pVPin1, pVPin2;
				if (FindConnectedPin(m_pRender, MEDIATYPE_Video, &pVPin1, &pVPin2))
				{
					CComPtr<IBaseFilter> spMJPGEnc;
					CoCreateInstance(CLSID_MJPGEnc, NULL, CLSCTX_INPROC, IID_IBaseFilter, (LPVOID*)&spMJPGEnc);
					if (spMJPGEnc)
						InsertFilter(m_pFg, pVPin1, spMJPGEnc, pVPin2);	// insert the MJPG compressor
				}
			}
			FreeMediaType(amtype);
		}
	}

	if (pSink)
	{
		CComPtr<IFileSinkFilter2> pFileSinkFilter2;
		hr = pSink->QueryInterface(IID_IFileSinkFilter2, (void**)&pFileSinkFilter2);
		if (SUCCEEDED(hr))
			pFileSinkFilter2->SetMode(AM_FILE_OVERWRITE);
	}
    hr = m_pBuilder->RenderStream(&PIN_CATEGORY_PREVIEW, &MEDIATYPE_Video, m_pSource, NULL, NULL);
    if (hr == VFW_S_NOPREVIEWPIN)
    {
		// preview was faked up for us using the (only) capture pin
    }
    else if (hr != S_OK)
    {
         goto SetupCaptureFail;
    }

    // This will find the IVideoWindow interface on the renderer.  It is
    // important to ask the filtergraph for this interface... do NOT use
    // ICaptureGraphBuilder2::FindInterface, because the filtergraph needs to
    // know we own the window so it can give us display changed messages, etc.
    {
		_ASSERTE(NULL == m_pVW);
        hr = m_pFg->QueryInterface(IID_IVideoWindow, (void**)&m_pVW);
        if (hr != NOERROR)
			goto SetupCaptureFail;
        else if (hr == NOERROR)
        {
            m_pVW->put_Owner((OAHWND)(m_hOwner));    // We own the window now
			m_pVW->put_MessageDrain((OAHWND)(m_hOwner));
            m_pVW->put_WindowStyle(WS_CHILD | WS_CLIPSIBLINGS);    // you are now a child

			resize_window();

            m_pVW->put_Visible(OATRUE);
        }
    }

    // now ask the filtergraph to tell us when something is completed or aborted
    // (EC_COMPLETE, EC_USERABORT, EC_ERRORABORT).  This is how we will find out
    // if the disk gets full while capturing
	_ASSERTE(NULL == m_pME);
    hr = m_pFg->QueryInterface(IID_IMediaEventEx, (void**)&m_pME);
    if (hr == NOERROR)
        m_pME->SetNotifyWindow((OAHWND)(m_hOwner), m_NotifyMsg, 0);

    // All done.
    m_fCaptureGraphBuilt = TRUE;
    return TRUE;

SetupCaptureFail:
    TearDownGraph();
    return FALSE;
}

BOOL TDshowContext::StartPreview()
{
    // way ahead of you
    if (m_fPreviewing)
        return TRUE;

    if (!m_fPreviewGraphBuilt)
        return FALSE;

    // run the graph
    CComPtr<IMediaControl> spMC;
    HRESULT hr = m_pFg->QueryInterface(IID_IMediaControl, (void**)&spMC);
    if (SUCCEEDED(hr) && spMC)
    {
        hr = spMC->Run();
        if (FAILED(hr))
            spMC->Stop();
    }

    m_fPreviewing = TRUE;
	resize_window();
    return TRUE;
}

BOOL TDshowContext::StartCapture()
{
    // way ahead of you
    if (m_fCapturing)
        return TRUE;

    // or we'll get confused
    if (m_fPreviewing)
        StopPreview();

    // or we'll crash
    if (!m_fCaptureGraphBuilt)
        return FALSE;

    REFERENCE_TIME start = 0x7FFFFFFFFFFFFFFF, stop = 0x7FFFFFFFFFFFFFFF;

    // don't capture quite yet...
    HRESULT hr = m_pBuilder->ControlStream(&PIN_CATEGORY_CAPTURE, NULL, NULL, &start, NULL, 0, 0);

    // Do we have the ability to control capture and preview separately?
    BOOL fHasStreamControl = SUCCEEDED(hr);

    // prepare to run the graph
    CComPtr<IMediaControl> spMC;
    hr = m_pFg->QueryInterface(IID_IMediaControl, (void**)&spMC);
    if (FAILED(hr))
        return FALSE;

    // If we were able to keep capture off, then we can
    // run the graph now for frame accurate start later yet still showing a
    // preview.   Otherwise, we can't run the graph yet without capture
    // starting too, so we'll pause it so the latency between when they
    // press a key and when capture begins is still small (but they won't have
    // a preview while they wait to press a key)

    if (fHasStreamControl)
        hr = spMC->Run();
    else
        hr = spMC->Pause();
    if (FAILED(hr))
    {
        // stop parts that started
        spMC->Stop();
        return FALSE;
    }

    // Start capture NOW!
    if (fHasStreamControl)
    {
        // turn the capture pin on now!
        hr = m_pBuilder->ControlStream(&PIN_CATEGORY_CAPTURE, NULL, NULL, NULL, &stop, 0, 0);
    }
    else
    {
        hr = spMC->Run();
        if (FAILED(hr))
        {
            // stop parts that started
            spMC->Stop();
            return FALSE;
        }
    }

    m_fCapturing = TRUE;
    return TRUE;
}

BOOL TDshowContext::StopCapture()
{
    // way ahead of you
    if (!m_fCapturing)
        return FALSE;

    // stop the graph
    CComPtr<IMediaControl> spMC;
    HRESULT hr = m_pFg->QueryInterface(IID_IMediaControl, (void**)&spMC);
    if (SUCCEEDED(hr) && spMC)
        hr = spMC->Stop();
    if (FAILED(hr))
        return FALSE;
	
	if (m_fPreviewGraphBuilt || m_fCaptureGraphBuilt)
		TearDownGraph();

    m_fCapturing = m_fCaptureGraphBuilt = FALSE;

    return TRUE;
}

HRESULT TDshowContext::start(HMENU hMenuSub1, UINT pos, UINT times, UINT cmdID)
{
	if (m_fPreviewing)
	{
		if (!StopPreview())
			return E_FAIL;
	}
	if (m_fPreviewGraphBuilt)
		TearDownGraph();
	FreeCapFilter();

	HRESULT hr = InitCapFilter();
	if (FAILED(hr))
		return hr;

	hr = BuildPreviewGraph();
	if (FAILED(hr))
		return hr;
	if (!StartPreview())
		return E_FAIL;
	
	MakeMenuOption(TRUE, hMenuSub1, pos, times, cmdID);

	return S_OK;
}

HRESULT TDshowContext::startcapture(const wchar_t* szFullPath)
{
	m_strFullPath = szFullPath;

	if (m_fPreviewing)
	{
		if (!StopPreview())
			return E_FAIL;
	}
	if (m_fPreviewGraphBuilt)
		TearDownGraph();

	if (BuildCaptureGraph())
	{
		if (StartCapture())
			return S_OK;
	}

	HRESULT hr = BuildPreviewGraph();
	if (FAILED(hr))
		return hr;
	
	return StartPreview() ? S_FALSE : E_FAIL;
}

void TDshowContext::stopcapture()
{
	StopCapture();

	if (SUCCEEDED(BuildPreviewGraph()))
		StartPreview();
}

BOOL TDshowContext::video_size(LONG* lWidth, LONG* lHeight)
{
	if (m_pAMStreamConfig)
	{
		AM_MEDIA_TYPE* amt = NULL;
		HRESULT hr = m_pAMStreamConfig->GetFormat(&amt);
		if (FAILED(hr))
			return FALSE;
	
		if (amt->formattype != FORMAT_VideoInfo)
		{
			DeleteMediaType(amt);
			return FALSE;
		}

		if (lWidth)
			*lWidth = HEADER(amt->pbFormat)->biWidth;
		if (lHeight)
			*lHeight = abs(HEADER(amt->pbFormat)->biHeight);
		DeleteMediaType(amt);
		return TRUE;
	}
	return FALSE;
}

void TDshowContext::resize_window()
{
	PostMessage(m_hParent, m_NotifyMsg + 1, 0, 0);
}

BOOL TDshowContext::full_screen()
{
    HRESULT hr = S_OK;
    LONG lMode;

    if (NULL == m_pVW)
        return m_bFullscreen;

    // Read current state
    if (FAILED(m_pVW->get_FullScreenMode(&lMode)))
		return m_bFullscreen;

    if (lMode == OAFALSE)
    {
        // Switch to full-screen mode
        lMode = OATRUE;
        if (FAILED(m_pVW->put_FullScreenMode(lMode)))
			return m_bFullscreen;
        m_bFullscreen = TRUE;
    }
    else
    {
        // Switch back to windowed mode
        lMode = OAFALSE;
        if (FAILED(m_pVW->put_FullScreenMode(lMode)))
			return m_bFullscreen;

        // Reset video window
        m_pVW->SetWindowForeground(-1);

        // Reclaim keyboard focus
        UpdateWindow(m_hParent);
        SetForegroundWindow(m_hParent);
        SetFocus(m_hParent);
        m_bFullscreen = FALSE;
    }
	return m_bFullscreen;
}

void TDshowContext::move_window()
{
	long w = 0, h = 0;
	RECT c;

	if (video_size(&w, &h))
	{
		GetClientRect(m_hOwner, &c);
		if (h < 0)
			h = -h;
		const float w2 = (float)(c.right - c.left);
		const float h2 = (float)(c.bottom - c.top);
		const float f = __min(w2 / w, h2 / h);
		w = (long)(f * w + 0.5f);
		h = (long)(f * h + 0.5f);
		const int left = c.left + ((c.right - c.left) - w) / 2;
		const int top = c.top+((c.bottom - c.top) - h) / 2;
		if (m_pVW)
		{
			m_pVW->SetWindowPosition(left, top, w, h);
			m_pVW->SetWindowForeground(OATRUE);
		}
	}
}

double TDshowContext::get_framerate()
{
	if (m_pBuilder)
	{
		CComPtr<IQualProp> spIQualProp;
		m_pBuilder->FindInterface(&PIN_CATEGORY_CAPTURE, &MEDIATYPE_Video, m_pSource, IID_IQualProp, (void**)&spIQualProp);
		if (spIQualProp)
		{
			int afr = 0;
			if (SUCCEEDED(spIQualProp->get_AvgFrameRate(&afr)))
				return (afr * 0.01);
		}
	}

	return 0.0;
}

void TDshowContext::on_notify()
{
	if (m_pME)
		return;

	LONG evt;
	LONG_PTR l1, l2;
	HRESULT hrAbort = S_OK;
	BOOL bAbort = FALSE;
	while (m_pME->GetEvent(&evt, (LONG_PTR*)&l1, (LONG_PTR*)&l2, 0) == S_OK)
	{
		m_pME->FreeEventParams(evt, l1, l2);
		if (evt == EC_ERRORABORT)
		{
			bAbort = TRUE;
			hrAbort = l1;
			continue;
		}
		else if (evt == EC_VIDEO_SIZE_CHANGED)
			resize_window();
		else if (evt == EC_DEVICE_LOST)
		{
			// Check if we have lost a capture filter being used.
			// lParam2 of EC_DEVICE_LOST event == 1 indicates device added
			//                                 == 0 indicates device removed
			if (l2 == 0)
			{
				IBaseFilter* pf = NULL;
				IUnknown* punk = (IUnknown*)(LONG_PTR)l1;
				if (S_OK == punk->QueryInterface(IID_IBaseFilter, (void**)&pf))
				{
					if (IsEqualObject(m_pSource, pf))
					{
						pf->Release();
						bAbort = FALSE;
						break;
					}
					pf->Release();
				}
			}
		}
	} // end while
	if (bAbort)
		StartPreview();
}

void TDshowContext::on_dialog(int id)
{
	HRESULT hr;
	if (id == m_iVCapDialogPos)
	{
		if (m_pSource)
		{
			FILTER_INFO FilterInfo = { 0 };
            m_pSource->QueryFilterInfo(&FilterInfo);

			CComPtr<ISpecifyPropertyPages> pSpec;
			hr = m_pSource->QueryInterface(IID_ISpecifyPropertyPages, (void**)&pSpec);
			if (hr == S_OK)
			{
				CAUUID cauuid = { 0 };
				hr = pSpec->GetPages(&cauuid);
				pSpec = NULL;
				if (hr == S_OK)
				{
					OleCreatePropertyFrame(m_hParent, 30, 30, FilterInfo.achName, 1, (IUnknown**)&m_pSource, cauuid.cElems, (GUID*)cauuid.pElems, 0, 0, NULL);
					CoTaskMemFree(cauuid.pElems);
				}
			}
			SAFE_RELEASE(FilterInfo.pGraph);
		}
	}
	else if (id == m_iVCapCapturePinDialogPos)
	{
		if (m_pBuilder)
		{
			// You can change this pin's output format in these dialogs.
			// If the capture pin is already connected to somebody who's
			// fussy about the connection type, that may prevent using
			// this dialog(!) because the filter it's connected to might not
			// allow reconnecting to a new format. (EG: you switch from RGB
			// to some compressed type, and need to pull in a decoder)
			// I need to tear down the graph downstream of the
			// capture filter before bringing up these dialogs.
			// In any case, the graph must be STOPPED when calling them.
			StopPreview();  // make sure graph is stopped
		
			// The capture pin that we are trying to set the format on is connected if
			// one of these variable is set to TRUE. The pin should be disconnected for
			// the dialog to work properly.
			if (m_fPreviewGraphBuilt)
				TearDownGraph();    // graph could prevent dialog working
		
			CComPtr<IAMStreamConfig> pSC;
			hr = m_pBuilder->FindInterface(&PIN_CATEGORY_CAPTURE, &MEDIATYPE_Video, m_pSource, IID_IAMStreamConfig, (void**)&pSC);
			if (pSC)
			{
				CComPtr<ISpecifyPropertyPages> pSpec;
				hr = pSC->QueryInterface(IID_ISpecifyPropertyPages, (void**)&pSpec);
				if (hr == S_OK)
				{
					CAUUID cauuid = { 0 };
					hr = pSpec->GetPages(&cauuid);
					if (hr == S_OK)
					{
						OleCreatePropertyFrame(m_hParent, 30, 30, NULL, 1, (IUnknown**)&pSC.p, cauuid.cElems, (GUID*)cauuid.pElems, 0, 0, NULL);
						CoTaskMemFree(cauuid.pElems);
					}
				}
			}

			if (SUCCEEDED(BuildPreviewGraph()))
				StartPreview();
		}
	}
	else if (id == m_iVStillImagePinDialogPos)
	{
		if (m_pBuilder && m_pStillImagePin)
		{
			StopPreview();
			if (m_fPreviewGraphBuilt)
				TearDownGraph();    // graph could prevent dialog working

			CComPtr<IAMStreamConfig> pSC;
			hr = m_pBuilder->FindInterface(&PIN_CATEGORY_STILL, &MEDIATYPE_Video, m_pSource, IID_IAMStreamConfig, (void**)&pSC);
			if (hr == S_OK)
			{
				CComPtr<IPin> pStillImagePin;
				hr = m_pBuilder->FindPin(m_pSource, PINDIR_OUTPUT, &PIN_CATEGORY_STILL, &MEDIATYPE_Video, FALSE, 0, &pStillImagePin);
				if (hr == S_OK)
				{
					CComPtr<ISpecifyPropertyPages> pSpec;
					hr = pStillImagePin->QueryInterface(IID_ISpecifyPropertyPages, (void**)&pSpec);
					if (hr == S_OK)
					{
						CAUUID cauuid = { 0 };
						hr = pSpec->GetPages(&cauuid);
						if (hr == S_OK)
						{
							OleCreatePropertyFrame(m_hParent, 30, 30, NULL, 1, (IUnknown**)&pStillImagePin.p, cauuid.cElems, (GUID*)cauuid.pElems, 0, 0, NULL);
							CoTaskMemFree(cauuid.pElems);
						}
					}
				}
			}

			if (SUCCEEDED(BuildPreviewGraph()))
				StartPreview();
		}
	}
}

BOOL TDshowContext::stillimage_supported()
{
	return m_pStillImageVideoControl ? TRUE : FALSE;
}

BOOL savebitmap(const BITMAPINFOHEADER* pHeader, const char* data, long nSize, const wchar_t* filename)
{
	FILE* fp = _wfopen(filename, L"wb");
	if (fp)
	{
		BITMAPFILEHEADER fheader = { 0 };
		fheader.bfType = 'M' << 8 | 'B';
		fheader.bfSize = sizeof(fheader);
		fheader.bfOffBits = fheader.bfSize + pHeader->biSize;
		fwrite(&fheader, 1, sizeof(fheader), fp);
		fwrite(pHeader, 1, sizeof(BITMAPINFOHEADER), fp);
		fwrite(data, 1, nSize, fp);
		fclose(fp);
		return TRUE;
	}
	return FALSE;
}

BOOL TDshowContext::Snapshot(IBaseFilter* pSampleGrabber, const wchar_t* filename)
{
	if (NULL == pSampleGrabber)
		return FALSE;

	CComPtr<ISampleGrabber> spGrabber;
	pSampleGrabber->QueryInterface(IID_ISampleGrabber, (void**)&spGrabber);
	if (spGrabber == NULL)
		return FALSE;

	if (pSampleGrabber == m_pStillSampleGrabber)
	{
		m_pStillImageVideoControl->SetMode(m_pStillImagePin, VideoControlFlag_Trigger);
		return TRUE;
	}

	AM_MEDIA_TYPE mt = { 0 };
	HRESULT hr = spGrabber->GetConnectedMediaType(&mt);
	if (FAILED(hr))
		return FALSE;

	BOOL bRet = FALSE;
	// Examine the format block.
	if ((mt.formattype == FORMAT_VideoInfo) &&
		(mt.cbFormat >= sizeof(VIDEOINFOHEADER)) &&
		mt.pbFormat)
	{
		VIDEOINFOHEADER* pVih = (VIDEOINFOHEADER*)mt.pbFormat;
		LONG nWidth = pVih->bmiHeader.biWidth;
		LONG nHeight = pVih->bmiHeader.biHeight;
		if (nHeight < 0)
			nHeight = -nHeight;
		if (nWidth && nHeight)
		{
			long nSize = WIDTHBYTES(nWidth * pVih->bmiHeader.biBitCount) * nHeight;
			std::auto_ptr<char> p(new char[nSize]);
			if (p.get())
			{
				if (SUCCEEDED(spGrabber->GetCurrentBuffer(&nSize, (long*)p.get())))
					bRet = savebitmap(&pVih->bmiHeader, p.get(), nSize, filename);
			}
		}
		
		FreeMediaType(mt);
	}
	return bRet;
}

BOOL TDshowContext::preview_snapshot(const wchar_t* filename)
{
	return Snapshot(m_pSampleGrabber, filename);
}

BOOL TDshowContext::stillimage_snapshot(const wchar_t* filename)
{
	return Snapshot(m_pStillSampleGrabber, filename);
}

HRESULT TDshowContext::queryinterface(const IID& riid, void** ppvObj)
{
	if (m_pSource)
		return m_pSource->QueryInterface(riid, ppvObj);
	return E_UNEXPECTED;
}

HRESULT TDshowContext::set_roi(unsigned xOffset, unsigned yOffset, unsigned xWidth, unsigned yHeight)
{
	HRESULT hr = E_NOTIMPL;
	IToupcam* pI = NULL;
	if (m_pSource && SUCCEEDED(m_pSource->QueryInterface(IID_IToupcam, (void**)&pI)) && pI)
	{
		StopPreview();
		if (m_fPreviewGraphBuilt)
			TearDownGraph();
		hr = pI->put_Roi(xOffset, yOffset, xWidth, yHeight);
		pI->Release();

		if (SUCCEEDED(BuildPreviewGraph()))
			StartPreview();
	}
	return hr;
}

HRESULT TDshowContext::get_roi(unsigned* pxOffset, unsigned* pyOffset, unsigned* pxWidth, unsigned* pyHeight)
{
	HRESULT hr = E_NOTIMPL;
	IToupcam* pI = NULL;
	if (m_pSource && SUCCEEDED(m_pSource->QueryInterface(IID_IToupcam, (void**)&pI)) && pI)
	{
		hr = pI->get_Roi(pxOffset, pyOffset, pxWidth, pyHeight);
		pI->Release();
	}
	return hr;
}