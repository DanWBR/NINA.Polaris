#pragma once

class CCoolingPropertyPage : public CPropertyPage
{
public:
	CCoolingPropertyPage();

#ifdef AFX_DESIGN_TIME
	enum { IDD = IDD_PROPERTY_COOLING };
#endif

protected:
	virtual BOOL OnInitDialog();
	DECLARE_MESSAGE_MAP()

public:
	afx_msg void OnBnClickedButtonApply();
	afx_msg void OnBnClickedCheckTec();
	afx_msg void OnEnChangeEditTec();
};
