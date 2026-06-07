#pragma once

#ifndef __AFXWIN_H__
	#error "include 'stdafx.h' before including this file for PCH"
#endif

#include "resource.h"		// main symbols

class Cdemod3d11App : public CWinApp
{
public:
	virtual BOOL InitInstance();
};

extern Cdemod3d11App theApp;