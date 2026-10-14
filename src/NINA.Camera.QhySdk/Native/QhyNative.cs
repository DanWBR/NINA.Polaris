// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using System.Runtime.InteropServices;

namespace NINA.Camera.QhySdk.Native;

/// <summary>
/// P/Invoke surface for the QHYCCD SDK (<c>qhyccd</c> → <c>qhyccd.dll</c> /
/// <c>libqhyccd.so</c>). Names and argument order mirror <c>qhyccd.h</c> from
/// the SDK 25.06.16 headers; only the calls the backend makes are bound.
///
/// <para>Calling convention: the header declares every export
/// <c>EXPORTC ... STDCALL</c>, and <c>STDCALL</c> is <c>__stdcall</c> on
/// Windows and empty everywhere else. <see cref="CallingConvention.Winapi"/>
/// is exactly that rule (stdcall on Windows, the platform C convention
/// elsewhere). On x64 and arm64 there is only one convention anyway, so this
/// only matters for a 32-bit Windows process.</para>
///
/// <para>Return codes: <c>QHYCCD_SUCCESS</c> is 0 and the generic failure is
/// <c>QHYCCD_ERROR</c> = 0xFFFFFFFF; nearly every call reports failure with
/// that one value. <c>CONTROL_ID</c> is a C++ enum, so it travels as an
/// <c>int</c>.</para>
/// </summary>
internal static class QhyNative {
    private const string DLL = "qhyccd";

    public const uint QHYCCD_SUCCESS = 0;
    public const uint QHYCCD_ERROR = 0xFFFFFFFF;
    /// <summary>The camera disappeared from the bus (<c>QHYCCD_ERROR_DEVICELOSE</c>, -3).</summary>
    public const uint QHYCCD_ERROR_DEVICELOSE = unchecked((uint)-3);
    /// <summary>SetQHYCCDBinMode asks the host to wait 200 ms before going on.</summary>
    public const uint QHYCCD_DELAY_200MS = 0x2000;
    /// <summary>ExpQHYCCDSingleFrame: the frame is already there, read it now.</summary>
    public const uint QHYCCD_READ_DIRECTLY = 0x2001;

    /// <summary>Buffer sizes for the id / model strings. The SDK writes a
    /// NUL-terminated string into memory the caller owns and takes no length,
    /// so these are generous: ids look like "QHY600M-6bb54f4c1d2e3f40" (well
    /// under 32), the header's model buffers are 256 elsewhere.</summary>
    public const int IdBufferLength = 64;
    public const int ModelBufferLength = 256;

    /// <summary>Stream modes for SetQHYCCDStreamMode.</summary>
    public const byte SingleFrameMode = 0;

    /// <summary>The <c>CONTROL_ID</c> values this backend touches, numbered
    /// exactly as in <c>qhyccdstruct.h</c> (the enum has gaps, so every value
    /// is spelled out rather than left to auto-increment).</summary>
    public enum CONTROL_ID {
        CONTROL_GAIN = 6,
        CONTROL_OFFSET = 7,
        CONTROL_EXPOSURE = 8,          // microseconds
        CONTROL_SPEED = 9,
        CONTROL_TRANSFERBIT = 10,
        CONTROL_USBTRAFFIC = 12,
        CONTROL_ROWNOISERE = 13,
        CONTROL_CURTEMP = 14,          // degrees C
        CONTROL_CURPWM = 15,           // 0..255 on most models
        CONTROL_MANULPWM = 16,
        CONTROL_CFWPORT = 17,          // the camera's filter wheel port
        CONTROL_COOLER = 18,
        CAM_COLOR = 20,                // IsQHYCCDControlAvailable returns the BAYER_ID
        CAM_BIN1X1MODE = 21,
        CAM_BIN2X2MODE = 22,
        CAM_BIN3X3MODE = 23,
        CAM_BIN4X4MODE = 24,
        CAM_IGNOREOVERSCAN_INTERFACE = 37,
        CONTROL_CFWSLOTSNUM = 44,      // slots on the attached filter wheel
        CONTROL_DDR = 48,
        CAM_BIN6X6MODE = 75,
        CAM_BIN8X8MODE = 76,
    }

    /// <summary><c>BAYER_ID</c>: what IsQHYCCDControlAvailable(CAM_COLOR)
    /// returns for a colour sensor. A mono sensor answers QHYCCD_ERROR.</summary>
    public enum BAYER_ID : uint {
        BAYER_GB = 1,
        BAYER_GR = 2,
        BAYER_BG = 3,
        BAYER_RG = 4,
    }

    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint InitQHYCCDResource();
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint ReleaseQHYCCDResource();
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint ScanQHYCCD();
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint GetQHYCCDId(uint index, byte[] id);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern uint GetQHYCCDModel(string id, byte[] model);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern IntPtr OpenQHYCCD(string id);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint CloseQHYCCD(IntPtr handle);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint InitQHYCCD(IntPtr handle);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint SetQHYCCDStreamMode(IntPtr handle, byte mode);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)]
    public static extern uint GetQHYCCDChipInfo(IntPtr handle, out double chipw, out double chiph,
        out uint imagew, out uint imageh, out double pixelw, out double pixelh, out uint bpp);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)]
    public static extern uint GetQHYCCDEffectiveArea(IntPtr handle, out uint startX, out uint startY, out uint sizeX, out uint sizeY);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint IsQHYCCDControlAvailable(IntPtr handle, CONTROL_ID controlId);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)]
    public static extern uint GetQHYCCDParamMinMaxStep(IntPtr handle, CONTROL_ID controlId, out double min, out double max, out double step);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint SetQHYCCDParam(IntPtr handle, CONTROL_ID controlId, double value);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern double GetQHYCCDParam(IntPtr handle, CONTROL_ID controlId);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint SetQHYCCDBinMode(IntPtr handle, uint wbin, uint hbin);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint SetQHYCCDResolution(IntPtr handle, uint x, uint y, uint xsize, uint ysize);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint SetQHYCCDBitsMode(IntPtr handle, uint bits);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint SetQHYCCDDebayerOnOff(IntPtr handle, [MarshalAs(UnmanagedType.U1)] bool onoff);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint ExpQHYCCDSingleFrame(IntPtr handle);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint GetQHYCCDMemLength(IntPtr handle);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)]
    public static extern uint GetQHYCCDSingleFrame(IntPtr handle, ref uint w, ref uint h, ref uint bpp, ref uint channels, byte[] imgdata);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint CancelQHYCCDExposingAndReadout(IntPtr handle);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint ControlQHYCCDTemp(IntPtr handle, double targettemp);
    // The filter wheel on the camera's CFW port (or built in, as on the
    // MiniCAM8) is driven through the camera's own handle. The order is the
    // target slot as one ASCII hex digit ("0" = first slot); the status is
    // the same digit once the wheel has stopped, and "N" (CFW2/CFW3) or "/"
    // (A-series) while it moves.
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint IsQHYCCDCFWPlugged(IntPtr handle);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint GetQHYCCDCFWStatus(IntPtr handle, byte[] status);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)] public static extern uint SendOrder2QHYCCDCFW(IntPtr handle, byte[] order, uint length);
    [DllImport(DLL, CallingConvention = CallingConvention.Winapi)]
    public static extern uint GetQHYCCDSDKVersion(out uint year, out uint month, out uint day, out uint subday);
}
