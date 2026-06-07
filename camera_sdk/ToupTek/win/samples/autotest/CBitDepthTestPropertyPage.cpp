#include "stdafx.h"
#include "global.h"
#include "AutoTest.h"
#include "CBitDepthTestPropertyPage.h"

CBitDepthTestPropertyPage::CBitDepthTestPropertyPage()
	: CTestPropertyPage(IDD_PROPERTY_BITDEPTH_TEST)
	, m_formatCount(0)
{
}

void CBitDepthTestPropertyPage::UpdateHint()
{
	CString str;
	str.Format(_T("%d/%d"), m_count, m_totalCount);
	SetDlgItemText(IDC_STATIC_BITDEPTH_TEST_HINT, str);
}

BEGIN_MESSAGE_MAP(CBitDepthTestPropertyPage, CPropertyPage)
	ON_EN_CHANGE(IDC_EDIT_BITDEPTH_TEST_CNT, &CBitDepthTestPropertyPage::OnEnChangeEditBitDepthTestCount)
	ON_BN_CLICKED(IDC_BUTTON_BITDEPTH_TEST_START, &CBitDepthTestPropertyPage::OnBnClickedButtonBitDepthTestStart)
	ON_WM_TIMER()
END_MESSAGE_MAP()

BOOL CBitDepthTestPropertyPage::OnInitDialog()
{
	CPropertyPage::OnInitDialog();

	UpdateHint();
	GetDlgItem(IDC_BUTTON_BITDEPTH_TEST_START)->EnableWindow(FALSE);
	return TRUE;
}

void CBitDepthTestPropertyPage::OnEnChangeEditBitDepthTestCount()
{
	m_totalCount = GetDlgItemInt(IDC_EDIT_BITDEPTH_TEST_CNT);
	UpdateHint();
	GetDlgItem(IDC_BUTTON_BITDEPTH_TEST_START)->EnableWindow(m_totalCount > 0);
}

void CBitDepthTestPropertyPage::OnTimer(UINT_PTR nIDEvent)
{
	if (m_count >= m_totalCount || g_bBlack)
	{
		Stop();
		if (g_bBlack)
			AfxMessageBox(_T("Image is completely black."), MB_ICONEXCLAMATION | MB_OK);
		else
			AfxMessageBox(_T("Bitdepth test completed."), MB_ICONINFORMATION | MB_OK);
		return;
	}

	g_snapCount = m_count;
	int pixelNum = -1, iFormat = -1;
	Toupcam_get_PixelFormatSupport(g_hcam, -1, &pixelNum);
	Toupcam_get_PixelFormatSupport(g_hcam, m_formatCount, &iFormat);
	Toupcam_put_Option(g_hcam, TOUPCAM_OPTION_PIXEL_FORMAT, iFormat);
	g_bBitdepthTest = true;
	if (++m_formatCount >= pixelNum)
	{
		++m_count;
		UpdateHint();
		m_formatCount = 0;
	}
}

void CBitDepthTestPropertyPage::Stop()
{
	m_bStart = g_bTesting = false;
	KillTimer(1);
	SetDlgItemText(IDC_BUTTON_BITDEPTH_TEST_START, _T("Start"));
	GetDlgItem(IDC_EDIT_BITDEPTH_TEST_CNT)->EnableWindow(TRUE);
}

void CBitDepthTestPropertyPage::OnBnClickedButtonBitDepthTestStart()
{
	if (m_bStart)
		Stop();
	else if (OnStart())
	{
		g_snapDir = GetAppTimeDir(_T("BitDepthTest"));
		if (!PathIsDirectory((LPCTSTR)g_snapDir))
			SHCreateDirectory(m_hWnd, (LPCTSTR)g_snapDir);

		m_bStart = g_bTesting = true;
		g_bCheckBlack = g_bEnableCheckBlack;
		g_bBlack = false;
		m_count = 0;
		SetDlgItemText(IDC_BUTTON_BITDEPTH_TEST_START, _T("Stop"));
		GetDlgItem(IDC_EDIT_BITDEPTH_TEST_CNT)->EnableWindow(FALSE);
		SetTimer(1, 1000, nullptr);
	}
}