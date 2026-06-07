#include "stdafx.h"
#include "demod3d11.h"
#include "demod3d11Dlg.h"
#include <InitGuid.h>
#include <wincodec.h>

static BOOL SaveImageByWIC(const wchar_t* strFilename, const void* pData, const BITMAPINFOHEADER* pHeader)
{
	GUID guidContainerFormat;
	if (PathMatchSpec(strFilename, L"*.bmp"))
		guidContainerFormat = GUID_ContainerFormatBmp;
	else if (PathMatchSpec(strFilename, L"*.jpg"))
		guidContainerFormat = GUID_ContainerFormatJpeg;
	else if (PathMatchSpec(strFilename, L"*.png"))
		guidContainerFormat = GUID_ContainerFormatPng;
	else
		return FALSE;

	CComPtr<IWICImagingFactory> spIWICImagingFactory;
	HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, NULL, CLSCTX_INPROC_SERVER, __uuidof(IWICImagingFactory), (LPVOID*)&spIWICImagingFactory);
	if (FAILED(hr))
		return FALSE;

	CComPtr<IWICBitmapEncoder> spIWICBitmapEncoder;
	hr = spIWICImagingFactory->CreateEncoder(guidContainerFormat, NULL, &spIWICBitmapEncoder);
	if (FAILED(hr))
		return FALSE;

	CComPtr<IWICStream> spIWICStream;
	spIWICImagingFactory->CreateStream(&spIWICStream);
	if (FAILED(hr))
		return FALSE;

	hr = spIWICStream->InitializeFromFilename(strFilename, GENERIC_WRITE);
	if (FAILED(hr))
		return FALSE;

	hr = spIWICBitmapEncoder->Initialize(spIWICStream, WICBitmapEncoderNoCache);
	if (FAILED(hr))
		return FALSE;

	CComPtr<IWICBitmapFrameEncode> spIWICBitmapFrameEncode;
	CComPtr<IPropertyBag2> spIPropertyBag2;
	hr = spIWICBitmapEncoder->CreateNewFrame(&spIWICBitmapFrameEncode, &spIPropertyBag2);
	if (FAILED(hr))
		return FALSE;

	if (GUID_ContainerFormatJpeg == guidContainerFormat)
	{
		PROPBAG2 option = { 0 };
		option.pstrName = L"ImageQuality"; /* jpg quality, you can change this setting */
		CComVariant varValue(0.75f);
		spIPropertyBag2->Write(1, &option, &varValue);
	}
	hr = spIWICBitmapFrameEncode->Initialize(spIPropertyBag2);
	if (FAILED(hr))
		return FALSE;

	hr = spIWICBitmapFrameEncode->SetSize(pHeader->biWidth, pHeader->biHeight);
	if (FAILED(hr))
		return FALSE;

	WICPixelFormatGUID formatGUID = GUID_WICPixelFormat24bppBGR;
	hr = spIWICBitmapFrameEncode->SetPixelFormat(&formatGUID);
	if (FAILED(hr))
		return FALSE;

	LONG nWidthBytes = TDIBWIDTHBYTES(pHeader->biWidth * pHeader->biBitCount);
	for (LONG i = 0; i < pHeader->biHeight; ++i)
	{
		hr = spIWICBitmapFrameEncode->WritePixels(1, nWidthBytes, nWidthBytes, ((BYTE*)pData) + nWidthBytes * (pHeader->biHeight - i - 1));
		if (FAILED(hr))
			return FALSE;
	}

	hr = spIWICBitmapFrameEncode->Commit();
	if (FAILED(hr))
		return FALSE;
	hr = spIWICBitmapEncoder->Commit();
	if (FAILED(hr))
		return FALSE;
	
	return TRUE;
}

Cdemod3d11Dlg::Cdemod3d11Dlg(CWnd* pParent /*=NULL*/)
: CDialog(Cdemod3d11Dlg::IDD, pParent), m_hcam(NULL)
{
}

BEGIN_MESSAGE_MAP(Cdemod3d11Dlg, CDialog)
	ON_BN_CLICKED(IDC_BUTTON1, &Cdemod3d11Dlg::OnBnClickedButton1)
	ON_CBN_SELCHANGE(IDC_COMBO1, &Cdemod3d11Dlg::OnCbnSelchangeCombo1)
	ON_MESSAGE(MSG_CAMEVENT, &Cdemod3d11Dlg::OnMsgCamevent)
	ON_WM_DESTROY()
	ON_BN_CLICKED(IDC_BUTTON2, &Cdemod3d11Dlg::OnBnClickedButton2)
	ON_BN_CLICKED(IDC_CHECK1, &Cdemod3d11Dlg::OnBnClickedCheck1)
	ON_BN_CLICKED(IDC_BUTTON3, &Cdemod3d11Dlg::OnBnClickedButton3)
	ON_WM_HSCROLL()
	ON_WM_SIZE()
	ON_WM_GETMINMAXINFO()
	ON_WM_TIMER()
	ON_COMMAND_RANGE(IDM_SNAP_RESOLUTION, IDM_SNAP_RESOLUTION + TOUPCAM_MAX, &Cdemod3d11Dlg::OnSnapResolution)
END_MESSAGE_MAP()

BOOL Cdemod3d11Dlg::OnInitDialog()
{
	CDialog::OnInitDialog();
	GetWindowRect(&m_rectDlg);

	GetDlgItem(IDC_BUTTON2)->EnableWindow(FALSE);
	GetDlgItem(IDC_BUTTON3)->EnableWindow(FALSE);
	GetDlgItem(IDC_CHECK1)->EnableWindow(FALSE);
	GetDlgItem(IDC_SLIDER1)->EnableWindow(FALSE);
	GetDlgItem(IDC_SLIDER2)->EnableWindow(FALSE);
	GetDlgItem(IDC_SLIDER3)->EnableWindow(FALSE);
	GetDlgItem(IDC_COMBO1)->EnableWindow(FALSE);
	SetDlgItemText(IDC_STATIC5, L"");

	Toupcam_GigeEnable(NULL, NULL); /* Enable GigE */
	SetTimer(1, 2000, NULL);
	return TRUE;
}

void Cdemod3d11Dlg::OnBnClickedButton1()
{
	if (m_hcam)
		return;

	m_hcam = Toupcam_Open(NULL);
	if (NULL == m_hcam)
	{
		AfxMessageBox(_T("No Device"));
		return;
	}
	Toupcam_put_RealTime(m_hcam, 1);
	Toupcam_put_Option(m_hcam, TOUPCAM_OPTION_BYTEORDER, 0); //RGB or BGR

	CComboBox* pCombox = (CComboBox*)GetDlgItem(IDC_COMBO1);
	pCombox->ResetContent();
	const int n = (int)Toupcam_get_ResolutionNumber(m_hcam);
	if (n > 0)
	{
		TCHAR txt[128];
		int nWidth, nHeight;
		for (int i = 0; i < n; ++i)
		{
			Toupcam_get_Resolution(m_hcam, i, &nWidth, &nHeight);
			_stprintf(txt, _T("%d * %d"), nWidth, nHeight);
			pCombox->AddString(txt);
		}

		unsigned nCur = 0;
		Toupcam_get_eSize(m_hcam, &nCur);
		pCombox->SetCurSel(nCur);
	}
	
	StartDevice();
}

void Cdemod3d11Dlg::StartDevice()
{
	int nWidth = 0, nHeight = 0;
	HRESULT hr = Toupcam_get_Size(m_hcam, &nWidth, &nHeight);
	if (FAILED(hr))
		return;

	if (IsDlgButtonChecked(IDC_CHECK2))
	{
		Toupcam_put_Option(m_hcam, TOUPCAM_OPTION_BITDEPTH, 1);
		Toupcam_put_Option(m_hcam, TOUPCAM_OPTION_RGB, Toupcam_get_MonoMode(m_hcam) ? 5 : 4);	// RGB64
	}
	else
	{
		Toupcam_put_Option(m_hcam, TOUPCAM_OPTION_BITDEPTH, 0);
		Toupcam_put_Option(m_hcam, TOUPCAM_OPTION_RGB, Toupcam_get_MonoMode(m_hcam) ? 2 : 3);	// RGB32
	}

	m_render = std::make_shared<CD3D11Render>(m_hcam, GetDlgItem(IDC_STATIC4)->GetSafeHwnd(), nWidth, nHeight);
	if (!m_render->Init())
		return;

	Toupcam_StartPullModeWithWndMsg(m_hcam, m_hWnd, MSG_CAMEVENT);

	BOOL bEnableAutoExpo = TRUE;
	Toupcam_get_AutoExpoEnable(m_hcam, &bEnableAutoExpo);
	CheckDlgButton(IDC_CHECK1, bEnableAutoExpo ? 1 : 0);
	GetDlgItem(IDC_SLIDER1)->EnableWindow(!bEnableAutoExpo);
	
	unsigned nMinExpoTime, nMaxExpoTime, nDefExpoTime;
	Toupcam_get_ExpTimeRange(m_hcam, &nMinExpoTime, &nMaxExpoTime, &nDefExpoTime);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER1))->SetRange(nMinExpoTime / 1000, nMaxExpoTime / 1000);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER2))->SetRange(TOUPCAM_TEMP_MIN, TOUPCAM_TEMP_MAX);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER3))->SetRange(TOUPCAM_TINT_MIN, TOUPCAM_TINT_MAX);

	OnEventExpo();
	OnEventTempTint();

	GetDlgItem(IDC_BUTTON2)->EnableWindow(TRUE);
	GetDlgItem(IDC_BUTTON3)->EnableWindow(TRUE);
	GetDlgItem(IDC_CHECK1)->EnableWindow(TRUE);
	GetDlgItem(IDC_SLIDER2)->EnableWindow(TRUE);
	GetDlgItem(IDC_SLIDER3)->EnableWindow(TRUE);
	GetDlgItem(IDC_COMBO1)->EnableWindow(TRUE);
}

void Cdemod3d11Dlg::OnCbnSelchangeCombo1()
{
	if (NULL == m_hcam)
		return;

	const int nSel = ((CComboBox*)GetDlgItem(IDC_COMBO1))->GetCurSel();
	if (nSel < 0)
		return;

	unsigned nResolutionIndex = 0;
	HRESULT hr = Toupcam_get_eSize(m_hcam, &nResolutionIndex);
	if (FAILED(hr))
		return;

	if (nResolutionIndex != nSel)
	{
		hr = Toupcam_Stop(m_hcam);
		if (FAILED(hr))
			return;
		m_render.reset();

		Toupcam_put_eSize(m_hcam, nSel);

		StartDevice();
	}
}

LRESULT Cdemod3d11Dlg::OnMsgCamevent(WPARAM wp, LPARAM /*lp*/)
{
	switch (wp)
	{
	case TOUPCAM_EVENT_ERROR:
	case TOUPCAM_EVENT_NOFRAMETIMEOUT:
	case TOUPCAM_EVENT_NOPACKETTIMEOUT:
		OnEventError();
		break;
	case TOUPCAM_EVENT_DISCONNECTED:
		OnEventDisconnected();
		break;
	case TOUPCAM_EVENT_IMAGE:
		OnEventImage();
		break;
	case TOUPCAM_EVENT_EXPOSURE:
		OnEventExpo();
		break;
	case TOUPCAM_EVENT_TEMPTINT:
		OnEventTempTint();
		break;
	case TOUPCAM_EVENT_STILLIMAGE:
		OnEventStillImage();
		break;
	default:
		break;
	}
	return 0;
}

void Cdemod3d11Dlg::OnEventDisconnected()
{
	if (m_hcam)
	{
		Toupcam_Close(m_hcam);
		m_hcam = NULL;
	}
	AfxMessageBox(_T("Camera disconnect."));
}

void Cdemod3d11Dlg::OnEventError()
{
	if (m_hcam)
	{
		Toupcam_Close(m_hcam);
		m_hcam = NULL;
	}
	AfxMessageBox(_T("Generic error."));
}

void Cdemod3d11Dlg::OnEventExpo()
{
	unsigned nTime = 0;
	Toupcam_get_ExpoTime(m_hcam, &nTime);
	SetDlgItemInt(IDC_STATIC1, nTime / 1000, FALSE);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER1))->SetPos(nTime / 1000);
}

void Cdemod3d11Dlg::OnEventTempTint()
{
	int nTemp = TOUPCAM_TEMP_DEF, nTint = TOUPCAM_TINT_DEF;
	Toupcam_get_TempTint(m_hcam, &nTemp, &nTint);
	SetDlgItemInt(IDC_STATIC2, nTemp, TRUE);
	SetDlgItemInt(IDC_STATIC3, nTint, TRUE);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER2))->SetPos(nTemp);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER3))->SetPos(nTint);
}

void Cdemod3d11Dlg::OnEventImage()
{
	if (m_render)
		m_render->Render();
}

void Cdemod3d11Dlg::OnEventStillImage()
{
	ToupcamFrameInfoV4 info = { 0 };
	HRESULT hr = Toupcam_PullImageV4(m_hcam, NULL, 1, 24, 0, &info);
	if (SUCCEEDED(hr))
	{
		void* pData = malloc(TDIBWIDTHBYTES(info.v3.width * 24) * info.v3.height);
		hr = Toupcam_PullImageV3(m_hcam, pData, 1, 24, 0, NULL);
		if (SUCCEEDED(hr))
		{
			BITMAPINFOHEADER header = { 0 };
			header.biSize = sizeof(header);
			header.biPlanes = 1;
			header.biBitCount = 24;
			header.biWidth = info.v3.width;
			header.biHeight = info.v3.height;
			header.biSizeImage = TDIBWIDTHBYTES(header.biWidth * header.biBitCount) * header.biHeight;
			SaveImageByWIC(L"demod3d11.jpg", pData, &header);
		}
		free(pData);
	}
}

void Cdemod3d11Dlg::OnGetMinMaxInfo(MINMAXINFO* lpMMI)
{
	CDialog::OnGetMinMaxInfo(lpMMI);
	lpMMI->ptMinTrackSize.x = m_rectDlg.Width();
	lpMMI->ptMinTrackSize.y = m_rectDlg.Height();
}

void Cdemod3d11Dlg::OnSize(UINT nType, int cx, int cy)
{
	CDialog::OnSize(nType, cx, cy);
	if ((nType != SIZE_MINIMIZED) && m_render)
		m_render->Resize();
}

void Cdemod3d11Dlg::OnTimer(UINT_PTR nIDEvent)
{
	if ((1 == nIDEvent) && m_hcam && m_render)
	{
		unsigned nFrame = 0, nTime = 0, nTotalFrame = 0, nTotalRender = 0;
		if (SUCCEEDED(Toupcam_get_FrameRate(m_hcam, &nFrame, &nTime, &nTotalFrame)) && nTime)
		{
			double f = nFrame * 1000.0 / nTime;
			if (m_render->GetFrameRate(nFrame, nTime, nTotalRender) && nTime)
			{
				double g = nFrame * 1000.0 / nTime;
				wchar_t str[256];
				swprintf(str, L"%.1f, %u; %.1f, %u", f, nTotalFrame, g, nTotalRender);
				SetDlgItemText(IDC_STATIC5, str);
			}
		}
	}
	CDialog::OnTimer(nIDEvent);
}

void Cdemod3d11Dlg::OnDestroy()
{
	m_render.reset();
	if (m_hcam)
	{
		Toupcam_Close(m_hcam);
		m_hcam = NULL;
	}

	CDialog::OnDestroy();
}

void Cdemod3d11Dlg::OnSnapResolution(UINT nID)
{
	Toupcam_Snap(m_hcam, nID - IDM_SNAP_RESOLUTION);
}

void Cdemod3d11Dlg::OnBnClickedButton2()
{
	const int n = Toupcam_get_StillResolutionNumber(m_hcam);
	if (n <= 0)
		Toupcam_Snap(m_hcam, 0xffffffff);
	else
	{
		CPoint pt;
		GetCursorPos(&pt);
		CMenu menu;
		menu.CreatePopupMenu();
		TCHAR text[64];
		int w, h;
		for (int i = 0; i < n; ++i)
		{
			Toupcam_get_StillResolution(m_hcam, i, &w, &h);
			_stprintf(text, _T("%d * %d"), w, h);
			menu.AppendMenu(MF_STRING, IDM_SNAP_RESOLUTION + i, text);
		}
		menu.TrackPopupMenu(TPM_RIGHTALIGN, pt.x, pt.y, this);
	}
}

void Cdemod3d11Dlg::OnBnClickedCheck1()
{
	if (m_hcam)
		Toupcam_put_AutoExpoEnable(m_hcam, IsDlgButtonChecked(IDC_CHECK1) ? 1 : 0);
	GetDlgItem(IDC_SLIDER1)->EnableWindow(IsDlgButtonChecked(IDC_CHECK1) ? FALSE : TRUE);
}

void Cdemod3d11Dlg::OnBnClickedButton3()
{
	if (m_hcam)
		Toupcam_AwbOnce(m_hcam, NULL, NULL);
}

void Cdemod3d11Dlg::OnHScroll(UINT nSBCode, UINT nPos, CScrollBar* pScrollBar)
{
	if (pScrollBar == GetDlgItem(IDC_SLIDER1))
	{
		const int nTime = ((CSliderCtrl*)GetDlgItem(IDC_SLIDER1))->GetPos();
		SetDlgItemInt(IDC_STATIC1, nTime, TRUE);
		Toupcam_put_ExpoTime(m_hcam, nTime * 1000);
	}
	else
	{
		const int nTemp = ((CSliderCtrl*)GetDlgItem(IDC_SLIDER2))->GetPos();
		const int nTint = ((CSliderCtrl*)GetDlgItem(IDC_SLIDER3))->GetPos();
		SetDlgItemInt(IDC_STATIC2, nTemp, TRUE);
		SetDlgItemInt(IDC_STATIC3, nTint, TRUE);
		Toupcam_put_TempTint(m_hcam, nTemp, nTint);
	}
    
	CDialog::OnHScroll(nSBCode, nPos, pScrollBar);
}