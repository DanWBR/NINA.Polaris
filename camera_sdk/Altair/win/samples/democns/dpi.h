#ifndef __dpi__
#define __dpi__

class CDPI
{
    bool _fInitialized;
    int _dpiX;
    int _dpiY;
public:
    CDPI() noexcept : _fInitialized(false), _dpiX(96), _dpiY(96) {}
    
    // Get screen DPI.
    int GetDPIX() noexcept { _Init(); return _dpiX; }
    int GetDPIY() noexcept { _Init(); return _dpiY; }

    // Convert between raw pixels and relative pixels.
    int ScaleX(int x) noexcept { _Init(); return MulDiv(x, _dpiX, 96); }
    int ScaleY(int y) noexcept { _Init(); return MulDiv(y, _dpiY, 96); }
    int UnscaleX(int x) noexcept { _Init(); return MulDiv(x, 96, _dpiX); }
    int UnscaleY(int y) noexcept { _Init(); return MulDiv(y, 96, _dpiY); }

    // Determine the screen dimensions in relative pixels.
    int ScaledScreenWidth() noexcept { return _ScaledSystemMetricX(SM_CXSCREEN); }
    int ScaledScreenHeight() noexcept { return _ScaledSystemMetricY(SM_CYSCREEN); }

    // Scale rectangle from raw pixels to relative pixels.
    void ScaleRect(RECT* pRect) noexcept
    {
        pRect->left = ScaleX(pRect->left);
        pRect->right = ScaleX(pRect->right);
        pRect->top = ScaleY(pRect->top);
        pRect->bottom = ScaleY(pRect->bottom);
    }
    // Determine if screen resolution meets minimum requirements in relative
    // pixels.
    bool IsResolutionAtLeast(int cxMin, int cyMin) noexcept
    {
        return (ScaledScreenWidth() >= cxMin) && (ScaledScreenHeight() >= cyMin);
    }

    // Convert a point size (1/72 of an inch) to raw pixels.
    int PointsToPixels(int pt) noexcept { _Init(); return MulDiv(pt, _dpiY, 72); }
	int PixelsToPoints(int px) noexcept { _Init(); return MulDiv(px, 72, _dpiY); }

    // Invalidate any cached metrics.
    void Invalidate() noexcept { _fInitialized = false; }
private:
    void _Init() noexcept
    {
        if (!_fInitialized)
        {
            HDC hdc = GetDC(nullptr);
            if (hdc)
            {
                _dpiX = GetDeviceCaps(hdc, LOGPIXELSX);
                _dpiY = GetDeviceCaps(hdc, LOGPIXELSY);
                ReleaseDC(nullptr, hdc);
            }
            _fInitialized = true;
        }
    }

    int _ScaledSystemMetricX(int nIndex) noexcept
    {
        _Init();
        return MulDiv(GetSystemMetrics(nIndex), 96, _dpiX);
    }

    int _ScaledSystemMetricY(int nIndex) noexcept
    {
        _Init();
        return MulDiv(GetSystemMetrics(nIndex), 96, _dpiY);
    }
};

extern CDPI g_dpi;

#endif