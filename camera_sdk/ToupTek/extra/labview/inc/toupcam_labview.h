#ifndef __toupcam_labview_h__
#define __toupcam_labview_h__

#include "extcode.h"

#ifdef TOUPCAM_LABVIEW_EXPORTS
#define TOUPCAM_LABVIEW_API(x) __declspec(dllexport)    x   __cdecl
#else
#define TOUPCAM_LABVIEW_API(x) __declspec(dllimport)    x   __cdecl
#include "toupcam.h"
#endif

#ifdef __cplusplus
extern "C" {
#endif

TOUPCAM_LABVIEW_API(HRESULT) Start(HToupcam h, LVUserEventRef* rwer);

#ifdef __cplusplus
}
#endif

#endif