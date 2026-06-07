#include <windows.h>
#include <atlbase.h>
#include <atlwin.h>
#include <atlapp.h>
CAppModule _Module;
#include <atlcrack.h>
#include <atltypes.h>
#include <atlstr.h>
#include "toupcam.h"
#include <memory>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <deque>
#include <fcntl.h>
#include <io.h>
#include "resource.h"

#define MSG_EVENT	(WM_APP + 1)

static CString FormatString(const wchar_t* szFormat, ...)
{
	CString str;
	va_list valist;
	va_start(valist, szFormat);
	str.FormatV(szFormat, valist);
	va_end(valist);
	return str;
}

static void savebitmap(long width, long height, const void* data, long nSize, const wchar_t* filename)
{
	int fh = _wopen(filename, _O_BINARY | _O_TRUNC | _O_CREAT | _O_RDWR);
	if (fh >= 0)
	{
		BITMAPINFOHEADER bmpheader = { 0 };
		BITMAPFILEHEADER fheader = { 0 };
		bmpheader.biSize = sizeof(bmpheader);
		bmpheader.biWidth = width;
		bmpheader.biHeight = height;
		bmpheader.biPlanes = 1;
		bmpheader.biBitCount = 24;
		bmpheader.biSizeImage = TDIBWIDTHBYTES(24 * width) * height;
		fheader.bfType = ('M' << 8) | 'B';
		fheader.bfSize = sizeof(fheader);
		fheader.bfOffBits = fheader.bfSize + bmpheader.biSize;
		_write(fh, &fheader, sizeof(fheader));
		_write(fh, &bmpheader, sizeof(BITMAPINFOHEADER));
		_write(fh, data, nSize);
		_close(fh);
	}
}

static void AtlMessageBoxHresult(HWND hWnd, HRESULT hr)
{
	PTCHAR pMsgBuf = NULL;
	FormatMessage(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
		NULL, hr, MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT), (LPTSTR)&pMsgBuf, 0, NULL);
	if (pMsgBuf && pMsgBuf[0])
	{
		AtlMessageBox(hWnd, (LPCTSTR)pMsgBuf);
		LocalFree(pMsgBuf);
	}
	else
	{
		TCHAR str[64] = { 0 };
		_stprintf(str, L"Error, hr = 0x%08x", hr);
		AtlMessageBox(hWnd, str);
	}
}

template<typename T>
static bool GetDlgInt(CWindow* pDlg, UINT nID, T& val, T minval, T maxval)
{
	BOOL bTrans = FALSE;
	val = pDlg->GetDlgItemInt(nID, &bTrans, FALSE);
	if (!bTrans)
	{
		pDlg->GotoDlgCtrl(pDlg->GetDlgItem(nID));
		AtlMessageBox(pDlg->m_hWnd, L"Format error.", (LPCTSTR)nullptr, MB_OK | MB_ICONWARNING);
		return true;
	}
	if ((val < minval) || (val > maxval))
	{
		pDlg->GotoDlgCtrl(pDlg->GetDlgItem(nID));
		AtlMessageBox(pDlg->m_hWnd, (LPCTSTR)FormatString(L"Out of range [%u, %u].", minval, maxval), (LPCTSTR)nullptr, MB_OK | MB_ICONWARNING);
		return true;
	}

	return false; // everything ok
}

class CMainDlg : public CDialogImpl<CMainDlg>, public CMessageFilter
{
	HToupcam m_hcam;
	const ToupcamModelV2* m_model;
	volatile bool m_loop;
	bool m_save;
	void* m_data;
	unsigned m_interval, m_trigger, m_image, m_savenum;
	DWORD m_tick;
	std::unique_ptr<std::thread> m_thread;
	std::deque<void*> m_deque;
	std::mutex m_mtx;
	std::condition_variable m_cv;
public:
	enum { IDD = IDD_MAIN };
	CMainDlg()
	: m_hcam(nullptr), m_model(nullptr), m_loop(false), m_save(true), m_data(nullptr), m_interval(0), m_trigger(0), m_image(0), m_savenum(0), m_tick(0)
	{
	}

	virtual BOOL PreTranslateMessage(MSG* pMsg)
	{
		return ::IsDialogMessage(m_hWnd, pMsg);
	}

	BEGIN_MSG_MAP(CMainDlg)
		MESSAGE_HANDLER(WM_INITDIALOG, OnInitDialog)
		COMMAND_ID_HANDLER(IDCANCEL, OnCancel)
		COMMAND_ID_HANDLER(IDC_BUTTON1, OnButton1)
		COMMAND_ID_HANDLER(IDC_BUTTON2, OnButton2)
		MSG_WM_TIMER(OnTimer)
		MESSAGE_HANDLER(MSG_EVENT, OnMsgEvent)
	END_MSG_MAP()
private:
	LRESULT OnInitDialog(UINT /*uMsg*/, WPARAM /*wParam*/, LPARAM /*lParam*/, BOOL& /*bHandled*/)
	{
		CenterWindow(GetParent());

		GetDlgItem(IDC_BUTTON2).EnableWindow(FALSE);
		SetDlgItemInt(IDC_EDIT1, 1000);
		SetDlgItemInt(IDC_EDIT2, 100);
		SetDlgItemText(IDC_STATIC1, L"0");
		SetDlgItemText(IDC_STATIC2, L"0");
		SetDlgItemText(IDC_STATIC3, L"0");
		CheckDlgButton(IDC_CHECK1, 1);
		return TRUE;
	}

	LRESULT OnCancel(WORD /*wNotifyCode*/, WORD wID, HWND /*hWndCtl*/, BOOL& /*bHandled*/)
	{
		DestroyWindow();
		::PostQuitMessage(0);
		return 0;
	}

	LRESULT OnButton1(WORD /*wNotifyCode*/, WORD wID, HWND /*hWndCtl*/, BOOL& /*bHandled*/)
	{
		if (m_hcam)
		{
			CloseCamera();
			return 0;
		}

		ToupcamDeviceV2 arr[TOUPCAM_MAX];
		const unsigned num = Toupcam_EnumV2(arr);
		if (num <= 0)
			AtlMessageBox(m_hWnd, L"No camera found.", (LPCTSTR)nullptr, MB_OK | MB_ICONWARNING);
		else if (1 == num)
			OpenCamera(arr[0].id, arr[0].model);
		else
		{
			CPoint pt;
			GetCursorPos(&pt);
			CMenu menu;
			menu.CreatePopupMenu();
			for (unsigned i = 0; i < num; ++i)
				menu.AppendMenu(MF_STRING, ID_CAMERA00 + i, arr[i].displayname);
			const int ret = menu.TrackPopupMenu(TPM_RIGHTALIGN | TPM_RETURNCMD, pt.x, pt.y, m_hWnd);
			if (ret >= ID_CAMERA00)
			{
				ATLASSERT(ret - ID_CAMERA00 < num);
				OpenCamera(arr[ret - ID_CAMERA00].id, arr[ret - ID_CAMERA00].model);
			}
		}
		return 0;
	}

	LRESULT OnButton2(WORD /*wNotifyCode*/, WORD wID, HWND /*hWndCtl*/, BOOL& /*bHandled*/)
	{
		if (m_loop)
		{
			m_loop = false;
			KillTimer(1);
			Sleep(m_interval + 50); // wait for the last trigger to complete
			Toupcam_Stop(m_hcam);
			if (m_save)
			{
				m_cv.notify_one();
				if (m_thread)
				{
					m_thread->join();
					m_thread.reset();
				}

				if (!m_deque.empty())
				{
					for (size_t i = 0; i < m_deque.size(); ++i)
						free(m_deque[i]);
					m_deque.clear();
				}
			}
		}
		else
		{
			unsigned expoTime, minTime, maxTime, defTime;
			Toupcam_get_ExpTimeRange(m_hcam, &minTime, &maxTime, &defTime);
			if (GetDlgInt(this, IDC_EDIT1, expoTime, minTime, maxTime))
				return 0;

			if (GetDlgInt(this, IDC_EDIT2, m_interval, 1u, 1000000u))
				return 0;

			Toupcam_put_ExpoTime(m_hcam, expoTime);
			if (FAILED(Toupcam_StartPullModeWithCallback(m_hcam, StaticCameraCallback, this)))
			{
				AtlMessageBox(m_hWnd, L"Failed to start camera.", (LPCTSTR)nullptr, MB_OK | MB_ICONWARNING);
				return 0;
			}

			SetDlgItemText(IDC_STATIC1, L"0");
			SetDlgItemText(IDC_STATIC2, L"0");
			SetDlgItemText(IDC_STATIC3, L"0");
			m_save = IsDlgButtonChecked(IDC_CHECK1) ? true : false;
			m_loop = true;
			m_trigger = m_image = m_savenum = 0;
			m_tick = GetTickCount();
			SetTimer(1, m_interval, nullptr);
			if (m_save)
			{
				m_thread = std::make_unique<std::thread>([this]()
					{
						loopsave();
					});
			}
		}

		GetDlgItem(IDC_EDIT1).EnableWindow(!m_loop);
		GetDlgItem(IDC_EDIT2).EnableWindow(!m_loop);
		GetDlgItem(IDC_CHECK1).EnableWindow(!m_loop);
		SetDlgItemText(IDC_BUTTON2, m_loop ? L"Stop" : L"Start");
		return 0;
	}

	void OnTimer(UINT_PTR nIDEvent)
	{
		if (m_loop && m_hcam)
		{
			SetDlgItemInt(IDC_STATIC1, ++m_trigger);
			Toupcam_Trigger(m_hcam, 1);
			ATLTRACE("%s: %u\n", __func__, GetTickCount());
		}
	}

	LRESULT OnMsgEvent(UINT /*uMsg*/, WPARAM wParam, LPARAM /*lParam*/, BOOL& /*bHandled*/)
	{
		const HRESULT hr = (HRESULT)wParam;
		if (FAILED(hr))
			AtlMessageBoxHresult(m_hWnd, hr);
		else
		{
			wchar_t str[256];
			DWORD tick = GetTickCount();
			if (tick - m_tick > 1000)
				swprintf(str, L"%u; fps = %.1f", m_image, m_image * 1000.0 / (tick - m_tick));
			else
				swprintf(str, L"%u", m_image);
			SetDlgItemText(IDC_STATIC2, str);
			swprintf(str, L"%u", m_savenum);
			SetDlgItemText(IDC_STATIC3, str);
		}
		return 0;
	}
private:
	static void __stdcall StaticCameraCallback(unsigned nEvent, void* ctxEvent)
	{
		CMainDlg* pThis = (CMainDlg*)ctxEvent;
		pThis->CameraCallback(nEvent);
	}

	void CameraCallback(unsigned nEvent)
	{
		if (TOUPCAM_EVENT_IMAGE == nEvent)
		{
			void* pdata = m_data;
			if (m_save)
			{
				pdata = malloc(TDIBWIDTHBYTES(m_model->res[0].width * 24) * m_model->res[0].height);
				if (nullptr == pdata)
				{
					PostMessage(MSG_EVENT, HRESULT_FROM_WIN32(ERROR_OUTOFMEMORY));
					return;
				}
			}

			const HRESULT hr = Toupcam_PullImageV4(m_hcam, pdata, 0, 24, 0, nullptr);
			if (SUCCEEDED(hr))
			{
				++m_image;
				if (m_save)
				{
					{
						std::unique_lock<std::mutex> lock(m_mtx);
						m_deque.push_back(pdata);
					}
					m_cv.notify_one();
				}
			}
			PostMessage(MSG_EVENT, hr);
		}
	}

	void OpenCamera(const wchar_t* camId, const ToupcamModelV2* model)
	{
		m_hcam = Toupcam_Open(camId);
		if (nullptr == m_hcam)
		{
			AtlMessageBox(m_hWnd, L"Open camera failed.", (LPCTSTR)nullptr, MB_OK | MB_ICONWARNING);
			return;
		}

		m_model = model;
		if (0 == (m_model->flag & TOUPCAM_FLAG_TRIGGER_SOFTWARE))
		{
			AtlMessageBox(m_hWnd, L"Soft trigger not supported.", (LPCTSTR)nullptr, MB_OK | MB_ICONWARNING);
			CloseCamera();
			return;
		}
		Toupcam_put_Option(m_hcam, TOUPCAM_OPTION_TRIGGER, 1);
		Toupcam_put_AutoExpoEnable(m_hcam, 0); // always disable auto exposure

		m_data = malloc(TDIBWIDTHBYTES(m_model->res[0].width * 24) * m_model->res[0].height);
		SetDlgItemText(IDC_BUTTON1, L"Close");
		GetDlgItem(IDC_BUTTON2).EnableWindow(TRUE);
	}

	void CloseCamera()
	{
		if (m_hcam)
		{
			Toupcam_Close(m_hcam);
			m_hcam = nullptr;
		}
		if (m_data)
		{
			free(m_data);
			m_data = nullptr;
		}
		GetDlgItem(IDC_BUTTON2).EnableWindow(FALSE);
		SetDlgItemText(IDC_BUTTON1, L"Open");
	}

	void loopsave()
	{
		void* pdata = nullptr;
		wchar_t filename[MAX_PATH];
		while (m_loop)
		{
			{
				std::unique_lock<std::mutex> lock(m_mtx);
				if (m_deque.empty())
				{
					m_cv.wait(lock);
					continue;
				}
				pdata = m_deque.front();
				m_deque.pop_front();
			}
			
			swprintf(filename, L"%08u.bmp", ++m_savenum);
			savebitmap(m_model->res[0].width, m_model->res[0].height, pdata, TDIBWIDTHBYTES(m_model->res[0].width * 24) * m_model->res[0].height, filename);
			free(pdata);
		}
	}
};

static int Run()
{
	CMessageLoop theLoop;
	_Module.AddMessageLoop(&theLoop);
	
	Toupcam_GigeEnable(nullptr, nullptr);
	CMainDlg dlgMain;
	if (!dlgMain.Create(nullptr))
	{
		ATLTRACE(_T("Main dialog creation failed!\n"));
		return 0;
	}
	dlgMain.ShowWindow(SW_SHOW);

	int nRet = theLoop.Run();
	_Module.RemoveMessageLoop();
	return nRet;
}

int WINAPI _tWinMain(HINSTANCE hInstance, HINSTANCE /*hPrevInstance*/, LPTSTR /*pCmdLine*/, int /*nCmdShow*/)
{
	INITCOMMONCONTROLSEX iccx;
	iccx.dwSize = sizeof(iccx);
	iccx.dwICC = ICC_COOL_CLASSES | ICC_BAR_CLASSES;
	InitCommonControlsEx(&iccx);
	OleInitialize(nullptr);
	_Module.Init(nullptr, hInstance);
	int nRet = Run();
	_Module.Term();
	return nRet;
}

#if defined _M_IX86
#pragma comment(linker, "/manifestdependency:\"type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='x86' publicKeyToken='6595b64144ccf1df' language='*'\"")
#elif defined _M_X64
#pragma comment(linker, "/manifestdependency:\"type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='amd64' publicKeyToken='6595b64144ccf1df' language='*'\"")
#endif