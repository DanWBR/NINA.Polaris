#include "stdafx.h"
#include "imageptrmfc.h"
#include "imageptrmfcDlg.h"
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

CRectTrackerEx::CRectTrackerEx()
{
}

void CRectTrackerEx::SetRectLimit(CRect rect)
{
	m_rectLimit.left = rect.left + 1;
	m_rectLimit.right = rect.right - 1;
	m_rectLimit.top = rect.top + 1;
	m_rectLimit.bottom = rect.bottom - 1;
}

void CRectTrackerEx::OnChangedRect(const CRect& rectOld)
{
	if (!IsRectEmpty(m_rectLimit))
	{
		if (m_rect.Height() == rectOld.Height() && m_rect.Width() == rectOld.Width())
		{
			if (m_rect.left < m_rectLimit.left)
				m_rect.left = m_rectLimit.left, m_rect.right = m_rect.left + rectOld.Width();
			else if (m_rect.right > m_rectLimit.right)
				m_rect.right = m_rectLimit.right, m_rect.left = m_rect.right - rectOld.Width();
			if (m_rect.top < m_rectLimit.top)
				m_rect.top = m_rectLimit.top, m_rect.bottom = m_rect.top + rectOld.Height();
			else if (m_rect.bottom > m_rectLimit.bottom)
				m_rect.bottom = m_rectLimit.bottom, m_rect.top = m_rect.bottom - rectOld.Height();
		}
		else
		{
			if (m_rect.left < m_rectLimit.left)
				m_rect.left = m_rectLimit.left, m_rect.right = rectOld.right;
			else if (m_rect.right > m_rectLimit.right)
				m_rect.right = m_rectLimit.right, m_rect.left = rectOld.left;
			if (m_rect.top < m_rectLimit.top)
				m_rect.top = m_rectLimit.top, m_rect.bottom = rectOld.bottom;
			else if (m_rect.bottom > m_rectLimit.bottom)
				m_rect.bottom = m_rectLimit.bottom, m_rect.top = rectOld.top;
		}
	}
	CRectTracker::OnChangedRect(m_rect);
}

CimageptrmfcDlg::CimageptrmfcDlg(CWnd* pParent /*=NULL*/)
	: CDialog(CimageptrmfcDlg::IDD, pParent), m_hcam(NULL)
{
	memset(&m_ptr, 0, sizeof(m_ptr));
	memset(&m_header, 0, sizeof(m_header));
	m_header.biSize = sizeof(m_header);
	m_header.biPlanes = 1;
	m_header.biBitCount = 24;
	m_rectTracker = new CRectTrackerEx();
	m_rectTracker->m_nStyle = CRectTracker::resizeInside | CRectTracker::dottedLine;
}

BEGIN_MESSAGE_MAP(CimageptrmfcDlg, CDialog)
	ON_BN_CLICKED(IDC_BUTTON1, &CimageptrmfcDlg::OnBnClickedButton1)
	ON_CBN_SELCHANGE(IDC_COMBO1, &CimageptrmfcDlg::OnCbnSelchangeCombo1)
	ON_MESSAGE(MSG_CAMEVENT, &CimageptrmfcDlg::OnMsgCamevent)
	ON_WM_DESTROY()
	ON_BN_CLICKED(IDC_BUTTON2, &CimageptrmfcDlg::OnBnClickedButton2)
	ON_BN_CLICKED(IDC_CHECK1, &CimageptrmfcDlg::OnBnClickedCheck1)
	ON_BN_CLICKED(IDC_BUTTON3, &CimageptrmfcDlg::OnBnClickedButton3)
	ON_WM_HSCROLL()
	ON_COMMAND_RANGE(IDM_SNAP_RESOLUTION, IDM_SNAP_RESOLUTION + ALTAIRCAM_MAX, &CimageptrmfcDlg::OnSnapResolution)
	ON_WM_LBUTTONDOWN()
	ON_WM_SETCURSOR()
END_MESSAGE_MAP()

BOOL CimageptrmfcDlg::OnInitDialog()
{
	CDialog::OnInitDialog();

	GetDlgItem(IDC_BUTTON2)->EnableWindow(FALSE);
	GetDlgItem(IDC_BUTTON3)->EnableWindow(FALSE);
	GetDlgItem(IDC_CHECK1)->EnableWindow(FALSE);
	GetDlgItem(IDC_SLIDER1)->EnableWindow(FALSE);
	GetDlgItem(IDC_SLIDER2)->EnableWindow(FALSE);
	GetDlgItem(IDC_SLIDER3)->EnableWindow(FALSE);
	GetDlgItem(IDC_COMBO1)->EnableWindow(FALSE);

	Altaircam_GigeEnable(NULL, NULL); /* Enable GigE */
	return TRUE;
}

void CimageptrmfcDlg::OnBnClickedButton1()
{
	if (m_hcam)
		return;

	m_hcam = Altaircam_Open(NULL);
	if (NULL == m_hcam)
	{
		AfxMessageBox(_T("No Device"));
		return;
	}

	CComboBox* pCombox = (CComboBox*)GetDlgItem(IDC_COMBO1);
	pCombox->ResetContent();
	const int n = (int)Altaircam_get_ResolutionNumber(m_hcam);
	if (n > 0)
	{
		TCHAR txt[128];
		int nWidth, nHeight;
		for (int i = 0; i < n; ++i)
		{
			Altaircam_get_Resolution(m_hcam, i, &nWidth, &nHeight);
			_stprintf(txt, _T("%d * %d"), nWidth, nHeight);
			pCombox->AddString(txt);
		}

		unsigned nCur = 0;
		Altaircam_get_eSize(m_hcam, &nCur);
		pCombox->SetCurSel(nCur);
	}
	
	StartDevice();
}

void CimageptrmfcDlg::StartDevice()
{
	int nWidth = 0, nHeight = 0;
	HRESULT hr = Altaircam_get_Size(m_hcam, &nWidth, &nHeight);
	if (FAILED(hr))
		return;

	m_header.biWidth = nWidth;
	m_header.biHeight = nHeight;
	m_header.biSizeImage = TDIBWIDTHBYTES(nWidth * 24) * nHeight;

	Altaircam_StartPullModeWithWndMsg(m_hcam, m_hWnd, MSG_CAMEVENT);

	BOOL bEnableAutoExpo = TRUE;
	Altaircam_get_AutoExpoEnable(m_hcam, &bEnableAutoExpo);
	CheckDlgButton(IDC_CHECK1, bEnableAutoExpo ? 1 : 0);
	GetDlgItem(IDC_SLIDER1)->EnableWindow(!bEnableAutoExpo);
	GetAEAuxRect();
	const CRect rc = GetDrawRect();
	m_rectTracker->SetRectLimit(rc);
	
	unsigned nMinExpoTime, nMaxExpoTime, nDefExpoTime;
	Altaircam_get_ExpTimeRange(m_hcam, &nMinExpoTime, &nMaxExpoTime, &nDefExpoTime);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER1))->SetRange(nMinExpoTime / 1000, nMaxExpoTime / 1000);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER2))->SetRange(ALTAIRCAM_TEMP_MIN, ALTAIRCAM_TEMP_MAX);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER3))->SetRange(ALTAIRCAM_TINT_MIN, ALTAIRCAM_TINT_MAX);

	OnEventExpo();
	OnEventTempTint();

	GetDlgItem(IDC_BUTTON2)->EnableWindow(TRUE);
	GetDlgItem(IDC_BUTTON3)->EnableWindow(TRUE);
	GetDlgItem(IDC_CHECK1)->EnableWindow(TRUE);
	GetDlgItem(IDC_SLIDER2)->EnableWindow(TRUE);
	GetDlgItem(IDC_SLIDER3)->EnableWindow(TRUE);
	GetDlgItem(IDC_COMBO1)->EnableWindow(TRUE);
}

void CimageptrmfcDlg::OnCbnSelchangeCombo1()
{
	if (NULL == m_hcam)
		return;

	const int nSel = ((CComboBox*)GetDlgItem(IDC_COMBO1))->GetCurSel();
	if (nSel < 0)
		return;

	unsigned nResolutionIndex = 0;
	HRESULT hr = Altaircam_get_eSize(m_hcam, &nResolutionIndex);
	if (FAILED(hr))
		return;

	if (nResolutionIndex != nSel)
	{
		hr = Altaircam_Stop(m_hcam);
		if (FAILED(hr))
			return;

		Altaircam_put_eSize(m_hcam, nSel);

		StartDevice();
	}
}

LRESULT CimageptrmfcDlg::OnMsgCamevent(WPARAM wp, LPARAM /*lp*/)
{
	switch (wp)
	{
	case ALTAIRCAM_EVENT_ERROR:
	case ALTAIRCAM_EVENT_NOFRAMETIMEOUT:
	case ALTAIRCAM_EVENT_NOPACKETTIMEOUT:
		OnEventError();
		break;
	case ALTAIRCAM_EVENT_DISCONNECTED:
		OnEventDisconnected();
		break;
	case ALTAIRCAM_EVENT_IMAGE:
		OnEventImage();
		break;
	case ALTAIRCAM_EVENT_EXPOSURE:
		OnEventExpo();
		break;
	case ALTAIRCAM_EVENT_TEMPTINT:
		OnEventTempTint();
		break;
	case ALTAIRCAM_EVENT_STILLIMAGE:
		OnEventStillImage();
		break;
	default:
		break;
	}
	return 0;
}

void CimageptrmfcDlg::OnLButtonDown(UINT nFlags, CPoint point)
{
	m_rectTracker->SetCursor(this, m_rectTracker->HitTest(point));
	if (m_rectTracker->HitTest(point) < 0)
	{
		CRectTracker tempRectTracker;
		tempRectTracker.TrackRubberBand(this, point);
		tempRectTracker.m_rect.NormalizeRect();
		Invalidate();
	}
	else if (m_rectTracker->Track(this, point))
	{
		Invalidate();
		SetAEAuxRect();
	}
	CDialog::OnLButtonDown(nFlags, point);
}

BOOL CimageptrmfcDlg::OnSetCursor(CWnd* pWnd, UINT nHitTest, UINT message)
{
	if ((pWnd == this) && (m_rectTracker->SetCursor(this, nHitTest)))
		return TRUE;
	return CDialog::OnSetCursor(pWnd, nHitTest, message);
}

void CimageptrmfcDlg::OnEventDisconnected()
{
	if (m_hcam)
	{
		Altaircam_Close(m_hcam);
		m_hcam = NULL;
	}
	AfxMessageBox(_T("Camera disconnect."));
}

void CimageptrmfcDlg::OnEventError()
{
	if (m_hcam)
	{
		Altaircam_Close(m_hcam);
		m_hcam = NULL;
	}
	AfxMessageBox(_T("Generic error."));
}

void CimageptrmfcDlg::OnEventExpo()
{
	unsigned nTime = 0;
	Altaircam_get_ExpoTime(m_hcam, &nTime);
	SetDlgItemInt(IDC_STATIC1, nTime / 1000, FALSE);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER1))->SetPos(nTime / 1000);
}

void CimageptrmfcDlg::OnEventTempTint()
{
	int nTemp = ALTAIRCAM_TEMP_DEF, nTint = ALTAIRCAM_TINT_DEF;
	Altaircam_get_TempTint(m_hcam, &nTemp, &nTint);
	SetDlgItemInt(IDC_STATIC2, nTemp, TRUE);
	SetDlgItemInt(IDC_STATIC3, nTint, TRUE);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER2))->SetPos(nTemp);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER3))->SetPos(nTint);
}

CRect CimageptrmfcDlg::GetDrawRect()
{
	CRect rc, rcStartButton;
	GetClientRect(&rc);
	GetDlgItem(IDC_BUTTON1)->GetWindowRect(&rcStartButton);
	ScreenToClient(&rcStartButton);
	rc.left = rcStartButton.right + 4;
	rc.top += 4;
	rc.bottom -= 4;
	rc.right -= 4;
	return rc;
}

void CimageptrmfcDlg::OnEventImage()
{
	AltaircamImagePtr ptr = { 0 };
	HRESULT hr = Altaircam_PullImagePtr(m_hcam, 0, &ptr);
	if (SUCCEEDED(hr))
	{
		if (m_ptr.id)
			Altaircam_PushImagePtr(m_hcam, m_ptr.id);
		m_ptr = ptr;
		const CRect rc = GetDrawRect();
		CClientDC dc(this);
		int m = dc.SetStretchBltMode(COLORONCOLOR);
		StretchDIBits(dc, rc.left, rc.top, rc.Width(), rc.Height(), 0, 0, m_header.biWidth, m_header.biHeight, m_ptr.ptr8, (BITMAPINFO*)&m_header, DIB_RGB_COLORS, SRCCOPY);
		dc.SetStretchBltMode(m);
		if (IsDlgButtonChecked(IDC_CHECK1))
			m_rectTracker->Draw(&dc);
	}
}

void CimageptrmfcDlg::OnEventStillImage()
{
	AltaircamImagePtr ptr = { 0 };
	HRESULT hr = Altaircam_PullImagePtr(m_hcam, 1, &ptr);
	if (SUCCEEDED(hr))
	{
		BITMAPINFOHEADER header = { 0 };
		header.biSize = sizeof(header);
		header.biPlanes = 1;
		header.biBitCount = 24;
		header.biWidth = ptr.ptrInfo->v3.width;
		header.biHeight = ptr.ptrInfo->v3.height;
		header.biSizeImage = TDIBWIDTHBYTES(header.biWidth * header.biBitCount) * header.biHeight;
		SaveImageByWIC(L"imageptrmfc.jpg", ptr.ptr8, &header);
		
		Altaircam_PushImagePtr(m_hcam, ptr.id);
	}
}

void CimageptrmfcDlg::GetAEAuxRect()
{
	const CRect rc = GetDrawRect();
	RECT rect;
	Altaircam_get_AEAuxRect(m_hcam, &rect);
	rect.left = rect.left * rc.Width() / m_header.biWidth + rc.left;
	rect.right = rect.right * rc.Width() / m_header.biWidth + rc.left;
	rect.top = rect.top * rc.Height() / m_header.biHeight + rc.top;
	rect.bottom = rect.bottom * rc.Height() / m_header.biHeight + rc.top;
	m_rectTracker->m_rect.SetRect(CPoint(rect.left, rect.top), CPoint(rect.right, rect.bottom));
}

void CimageptrmfcDlg::SetAEAuxRect()
{
	const CRect rc = GetDrawRect();
	RECT rect;
	rect.left = (m_rectTracker->m_rect.left - rc.left) * m_header.biWidth / rc.Width();
	rect.right = (m_rectTracker->m_rect.right - rc.left) * m_header.biWidth / rc.Width();
	rect.bottom = (m_rectTracker->m_rect.bottom - rc.top) * m_header.biHeight / rc.Height();
	rect.top = (m_rectTracker->m_rect.top - rc.top) * m_header.biHeight / rc.Height();
	Altaircam_put_AEAuxRect(m_hcam, &rect);
}

void CimageptrmfcDlg::OnDestroy()
{
	if (m_hcam)
	{
		Altaircam_Close(m_hcam);
		m_hcam = NULL;
	}
	memset(&m_ptr, 0, sizeof(m_ptr)); /* Frame buffers that have not been returned become invalid immediately after Altaircam_Close */
	
    delete m_rectTracker;

	CDialog::OnDestroy();
}

void CimageptrmfcDlg::OnSnapResolution(UINT nID)
{
	Altaircam_Snap(m_hcam, nID - IDM_SNAP_RESOLUTION);
}

void CimageptrmfcDlg::OnBnClickedButton2()
{
	const int n = Altaircam_get_StillResolutionNumber(m_hcam);
	if (n <= 0)
		Altaircam_Snap(m_hcam, 0xffffffff);
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
			Altaircam_get_StillResolution(m_hcam, i, &w, &h);
			_stprintf(text, _T("%d * %d"), w, h);
			menu.AppendMenu(MF_STRING, IDM_SNAP_RESOLUTION + i, text);
		}
		menu.TrackPopupMenu(TPM_RIGHTALIGN, pt.x, pt.y, this);
	}
}

void CimageptrmfcDlg::OnBnClickedCheck1()
{
	if (m_hcam)
		Altaircam_put_AutoExpoEnable(m_hcam, IsDlgButtonChecked(IDC_CHECK1) ? 1 : 0);
	GetDlgItem(IDC_SLIDER1)->EnableWindow(IsDlgButtonChecked(IDC_CHECK1) ? FALSE : TRUE);
}

void CimageptrmfcDlg::OnBnClickedButton3()
{
	if (m_hcam)
		Altaircam_AwbOnce(m_hcam, NULL, NULL);
}

void CimageptrmfcDlg::OnHScroll(UINT nSBCode, UINT nPos, CScrollBar* pScrollBar)
{
	if (pScrollBar == GetDlgItem(IDC_SLIDER1))
	{
		const int nTime = ((CSliderCtrl*)GetDlgItem(IDC_SLIDER1))->GetPos();
		SetDlgItemInt(IDC_STATIC1, nTime, TRUE);
		Altaircam_put_ExpoTime(m_hcam, nTime * 1000);
	}
	else
	{
		const int nTemp = ((CSliderCtrl*)GetDlgItem(IDC_SLIDER2))->GetPos();
		const int nTint = ((CSliderCtrl*)GetDlgItem(IDC_SLIDER3))->GetPos();
		SetDlgItemInt(IDC_STATIC2, nTemp, TRUE);
		SetDlgItemInt(IDC_STATIC3, nTint, TRUE);
		Altaircam_put_TempTint(m_hcam, nTemp, nTint);
	}
    
	CDialog::OnHScroll(nSBCode, nPos, pScrollBar);
}