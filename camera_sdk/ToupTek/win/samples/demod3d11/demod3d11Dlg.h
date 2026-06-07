#pragma once

#include <memory>
#include "d3d11render.h"

class Cdemod3d11Dlg : public CDialog
{
	HToupcam m_hcam;
	std::shared_ptr<CD3D11Render> m_render;
	CRect m_rectDlg;
public:
	Cdemod3d11Dlg(CWnd* pParent = NULL);

	enum { IDD = IDD_DEMOD3D11 };

protected:
	virtual BOOL OnInitDialog();
	DECLARE_MESSAGE_MAP()
public:
	afx_msg void OnBnClickedButton1();
	afx_msg void OnBnClickedButton2();
	afx_msg void OnBnClickedButton3();
	afx_msg void OnCbnSelchangeCombo1();
	afx_msg void OnSnapResolution(UINT nID);
	afx_msg void OnBnClickedCheck1();
	afx_msg void OnHScroll(UINT nSBCode, UINT nPos, CScrollBar* pScrollBar);
	afx_msg void OnDestroy();
	afx_msg void OnSize(UINT nType, int cx, int cy);
	afx_msg void OnGetMinMaxInfo(MINMAXINFO* lpMMI);
	afx_msg void OnTimer(UINT_PTR nIDEvent);
	afx_msg LRESULT OnMsgCamevent(WPARAM wp, LPARAM lp);
private:
	void StartDevice();
	void OnEventError();
	void OnEventDisconnected();
	void OnEventImage();
	void OnEventExpo();
	void OnEventTempTint();
	void OnEventStillImage();
};
