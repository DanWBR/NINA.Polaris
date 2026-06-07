#ifndef __D3D11Render_h__
#define __D3D11Render_h__

#include <atlbase.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>
#include <thread>
#include <atomic>
#include "toupcam.h"

class CD3D11Render
{
	enum { BUFFERCOUNT = 2 };
public:
	CD3D11Render(HToupcam hcam, HWND hwndTarget, int imageWidth, int imageHeight);
	~CD3D11Render();
	bool Init();
	void Render();
	void Resize();
	bool GetFrameRate(unsigned& nFrame, unsigned& nTime, unsigned& nTotal);

private:
	const HToupcam m_hcam;
	const bool m_bMono;
	const HWND m_hwnd;
	const int m_imageWidth, m_imageHeight;
	unsigned m_bitdepth;
	int m_windowWidth, m_windowHeight;
	unsigned m_totalFrame, m_nFrame, m_nTick;
	std::atomic<unsigned> m_resize;
	volatile bool m_loop;
	HANDLE m_evt;
	std::shared_ptr<std::thread> m_thrd;

	CComPtr<ID3D11Device> m_device;
	CComPtr<ID3D11DeviceContext> m_context;
	CComPtr<IDXGISwapChain> m_swapChain;
	CComPtr<ID3D11Texture2D> m_textureImage;
	CComPtr<ID3D11ShaderResourceView> m_srv;
	CComPtr<ID3D11SamplerState> m_sampler;
	CComPtr<ID3D11VertexShader> m_vs;
	CComPtr<ID3D11PixelShader> m_ps;
	CComPtr<ID3D11InputLayout> m_inputLayout;
	CComPtr<ID3D11Buffer> m_vertexBuffer;

	HRESULT CreateShaders();
	HRESULT CreateSampler();
	HRESULT Resize(int windowWidth, int windowHeight);
	bool SetupRtv();
	void Loop();
};

#endif