#pragma once

class CSizeDlg : public CDialog
{
public:
	CSizeDlg(CWnd* pParent = NULL);

	enum { IDD = IDD_SIZEDLG };

protected:
	virtual void DoDataExchange(CDataExchange* pDX);

	DECLARE_MESSAGE_MAP()
public:
	unsigned m_nSize;
};

class CRoiDlg : public CDialog
{
public:
	CRoiDlg(CWnd* pParent = NULL);

	enum { IDD = IDD_ROIDLG };

protected:
	virtual void DoDataExchange(CDataExchange* pDX);

	DECLARE_MESSAGE_MAP()

public:
	unsigned m_nLeft, m_nTop, m_nWidth, m_nHeight;
};