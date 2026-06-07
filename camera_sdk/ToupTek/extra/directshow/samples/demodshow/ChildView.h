#pragma once

class CChildView : public CView
{
public:
	virtual BOOL PreCreateWindow(CREATESTRUCT& cs);
	virtual void OnDraw(CDC* pDC);
	virtual void PostNcDestroy();
protected:
	DECLARE_MESSAGE_MAP()
};

