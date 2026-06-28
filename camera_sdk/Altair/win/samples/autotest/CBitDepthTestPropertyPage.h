#pragma once

#include "CTestPropertyPage.h"

class CBitDepthTestPropertyPage : public CTestPropertyPage
{
	int m_formatCount;
	int m_interval;
public:
	CBitDepthTestPropertyPage();

#ifdef AFX_DESIGN_TIME
	enum { IDD = IDD_PROPERTY_BITDEPTH_TEST };
#endif

protected:
	virtual BOOL OnInitDialog();
	afx_msg void OnEnChangeEditBitDepthTestCount();
	afx_msg void OnBnClickedButtonBitDepthTestStart();
	afx_msg void OnTimer(UINT_PTR nIDEvent);
	DECLARE_MESSAGE_MAP()
private:
	void UpdateHint();
	void Stop();
public:
	afx_msg void OnEnChangeEditBitdepthInterval();
};