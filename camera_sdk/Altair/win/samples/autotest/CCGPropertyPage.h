#pragma once

class CCGPropertyPage : public CPropertyPage
{
	int	m_ConversionGain;
public:
	CCGPropertyPage();

#ifdef AFX_DESIGN_TIME
	enum { IDD = IDD_PROPERTY_CG };
#endif

protected:
	virtual BOOL OnInitDialog();
	virtual void DoDataExchange(CDataExchange* pDX);
	afx_msg void OnBnClickedRadioCG();
	DECLARE_MESSAGE_MAP()

};
