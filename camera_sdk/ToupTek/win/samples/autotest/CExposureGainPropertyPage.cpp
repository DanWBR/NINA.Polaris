#include "stdafx.h"
#include "global.h"
#include "AutoTest.h"
#include "CExposureGainPropertyPage.h"

CExposureGainPropertyPage::CExposureGainPropertyPage()
	: CPropertyPage(IDD_PROPERTY_EXPOSURE_GAIN)
{
}

void CExposureGainPropertyPage::OnAutoExposure()
{
	if (GetSafeHwnd())
	{
		if (GetDlgItem(IDC_SLIDER_EXPOSURE))
		{
			unsigned time = 0;
			Toupcam_get_ExpoTime(g_hcam, &time);
			SetExpoTimeValue(time);
		}

		if (GetDlgItem(IDC_SLIDER_GAIN))
		{
			unsigned short gain = 0;
			Toupcam_get_ExpoAGain(g_hcam, &gain);
			SetGainValue(gain);
		}
	}
}

void CExposureGainPropertyPage::UpdateSlidersEnable()
{
	int bAutoExp = 0;
	Toupcam_get_AutoExpoEnable(g_hcam, &bAutoExp);
	GetDlgItem(IDC_SLIDER_TARGET)->EnableWindow(bAutoExp);
	GetDlgItem(IDC_SLIDER_EXPOSURE)->EnableWindow(!bAutoExp);
	GetDlgItem(IDC_SLIDER_GAIN)->EnableWindow(!bAutoExp);
	GetDlgItem(IDC_EDIT_EXPOSURE)->EnableWindow(!bAutoExp);
	GetDlgItem(IDC_EDIT_GAIN)->EnableWindow(!bAutoExp);

	((CSliderCtrl*)GetDlgItem(IDC_SLIDER_TARGET))->SetRange(TOUPCAM_AETARGET_MIN, TOUPCAM_AETARGET_MAX);
	unsigned short target = 0;
	Toupcam_get_AutoExpoTarget(g_hcam, &target);
	SetTargetValue(target);

	unsigned timeMin = 0, timeMax = 0, timeDef = 0, timeVal = 0;
	Toupcam_get_ExpTimeRange(g_hcam, &timeMin, &timeMax, &timeDef);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER_EXPOSURE))->SetRange(timeMin, timeMax);
	Toupcam_get_ExpoTime(g_hcam, &timeVal);
	SetExpoTimeValue(timeVal);

	unsigned short gainMin = 0, gainMax = 0, gainDef = 0, gainVal = 0;
	Toupcam_get_ExpoAGainRange(g_hcam, &gainMin, &gainMax, &gainDef);
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER_GAIN))->SetRange(gainMin, gainMax);
	Toupcam_get_ExpoAGain(g_hcam, &gainVal);
	SetGainValue(gainVal);
}

void CExposureGainPropertyPage::SetTargetValue(int value)
{
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER_TARGET))->SetPos(value);
	SetDlgItemInt(IDC_STATIC_TARGET, value);
}

void CExposureGainPropertyPage::SetExpoTimeValue(unsigned value)
{
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER_EXPOSURE))->SetPos(value);
	CString str;
	str.Format(_T("%d us"), value);
	SetDlgItemText(IDC_STATIC_EXPOSURE, str);
}

void CExposureGainPropertyPage::SetGainValue(int value)
{
	((CSliderCtrl*)GetDlgItem(IDC_SLIDER_GAIN))->SetPos(value);
	SetDlgItemInt(IDC_STATIC_GAIN, value);
}

BEGIN_MESSAGE_MAP(CExposureGainPropertyPage, CPropertyPage)
	ON_BN_CLICKED(IDC_CHECK_AUTO, &CExposureGainPropertyPage::OnBnClickedCheckAuto)
	ON_WM_HSCROLL()
	ON_EN_KILLFOCUS(IDC_EDIT_EXPOSURE, &CExposureGainPropertyPage::OnEnKillfocusEditExpotime)
	ON_EN_KILLFOCUS(IDC_EDIT_GAIN, &CExposureGainPropertyPage::OnEnKillfocusEditExpogain)
END_MESSAGE_MAP()

void CExposureGainPropertyPage::OnBnClickedCheckAuto()
{
	Toupcam_put_AutoExpoEnable(g_hcam, ((CButton*)GetDlgItem(IDC_CHECK_AUTO))->GetCheck() ? 1 : 0);
	UpdateSlidersEnable();
}

BOOL CExposureGainPropertyPage::OnInitDialog()
{
	CPropertyPage::OnInitDialog();

	int bAutoExp = 0;
	Toupcam_get_AutoExpoEnable(g_hcam, &bAutoExp);
	((CButton*)GetDlgItem(IDC_CHECK_AUTO))->SetCheck(bAutoExp);
	UpdateSlidersEnable();

	return TRUE;
}

BOOL CExposureGainPropertyPage::PreTranslateMessage(MSG* pMsg)
{
	if (WM_KEYDOWN == pMsg->message && VK_RETURN == pMsg->wParam)
	{
		if (GetFocus() == GetDlgItem(IDC_EDIT_EXPOSURE))
			GetDlgItem(IDC_STATIC_EXPOSURE)->SetFocus();
		else if (GetFocus() == GetDlgItem(IDC_EDIT_GAIN))
			GetDlgItem(IDC_STATIC_GAIN)->SetFocus();
		return true;
	}
	return CDialog::PreTranslateMessage(pMsg);
}

void CExposureGainPropertyPage::OnHScroll(UINT nSBCode, UINT nPos, CScrollBar* pScrollBar)
{
	if (pScrollBar == GetDlgItem(IDC_SLIDER_TARGET))
	{
		unsigned short curTarget = 0;
		Toupcam_get_AutoExpoTarget(g_hcam, &curTarget);
		unsigned short target = ((CSliderCtrl*)GetDlgItem(IDC_SLIDER_TARGET))->GetPos();
		if (target != curTarget)
		{
			Toupcam_put_AutoExpoTarget(g_hcam, target);
			SetDlgItemInt(IDC_STATIC_TARGET, target);
		}
	}
	else if (pScrollBar == GetDlgItem(IDC_SLIDER_EXPOSURE))
	{
		unsigned curTime = 0;
		Toupcam_get_ExpoTime(g_hcam, &curTime);
		unsigned time = ((CSliderCtrl*)GetDlgItem(IDC_SLIDER_EXPOSURE))->GetPos();
		if (time != curTime)
		{
			Toupcam_put_ExpoTime(g_hcam, time);
			CString str;
			str.Format(_T("%d us"), time);
			SetDlgItemText(IDC_STATIC_EXPOSURE, str);
		}
	}
	else if (pScrollBar == GetDlgItem(IDC_SLIDER_GAIN))
	{
		unsigned short curGain = 0;
		Toupcam_get_ExpoAGain(g_hcam, &curGain);
		unsigned short gain = ((CSliderCtrl*)GetDlgItem(IDC_SLIDER_GAIN))->GetPos();
		if (gain != curGain)
		{
			Toupcam_put_ExpoAGain(g_hcam, gain);
			SetDlgItemInt(IDC_STATIC_GAIN, gain);
		}
	}

	CPropertyPage::OnHScroll(nSBCode, nPos, pScrollBar);
}


void CExposureGainPropertyPage::OnEnKillfocusEditExpotime()
{
	CString strTemp;
	GetDlgItemText(IDC_EDIT_EXPOSURE, strTemp);
	if (!strTemp.IsEmpty())
	{
		strTemp.Trim();
		TCHAR* endptr = nullptr;
		double value = _tcstod((LPCTSTR)strTemp, &endptr);
		if (nullptr == endptr || '\0' == *endptr)
		{
			unsigned nMinExpoTime, nMaxExpoTime, nDefExpoTime;
			Toupcam_get_ExpTimeRange(g_hcam, &nMinExpoTime, &nMaxExpoTime, &nDefExpoTime);
			if (value < nMinExpoTime)
				value = nMinExpoTime;
			else if (value > nMaxExpoTime)
				value = nMaxExpoTime;
			Toupcam_put_ExpoTime(g_hcam, value);
			SetDlgItemText(IDC_EDIT_EXPOSURE, _T(""));
			UpdateData(false);
		}
		else
			AfxMessageBox(_T("The exposure time is invalid"), MB_OK | MB_ICONINFORMATION);
	}
}

void CExposureGainPropertyPage::OnEnKillfocusEditExpogain()
{
	CString strTemp;
	GetDlgItemText(IDC_EDIT_GAIN, strTemp);
	if (!strTemp.IsEmpty())
	{
		unsigned short nMinExpoAGain, nMaxExpoAGain, nDefExpoAGain;
		Toupcam_get_ExpoAGainRange(g_hcam, &nMinExpoAGain, &nMaxExpoAGain, &nDefExpoAGain);
		int value = GetDlgItemInt(IDC_EDIT_GAIN);
		if (value < nMinExpoAGain)
			value = nMinExpoAGain;
		else if (value > nMaxExpoAGain)
			value = nMaxExpoAGain;
		Toupcam_put_ExpoAGain(g_hcam, value);
		SetDlgItemText(IDC_EDIT_GAIN, _T(""));
	}
}
