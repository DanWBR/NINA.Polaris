#include "stdafx.h"
#include "imageptrmfc.h"
#include "imageptrmfcDlg.h"

BEGIN_MESSAGE_MAP(CimageptrmfcApp, CWinApp)
END_MESSAGE_MAP()

CimageptrmfcApp::CimageptrmfcApp()
{
}

CimageptrmfcApp theApp;

BOOL CimageptrmfcApp::InitInstance()
{
	INITCOMMONCONTROLSEX InitCtrls;
	InitCtrls.dwSize = sizeof(InitCtrls);
	InitCtrls.dwICC = ICC_WIN95_CLASSES;
	InitCommonControlsEx(&InitCtrls);

	CWinApp::InitInstance();
	AfxOleInit();

	SetRegistryKey(_T("imageptrmfc"));

	CimageptrmfcDlg dlg;
	m_pMainWnd = &dlg;
	dlg.DoModal();

	return FALSE;
}

