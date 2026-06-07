#include "stdafx.h"
#include "d3d11render.h"

static unsigned get_precise_tick()
{
	return (unsigned)(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}

CD3D11Render::CD3D11Render(HToupcam hcam, HWND hwndTarget, int imageWidth, int imageHeight)
: m_hcam(hcam), m_bMono(Toupcam_get_MonoMode(m_hcam) == S_OK), m_hwnd(hwndTarget), m_imageWidth(imageWidth), m_imageHeight(imageHeight)
, m_windowWidth(INT_MIN), m_windowHeight(INT_MIN), m_loop(true), m_evt(nullptr), m_resize(0)
, m_totalFrame(0), m_nFrame(0), m_nTick(get_precise_tick())
{
	Toupcam_get_RawFormat(m_hcam, NULL, &m_bitdepth);
}

CD3D11Render::~CD3D11Render()
{
	if (m_thrd)
	{
		m_loop = false;
		SetEvent(m_evt);
		m_thrd->join();
	}
	if (m_evt)
		CloseHandle(m_evt);
}

bool CD3D11Render::GetFrameRate(unsigned& nFrame, unsigned& nTime, unsigned& nTotal)
{
	unsigned tick = get_precise_tick();
	unsigned diff = tick - m_nTick;
	if (diff > 500)
	{
		nTime = diff;
		nFrame = m_nFrame;
		nTotal = m_totalFrame;
		m_nTick = tick;
		m_nFrame = 0;
		return true;
	}
	return false;
}

bool CD3D11Render::Init()
{
	UINT creationFlags = 0;
#ifdef _DEBUG
	creationFlags |= D3D11_CREATE_DEVICE_DEBUG;
#endif
	DXGI_SWAP_CHAIN_DESC scd = {};
	scd.BufferCount = BUFFERCOUNT;
	scd.BufferDesc.Format = (m_bitdepth > 8) ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
	scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
	scd.OutputWindow = m_hwnd;
	scd.SampleDesc.Count = 1;
	scd.Windowed = TRUE;
	scd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
	D3D_FEATURE_LEVEL featureLevel;
	HRESULT hr = D3D11CreateDeviceAndSwapChain(NULL, D3D_DRIVER_TYPE_HARDWARE, NULL, creationFlags, NULL, 0, D3D11_SDK_VERSION, &scd, &m_swapChain, &m_device, &featureLevel, &m_context);
	if (FAILED(hr))
		return false;

	{
		CComQIPtr<IDXGIDevice1> spIDXGIDevice1(m_device);
		if (spIDXGIDevice1)
			spIDXGIDevice1->SetMaximumFrameLatency(1);
	}

	D3D11_TEXTURE2D_DESC texDesc = {};
	texDesc.Width = m_imageWidth;
	texDesc.Height = m_imageHeight;
	texDesc.MipLevels = texDesc.ArraySize = texDesc.SampleDesc.Count = 1;
	if (m_bMono)
		texDesc.Format = (m_bitdepth > 8) ? DXGI_FORMAT_R16_UNORM : DXGI_FORMAT_R8_UNORM;
	else
		texDesc.Format = (m_bitdepth > 8) ? DXGI_FORMAT_R16G16B16A16_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
	texDesc.Usage = D3D11_USAGE_DYNAMIC;
	texDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
	texDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
	hr = m_device->CreateTexture2D(&texDesc, NULL, &m_textureImage);
	if (FAILED(hr))
		return false;

	D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
	srvDesc.Format = texDesc.Format;
	srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
	srvDesc.Texture2D.MipLevels = 1;
	hr = m_device->CreateShaderResourceView(m_textureImage, &srvDesc, &m_srv);
	if (FAILED(hr))
		return false;

	hr = CreateShaders();
	if (FAILED(hr))
		return false;
	hr = CreateSampler();
	if (FAILED(hr))
		return false;

	RECT rc;
	GetClientRect(m_hwnd, &rc);
	Resize(rc.right - rc.left, rc.bottom - rc.top);

	m_context->PSSetSamplers(0, 1, &m_sampler.p);
	m_context->IASetInputLayout(m_inputLayout);

	m_context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);

	UINT stride = sizeof(float) * 6, offset = 0;
	m_context->IASetVertexBuffers(0, 1, &m_vertexBuffer.p, &stride, &offset);

	m_context->VSSetShader(m_vs, NULL, 0);
	m_context->PSSetShader(m_ps, NULL, 0);

	m_evt = CreateEvent(NULL, FALSE, FALSE, NULL);
	m_thrd = std::make_shared<std::thread>([this]()
		{
			Loop();
		});
	return true;
}

HRESULT CD3D11Render::CreateShaders()
{
	const char* vsCode = R"(
			struct VS_IN {
				float4 pos : POSITION;
				float2 uv  : TEXCOORD;
			};
			struct VS_OUT {
				float4 pos : SV_POSITION;
				float2 uv  : TEXCOORD;
			};
			VS_OUT VS(VS_IN input) {
				VS_OUT output;
				output.pos = input.pos;
				output.uv = input.uv;
				return output;
			})";
	CComPtr<ID3DBlob> vsBlob;
	HRESULT hr = D3DCompile(vsCode, strlen(vsCode), NULL, NULL, NULL, "VS", "vs_5_0", 0, 0, &vsBlob, NULL);
	if (FAILED(hr))
		return hr;
	hr = m_device->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), NULL, &m_vs);
	if (FAILED(hr))
		return hr;

	const char* psCode = R"(
			Texture2D tex : register(t0);
			SamplerState sam : register(s0);
			struct PS_IN {
				float4 pos : SV_POSITION;
				float2 uv  : TEXCOORD;
			};
			float4 PS(PS_IN input) : SV_Target {
			#if MONO == 1
				float gray = tex.Sample(sam, input.uv).r;
				float4 color = float4(gray, gray, gray, 1.0);
			#else
				float4 color = tex.Sample(sam, input.uv);
			#endif
				color *= SCALE;
				return color;
			})";
	char scale[32];
	if ((m_bitdepth <= 8) || (m_bitdepth >= 16))
		strcpy(scale, "1.0");
	else
	{
		const double denom = double((1 << m_bitdepth) - 1);
		sprintf(scale, "%.8f", 65535.0 / denom);
	}
	D3D_SHADER_MACRO macro[] = {
		{ "MONO", m_bMono ? "1" : "0" },
		{ "SCALE", scale },
		{ nullptr, nullptr }
	};
	CComPtr<ID3DBlob> psBlob;
	hr = D3DCompile(psCode, strlen(psCode), NULL, macro, NULL, "PS", "ps_5_0", 0, 0, &psBlob, NULL);
	if (FAILED(hr))
		return hr;
	hr = m_device->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), NULL, &m_ps);
	if (FAILED(hr))
		return hr;

	D3D11_INPUT_ELEMENT_DESC layout[] = {
		{ "POSITION", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 0,  D3D11_INPUT_PER_VERTEX_DATA, 0 },
		{ "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT,       0, 16, D3D11_INPUT_PER_VERTEX_DATA, 0 }
	};
	hr = m_device->CreateInputLayout(layout, 2, vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), &m_inputLayout);
	if (FAILED(hr))
		return hr;

	const struct Vertex {
		float x, y, z, w;
		float u, v;
	} vertices[] = {
		{ -1.0f,  1.0f, 0.0f, 1.0f, 0.0f, 0.0f },
		{  1.0f,  1.0f, 0.0f, 1.0f, 1.0f, 0.0f },
		{ -1.0f, -1.0f, 0.0f, 1.0f, 0.0f, 1.0f },
		{  1.0f, -1.0f, 0.0f, 1.0f, 1.0f, 1.0f }
	};
	D3D11_BUFFER_DESC bd = {};
	bd.Usage = D3D11_USAGE_DEFAULT;
	bd.ByteWidth = sizeof(vertices);
	bd.BindFlags = D3D11_BIND_VERTEX_BUFFER;
	D3D11_SUBRESOURCE_DATA initData = { vertices };
	return m_device->CreateBuffer(&bd, &initData, &m_vertexBuffer);
}

HRESULT CD3D11Render::CreateSampler()
{
	D3D11_SAMPLER_DESC sampDesc = {};
	sampDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
	sampDesc.AddressU = sampDesc.AddressV = sampDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
	return m_device->CreateSamplerState(&sampDesc, &m_sampler);
}

void CD3D11Render::Resize()
{
	RECT rc;
	GetClientRect(m_hwnd, &rc);
	if ((m_windowWidth != rc.right - rc.left) || (m_windowHeight != rc.bottom - rc.top))
	{
		int cx = rc.right - rc.left, cy = rc.bottom - rc.top;
		TOUPCAM_LOG_VERBOSE("%s: %d %d", __func__, cx, cy);
		m_resize.store(0x80000000 | (cx << 16) | cy);
		SetEvent(m_evt);
	}
}

HRESULT CD3D11Render::Resize(int windowWidth, int windowHeight)
{
	TOUPCAM_LOG_VERBOSE("%s: %d %d", __func__, windowWidth, windowHeight);
	m_windowWidth = windowWidth;
	m_windowHeight = windowHeight;
	m_context->OMSetRenderTargets(0, NULL, NULL);

	DXGI_SWAP_CHAIN_DESC scd = {};
	HRESULT hr = m_swapChain->GetDesc(&scd);
	if (FAILED(hr))
		return hr;
	hr = m_swapChain->ResizeBuffers(scd.BufferCount, m_windowWidth, m_windowHeight, scd.BufferDesc.Format, scd.Flags);
	if (FAILED(hr))
		return hr;

	const float scale = __min(m_windowWidth / (float)m_imageWidth, m_windowHeight / (float)m_imageHeight);
	const int vpW = static_cast<int>(m_imageWidth * scale);
	const int vpH = static_cast<int>(m_imageHeight * scale);
	const int vpX = (m_windowWidth - vpW) / 2;
	const int vpY = (m_windowHeight - vpH) / 2;
	const D3D11_VIEWPORT vp = {
		(float)vpX, (float)vpY,
		(float)vpW, (float)vpH,
		0.0f, 1.0f
	};
	m_context->RSSetViewports(1, &vp);
	return S_OK;
}

bool CD3D11Render::SetupRtv()
{
	CComPtr<ID3D11Texture2D> backBuffer;
	if (SUCCEEDED(m_swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&backBuffer)))
	{
		CComPtr<ID3D11RenderTargetView> rtv;
		if (SUCCEEDED(m_device->CreateRenderTargetView(backBuffer, nullptr, &rtv)))
		{
			m_context->OMSetRenderTargets(1, &rtv.p, nullptr);
			const float bgColor[4] = { 0.0f, 0.0f, 0.0f, 1.0f };
			m_context->ClearRenderTargetView(rtv, bgColor);
			return true;
		}
	}

	return false;
}

void CD3D11Render::Render()
{
	TOUPCAM_LOG_VERBOSE("%s", __func__);
	SetEvent(m_evt);
}

void CD3D11Render::Loop()
{
	while (m_loop)
	{
		{
			unsigned val = m_resize.load();
			if (val & 0x80000000)
			{
				m_resize.store(0);
				Resize((val >> 16) & 0x7fff, val & 0x7fff);
				continue;
			}
		}

		unsigned tick;
		D3D11_MAPPED_SUBRESOURCE mapped = {};
		HRESULT hr = m_context->Map(m_textureImage, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
		if (SUCCEEDED(hr))
		{
			hr = Toupcam_PullImageWithRowPitch(m_hcam, mapped.pData, m_bMono ? ((m_bitdepth > 8) ? 16 : 8) : ((m_bitdepth > 8) ? 64 : 32), mapped.RowPitch, NULL, NULL);
			if (SUCCEEDED(hr))
				tick = get_precise_tick();
			m_context->Unmap(m_textureImage, 0);
		}
		if (FAILED(hr))
		{
			WaitForSingleObject(m_evt, INFINITE);
			continue;
		}

		if (SetupRtv())
		{
			m_context->PSSetShaderResources(0, 1, &m_srv.p);
			m_context->Draw(4, 0);
			hr = m_swapChain->Present(1, 0);
			if (SUCCEEDED(hr))
			{
				++m_totalFrame;
				++m_nFrame;
			}
			TOUPCAM_LOG_VERBOSE("%s: Present, 0x%08x, tick = %u", __func__, hr, get_precise_tick() - tick);
		}
	}
}
