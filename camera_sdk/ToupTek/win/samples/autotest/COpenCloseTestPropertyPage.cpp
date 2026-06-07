#include "stdafx.h"
#include "global.h"
#include "AutoTest.h"
#include "COpenCloseTestPropertyPage.h"
#include "AutoTestDlg.h"

COpenCloseTestPropertyPage::COpenCloseTestPropertyPage()
	: CTestPropertyPage(IDD_PROPERTY_OPEN_CLOSE_TEST)
	, m_initFlag(false), m_interval(2000)
{
}

void COpenCloseTestPropertyPage::UpdateHint()
{
	CString str;
	str.Format(_T("%d/%d"), m_count, m_totalCount);
	SetDlgItemText(IDC_STATIC_OPEN_CLOSE_TEST_HINT, str);
}

BEGIN_MESSAGE_MAP(COpenCloseTestPropertyPage, CPropertyPage)
	ON_EN_CHANGE(IDC_EDIT_OPEN_CLOSE_CNT, &COpenCloseTestPropertyPage::OnEnChangeEditOpenCloseCnt)
	ON_BN_CLICKED(IDC_BUTTON_OPEN_CLOSE_TEST_START, &COpenCloseTestPropertyPage::OnBnClickedButtonOpenCloseTestStart)
	ON_WM_TIMER()
	ON_EN_CHANGE(IDC_EDIT_OPEN_CLOSE_INTERVAL, &COpenCloseTestPropertyPage::OnEnChangeEditOpenCloseInterval)
END_MESSAGE_MAP()

BOOL COpenCloseTestPropertyPage::OnInitDialog()
{
	CPropertyPage::OnInitDialog();
	UpdateHint();
	GetDlgItem(IDC_BUTTON_OPEN_CLOSE_TEST_START)->EnableWindow(m_totalCount > 0 && m_interval >= 100);
	SetDlgItemInt(IDC_EDIT_OPEN_CLOSE_CNT, m_totalCount, FALSE);
	SetDlgItemInt(IDC_EDIT_OPEN_CLOSE_INTERVAL, m_interval, FALSE);
	return TRUE;
}

void COpenCloseTestPropertyPage::OnEnChangeEditOpenCloseCnt()
{
	m_totalCount = GetDlgItemInt(IDC_EDIT_OPEN_CLOSE_CNT);
	UpdateHint();
	GetDlgItem(IDC_BUTTON_OPEN_CLOSE_TEST_START)->EnableWindow(m_totalCount > 0 && m_interval >= 100);
}

void COpenCloseTestPropertyPage::OnEnChangeEditOpenCloseInterval()
{
	m_interval = GetDlgItemInt(IDC_EDIT_OPEN_CLOSE_INTERVAL);
	GetDlgItem(IDC_BUTTON_OPEN_CLOSE_TEST_START)->EnableWindow(m_totalCount > 0 && m_interval >= 100);
}

void COpenCloseTestPropertyPage::OnTimer(UINT_PTR nIDEvent)
{
	KillTimer(1);
	if (m_count >= m_totalCount || g_bBlack)
	{
		Stop();
		if (g_bBlack)
			AfxMessageBox(_T("Image is completely black."), MB_ICONEXCLAMATION | MB_OK);
		else
			AfxMessageBox(_T("Open/close test completed."), MB_ICONINFORMATION | MB_OK);
		return;
	}
	if (m_conModel)
	{
		g_pMainDlg->SendMessage(WM_USER_OPEN_CLOSE);
		if (m_initFlag)
			g_snapCount = ++m_count;
		m_conModel = false;
		UpdateHint();
		SetTimer(1, 1000, nullptr);
	}
	else
	{
		if (!m_initFlag)
		{
			m_initFlag = true;
			m_resNum = Toupcam_get_ResolutionNumber(g_hcam);
		}		
		else
		{
			if (++m_resCount >= m_resNum)
				m_resCount = 0;
			g_pMainDlg->SendMessage(WM_USER_OPEN_CLOSE, m_resCount);	
		}
		SetTimer(1, m_interval, nullptr);
		g_bImageSnap = true;
		m_conModel = true;
	}
}

void COpenCloseTestPropertyPage::Stop()
{
	KillTimer(1);
	m_bStart = g_bTesting = false;
	SetDlgItemText(IDC_BUTTON_OPEN_CLOSE_TEST_START, _T("Start"));
	GetDlgItem(IDC_EDIT_OPEN_CLOSE_CNT)->EnableWindow(TRUE);
	GetDlgItem(IDC_EDIT_OPEN_CLOSE_INTERVAL)->EnableWindow(TRUE);
	m_count = 0;
	UpdateHint();
}

void COpenCloseTestPropertyPage::OnBnClickedButtonOpenCloseTestStart()
{
	if (m_bStart)
		Stop();
	else if (OnStart())
	{
		g_snapDir = GetAppTimeDir(_T("OpenCloseTest"));
		if (!PathIsDirectory((LPCTSTR)g_snapDir))
			SHCreateDirectory(m_hWnd, (LPCTSTR)g_snapDir);
		m_bStart = g_bTesting = true;
		g_bCheckBlack = g_bEnableCheckBlack;
		m_initFlag = g_bBlack = false;
		m_conModel = g_hcam ? false : true;
		m_count = g_snapCount = m_resCount = 0;
		SetDlgItemText(IDC_BUTTON_OPEN_CLOSE_TEST_START, _T("Stop"));
		GetDlgItem(IDC_EDIT_OPEN_CLOSE_CNT)->EnableWindow(FALSE);
		GetDlgItem(IDC_EDIT_OPEN_CLOSE_INTERVAL)->EnableWindow(FALSE);
		SetTimer(1, m_interval, nullptr);
	}
}


