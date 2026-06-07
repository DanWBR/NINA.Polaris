#pragma once
#include "ChildView.h"
#include "dshow.h"

#define MSG_DSNOTIFY	(WM_APP+1)
#define MSG_DSSIZE		(WM_APP+2)

class CMainFrame : public CFrameWnd
{
	std::wstring	m_curDshowDevice;
	TDshowContext*	m_pDshowContext;
public:
	CMainFrame();
protected:
	DECLARE_DYNAMIC(CMainFrame)
public:
	virtual BOOL PreCreateWindow(CREATESTRUCT& cs);
	virtual BOOL OnCmdMsg(UINT nID, int nCode, void* pExtra, AFX_CMDHANDLERINFO* pHandlerInfo);
protected:
	CChildView	m_wndView;
	CStatusBar	m_statusBar;
protected:
	afx_msg int OnCreate(LPCREATESTRUCT lpCreateStruct);
	afx_msg void OnSetFocus(CWnd *pOldWnd);
	afx_msg void OnUpdateCamera(CCmdUI* pCmdUI);
	afx_msg void OnCamera(UINT nID);
	afx_msg void OnSetup(UINT nID);
	afx_msg void OnClose();
	afx_msg void OnSize(UINT nType, int cx, int cy);
	afx_msg LRESULT OnMsgDsnotify(WPARAM wp, LPARAM lp);
	afx_msg LRESULT OnMsgDssize(WPARAM wp, LPARAM lp);
	afx_msg void OnExampleSize();
	afx_msg void OnExampleRoi();
	afx_msg void OnExampleFliphorizontal();
	afx_msg void OnExampleFlipvertical();
	afx_msg void OnPreviewSnapshot();
	afx_msg void OnStillimageSnapshot();
	afx_msg void OnCapture();
	afx_msg void OnStopCapture();
	afx_msg void OnSn();
	afx_msg void OnUpdateExampleSize(CCmdUI* pCmdUI);
	afx_msg void OnUpdateExampleRoi(CCmdUI* pCmdUI);
	afx_msg void OnUpdateExampleFliphorizontal(CCmdUI* pCmdUI);
	afx_msg void OnUpdateExampleFlipvertical(CCmdUI* pCmdUI);
	afx_msg void OnUpdatePreviewSnapshot(CCmdUI* pCmdUI);
	afx_msg void OnUpdateStillimageSnapshot(CCmdUI* pCmdUI);
	afx_msg void OnUpdateCapture(CCmdUI* pCmdUI);
	afx_msg void OnUpdateStopCapture(CCmdUI* pCmdUI);
	afx_msg void OnTimer(UINT_PTR nIDEvent);
	DECLARE_MESSAGE_MAP()
private:
	void MenuCamera(CMenu* pMenu);
};
