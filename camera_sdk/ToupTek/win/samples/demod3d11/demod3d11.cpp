#include "stdafx.h"
#include "demod3d11.h"
#include "demod3d11Dlg.h"

Cdemod3d11App theApp;

BOOL Cdemod3d11App::InitInstance()
{
	INITCOMMONCONTROLSEX InitCtrls;
	InitCtrls.dwSize = sizeof(InitCtrls);
	InitCtrls.dwICC = ICC_WIN95_CLASSES;
	InitCommonControlsEx(&InitCtrls);

	CWinApp::InitInstance();
	AfxOleInit();

	SetRegistryKey(_T("demod3d11"));

	Cdemod3d11Dlg dlg;
	m_pMainWnd = &dlg;
	dlg.DoModal();

	return FALSE;
}

