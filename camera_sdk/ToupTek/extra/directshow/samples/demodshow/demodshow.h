#pragma once

#ifndef __AFXWIN_H__
	#error "include 'stdafx.h' before including this file for PCH"
#endif

#include "resource.h"       // main symbols

class CdemodshowApp : public CWinApp
{
public:
	virtual BOOL InitInstance();
public:
	afx_msg void OnAppAbout();
	DECLARE_MESSAGE_MAP()
};

extern CdemodshowApp theApp;
