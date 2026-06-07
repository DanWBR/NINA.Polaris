#include "stdafx.h"
#include "demodshow.h"
#include "SizeDlg.h"

CSizeDlg::CSizeDlg(CWnd* pParent /*=NULL*/)
: CDialog(CSizeDlg::IDD, pParent), m_nSize(100)
{
}

void CSizeDlg::DoDataExchange(CDataExchange* pDX)
{
	CDialog::DoDataExchange(pDX);
	DDX_Text(pDX, IDC_EDIT1, m_nSize);
}

BEGIN_MESSAGE_MAP(CSizeDlg, CDialog)
END_MESSAGE_MAP()

CRoiDlg::CRoiDlg(CWnd* pParent /*=NULL*/)
: CDialog(CRoiDlg::IDD, pParent)
{
}

void CRoiDlg::DoDataExchange(CDataExchange* pDX)
{
	CDialog::DoDataExchange(pDX);
	DDX_Text(pDX, IDC_EDIT1, m_nLeft);
	DDX_Text(pDX, IDC_EDIT2, m_nTop);
	DDX_Text(pDX, IDC_EDIT3, m_nWidth);
	DDX_Text(pDX, IDC_EDIT4, m_nHeight);
}

BEGIN_MESSAGE_MAP(CRoiDlg, CDialog)
END_MESSAGE_MAP()
