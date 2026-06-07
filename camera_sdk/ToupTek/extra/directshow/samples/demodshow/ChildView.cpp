#include "stdafx.h"
#include "demodshow.h"
#include "ChildView.h"

BEGIN_MESSAGE_MAP(CChildView, CView)
END_MESSAGE_MAP()

BOOL CChildView::PreCreateWindow(CREATESTRUCT& cs)
{
	if (!CView::PreCreateWindow(cs))
		return FALSE;

	cs.dwExStyle |= WS_EX_CLIENTEDGE;
	cs.style &= ~WS_BORDER;
	cs.lpszClass = AfxRegisterWndClass(CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS, ::LoadCursor(NULL, IDC_ARROW), reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1), NULL);

	return TRUE;
}

void CChildView::PostNcDestroy()
{
	/* to avoid CView::PostNcDestroy, which delete this */
}

void CChildView::OnDraw(CDC* pDC)
{
	/* nothing to do */
}