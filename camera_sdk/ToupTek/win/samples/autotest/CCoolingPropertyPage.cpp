#include "stdafx.h"
#include "global.h"
#include "AutoTest.h"
#include "CCoolingPropertyPage.h"
#include "CCoolingPropertyPage.h"

CCoolingPropertyPage::CCoolingPropertyPage()
	: CPropertyPage(IDD_PROPERTY_COOLING)
{
}

BEGIN_MESSAGE_MAP(CCoolingPropertyPage, CPropertyPage)
END_MESSAGE_MAP()



BOOL CCoolingPropertyPage::OnInitDialog()
{
	CPropertyPage::OnInitDialog();
	if (g_cur.model->flag & TOUPCAM_FLAG_TEC_ONOFF)
	{
		int Target = -1;
		BOOL bEnableTEC = FALSE;
		Toupcam_get_Option(g_hcam, TOUPCAM_OPTION_TEC, &bEnableTEC);
		CheckDlgButton(IDC_CHECK_TEC, bEnableTEC ? 1 : 0);
		GetDlgItem(IDC_BUTTON_APPLY)->EnableWindow(bEnableTEC);
		GetDlgItem(IDC_EDIT_TEC)->EnableWindow(bEnableTEC);
		if (bEnableTEC)
		{
			Toupcam_get_Option(g_hcam, TOUPCAM_OPTION_TECTARGET, &Target);
			CString str;
			str.Format(L"%.1f", Target / 10.0);
			SetDlgItemText(IDC_EDIT_TEC, str);
			GetDlgItem(IDC_BUTTON_APPLY)->EnableWindow(FALSE);
		}	
	}
	else
	{
		GetDlgItem(IDC_CHECK_TEC)->EnableWindow(FALSE);
		GetDlgItem(IDC_BUTTON_APPLY)->EnableWindow(FALSE);
		GetDlgItem(IDC_EDIT_TEC)->EnableWindow(FALSE);
	}
	return 0;
}

void CCoolingPropertyPage::OnBnClickedButtonApply()
{
	if (g_hcam)
	{
		CString strTec;
		GetDlgItemText(IDC_EDIT_TEC, strTec);
		strTec.Trim();
		TCHAR* endptr = nullptr;
		const double d = _tcstod((LPCTSTR)strTec, &endptr);
		if (nullptr == endptr || '\0' == *endptr)
			Toupcam_put_Option(g_hcam, TOUPCAM_OPTION_TECTARGET, (int)(d * 10));
		else
			AfxMessageBox(_T("invalid value entered."), MB_OK | MB_ICONWARNING);
	}
	GetDlgItem(IDC_BUTTON_APPLY)->EnableWindow(FALSE);
}

void CCoolingPropertyPage::OnBnClickedCheckTec()
{
	if (g_hcam)
		Toupcam_put_Option(g_hcam, TOUPCAM_OPTION_TEC, IsDlgButtonChecked(IDC_CHECK_TEC) ? 1 : 0);
	GetDlgItem(IDC_BUTTON_APPLY)->EnableWindow(IsDlgButtonChecked(IDC_CHECK_TEC) ? TRUE : FALSE);
	GetDlgItem(IDC_EDIT_TEC)->EnableWindow(IsDlgButtonChecked(IDC_CHECK_TEC) ? TRUE : FALSE);
}

void CCoolingPropertyPage::OnEnChangeEditTec()
{
	if (!GetDlgItem(IDC_BUTTON_APPLY)->IsWindowEnabled())
		GetDlgItem(IDC_BUTTON_APPLY)->EnableWindow(TRUE);
}
