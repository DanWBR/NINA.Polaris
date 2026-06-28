#pragma once

#include "CTestPropertyPage.h"

class COpenCloseTestPropertyPage : public CTestPropertyPage
{
	int m_interval;
	bool m_conModel;
	bool m_initFlag;
	int m_resCount;
	int m_resNum;
public:
	COpenCloseTestPropertyPage();

#ifdef AFX_DESIGN_TIME
	enum { IDD = IDD_PROPERTY_OPEN_CLOSE_TEST };
#endif

protected:
	virtual BOOL OnInitDialog();
	afx_msg void OnEnChangeEditOpenCloseCnt();
	afx_msg void OnEnChangeEditOpenCloseInterval();
	afx_msg void OnBnClickedButtonOpenCloseTestStart();
	afx_msg void OnTimer(UINT_PTR nIDEvent);
	DECLARE_MESSAGE_MAP()
private:
	void Stop();
	void UpdateHint();
};