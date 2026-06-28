#include "global.h"

int g_nDpi = 96;

int DpiScale(int x)
{
#if defined(_WIN32)
    return MulDiv(x, g_nDpi, 96);
#else
    return x;
#endif
}

int DpiUnscale(int x)
{
#if defined(_WIN32)
    return MulDiv(x, 96, g_nDpi);
#else
    return x;
#endif
}
