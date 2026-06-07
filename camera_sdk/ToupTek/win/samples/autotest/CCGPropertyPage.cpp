#include "stdafx.h"
#include "global.h"
#include "AutoTest.h"
#include "CCGPropertyPage.h"


CCGPropertyPage::CCGPropertyPage()
	: CPropertyPage(IDD_PROPERTY_CG), m_ConversionGain(2)
{
}

BEGIN_MESSAGE_MAP(CCGPropertyPage, CPropertyPage)
	ON_BN_CLICKED(IDC_RADIO_HCG, &CCGPropertyPage::OnBnClickedRadioCG)
	ON_BN_CLICKED(IDC_RADIO_MCG, &CCGPropertyPage::OnBnClickedRadioCG)
	ON_BN_CLICKED(IDC_RADIO_LCG, &CCGPropertyPage::OnBnClickedRadioCG)
END_MESSAGE_MAP()

void CCGPropertyPage::DoDataExchange(CDataExchange* pDX)
{
	CDialog::DoDataExchange(pDX);
	DDX_Radio(pDX, IDC_RADIO_HCG, m_ConversionGain);
}

BOOL CCGPropertyPage::OnInitDialog()
{
	CPropertyPage::OnInitDialog();
	if (g_cur.model->flag & TOUPCAM_FLAG_CG)
	{
		int val = -1;
		Toupcam_get_Option(g_hcam, TOUPCAM_OPTION_CG, &val);
		if (val == 0)
		{
			m_ConversionGain = 2;
			CheckDlgButton(IDC_RADIO_HCG, 0);
			CheckDlgButton(IDC_RADIO_MCG, 0);
			CheckDlgButton(IDC_RADIO_LCG, 1);
		}
		else if (val == 1)
		{
			m_ConversionGain = 0;
			CheckDlgButton(IDC_RADIO_HCG, 1);
			CheckDlgButton(IDC_RADIO_MCG, 0);
			CheckDlgButton(IDC_RADIO_LCG, 0);
		}
		else if (val == 2)
		{
			m_ConversionGain = 1;
			CheckDlgButton(IDC_RADIO_HCG, 0);
			CheckDlgButton(IDC_RADIO_MCG, 1);
			CheckDlgButton(IDC_RADIO_LCG, 0);
		}
		if (g_cur.model->flag & TOUPCAM_FLAG_CGHDR)
			GetDlgItem(IDC_RADIO_MCG)->SetWindowText(L"HDR");
		if (g_cur.model->flag & TOUPCAM_FLAG_CG)
			GetDlgItem(IDC_RADIO_MCG)->ShowWindow(SW_HIDE);
	}
	else
	{
		GetDlgItem(IDC_RADIO_HCG)->EnableWindow(FALSE);
		GetDlgItem(IDC_RADIO_MCG)->EnableWindow(FALSE);
		GetDlgItem(IDC_RADIO_LCG)->EnableWindow(FALSE);
	}
	return 0;
}

void CCGPropertyPage::OnBnClickedRadioCG()
{
	UpdateData(true);
	if (g_hcam)
	{
		if (m_ConversionGain == 0)
			Toupcam_put_Option(g_hcam, TOUPCAM_OPTION_CG, 1);
		else if (m_ConversionGain == 1)
			Toupcam_put_Option(g_hcam, TOUPCAM_OPTION_CG, 2);
		else if (m_ConversionGain == 2)
			Toupcam_put_Option(g_hcam, TOUPCAM_OPTION_CG, 0);
	}
}
