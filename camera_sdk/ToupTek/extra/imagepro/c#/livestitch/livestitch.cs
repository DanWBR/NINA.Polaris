using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
#if !(NETFX_CORE || NETCOREAPP || WINDOWS_UWP)
using System.Security.Permissions;
using System.Runtime.ConstrainedExecution;
#endif
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
internal class Livestitch : IDisposable
{
    public enum eFormat : uint
    {
        eRGB24,
        eRGB48,
        eRGBA32,
        eRGBA64
    };

    public enum eRet : uint
    {
        eEMPTY,
        eNORMAL,
        eNORMAL_REF
    }

    public enum eQuality : uint
    {
        eZERO,
        eGOOD,
        eCAUTION,
        eBAD,
        eWARNING
    }

    public enum eStatus : uint
    {
        eNONE,
        eNORMAL,
        eAREPAIR, //auto repair
        eMREPAIR, //manual repair
        eRESET,
        eRESTART
    }

    public enum eDirection : uint
    {
        eSTILL,
        ePLUS,
        eMINUS
    }

    public enum eEvent : uint
    {
        eNONE,
        eERROR,
        eNOMEM, /* out of memory */
        eEXPAND,
        eEXPAND_FAILURE,
        eEXPAND_SUCCESS,
        eENTER_NORMAL,
        eENTER_AREPAIR,
        eLEAVE_AREPAIR,
        eENTER_MREPAIR,
        eLEAVE_MREPAIR,
        eENTER_RESET,
        eLEAVE_RESET,
        eENTER_RESTART,
        eLEAVE_RESTART,
        eAREPAIR_STOP_X,
        eAREPAIR_STOP_Y,
        eAREPAIR_KEEP_X,
        eAREPAIR_KEEP_Y,
        eAREPAIR_REVERSE_X,
        eAREPAIR_REVERSE_Y,
        eAREPAIR_RIGHT_DIR,
        eMREPAIR_START_MOVING,
        eMREPAIR_REF_FAILURE,
        eMREPAIR_RETRY,
        eRESTART_START
    };
	
	public enum ePrecision : uint
	{
		ePrecisionLower,
		ePrecisionLow,
		ePrecisionMedium,
		ePrecisionHigh,
		ePrecisionHigher
	};

	public enum eThreshold : uint
	{
		eThresholdSmaller,
		eThresholdSmall,
		eThresholdMedium,
		eThresholdLarge,
		eThresholdLarger
	};

    public enum BitmapCompression : uint
    {
        BI_RGB = 0,
        BI_RLE8 = 1,
        BI_RLE4 = 2,
        BI_BITFIELDS = 3,
        BI_JPEG = 4,
        BI_PNG = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public BitmapCompression biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;

        public IntPtr buffer;
    }

    public static IntPtr IncIntPtr(IntPtr p, int offset)
    {
        return new IntPtr(p.ToInt64() + offset);
    }

    public static Livestitch New(eFormat format, int bGlobalShutter, int videoW, int videoH, int background, DelegateCallback delegateCallback, DelegateECallback delegateECallback, ePrecision precision = ePrecision.ePrecisionMedium, eThreshold threshold = eThreshold.eThresholdMedium)
    {
        IntPtr id = new IntPtr(Interlocked.Increment(ref sid_));
        LIVESTITCH_CALLBACK ptrCallback = delegate (IntPtr ctx, IntPtr outData, int stride, int outW, int outH, int curW, int curH, int curType,
                                        int posX, int posY, eQuality quality, float sharpness, int bUpdate, int bSize)
        {
            Object obj = null;
            if (map_.TryGetValue(ctx.ToInt32(), out obj) && (obj != null))
            {
                Livestitch pthis = obj as Livestitch;
                if (pthis != null)
                    pthis.delegateCallback_(outData, stride, outW, outH, curW, curH, curType, posX, posY, quality, sharpness, bUpdate, bSize);
            }
        };
        LIVESTITCH_ECALLBACK ptrECallback = delegate (IntPtr ctx, eEvent evt)
        {
            Object obj = null;
            if (map_.TryGetValue(ctx.ToInt32(), out obj) && (obj != null))
            {
                Livestitch pthis = obj as Livestitch;
                if (pthis != null)
                    pthis.delegateECallback_(evt);
            }
        };
        SafeLivestitchHandle h = imagepro_stitch_newV2(format, bGlobalShutter, videoW, videoH, background, ptrCallback, ptrECallback, id);
        if (h == null || h.IsInvalid || h.IsClosed)
            return null;
        return new Livestitch(h, id, ptrCallback, delegateCallback, ptrECallback, delegateECallback);
    }

    public static void init(IMAGEPRO_MALLOC pfun)
    {
        imagepro_init(pfun);
    }

    public void Start()
    {
        imagepro_stitch_start(handle_);
    }

    public void Stop(int normal, int crop, ref BitmapInfo info)
    {
        IntPtr ptr = imagepro_stitch_stop(handle_, normal, crop);
        info.bmiHeader.biSize = (uint)Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, sizeof(uint));
        info.bmiHeader.biWidth = (int)Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, sizeof(int));
        info.bmiHeader.biHeight = (int)Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, sizeof(int));
        info.bmiHeader.biPlanes = (ushort)Marshal.ReadInt16(ptr);
        ptr = IncIntPtr(ptr, sizeof(ushort));
        info.bmiHeader.biBitCount = (ushort)Marshal.ReadInt16(ptr);
        ptr = IncIntPtr(ptr, sizeof(ushort));
        info.bmiHeader.biCompression = (BitmapCompression)Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, sizeof(BitmapCompression));
        info.bmiHeader.biSizeImage = (uint)Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, sizeof(uint));
        info.bmiHeader.biXPelsPerMeter = (int)Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, sizeof(int));
        info.bmiHeader.biYPelsPerMeter = (int)Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, sizeof(int));
        info.bmiHeader.biClrUsed = (uint)Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, sizeof(uint));
        info.bmiHeader.biClrImportant = (uint)Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, sizeof(uint));
        info.buffer = ptr;
    }

    public void ReadData(IntPtr data, int w, int h, int roix, int roiy, int roiw, int roih)
    {
        imagepro_stitch_readdata(handle_, data, w, h, roix, roiy, roiw, roih);
    }
    public void Close()
    {
        Dispose();
    }

    [Obsolete]
    public int Pull(Toupcam.SafeCamHandle hToupcam, int bFeed, IntPtr pImageData, int bits, int rowPitch, out Toupcam.FrameInfoV2 pInfo)
    {
        return imagepro_stitch_pull(handle_, hToupcam, bFeed, pImageData, bits, rowPitch, out pInfo);
    }

    [Obsolete]
    public int Pull(Toupcam.SafeCamHandle hToupcam, int bFeed, IntPtr pImageData, int bits, int rowPitch, out Toupcam.FrameInfoV3 pInfo)
    {
        return imagepro_stitch_pullV3(handle_, hToupcam, bFeed, pImageData, bits, rowPitch, out pInfo);
    }

    public int Pull(Toupcam.SafeCamHandle hToupcam, int bFeed, IntPtr pImageData, int bits, int rowPitch, out Toupcam.FrameInfoV4 pInfo)
    {
        return imagepro_stitch_pullV4(handle_, hToupcam, bFeed, pImageData, bits, rowPitch, out pInfo);
    }

    private static int sid_ = 0;
    private static Dictionary<int, Object> map_ = new Dictionary<int, Object>();

    private SafeLivestitchHandle handle_;
    private IntPtr id_;
    private DelegateCallback delegateCallback_;
    private DelegateECallback delegateECallback_;
    private LIVESTITCH_CALLBACK ptrCallback_;
    private LIVESTITCH_ECALLBACK ptrECallback_;

    private Livestitch(SafeLivestitchHandle h, IntPtr id, LIVESTITCH_CALLBACK ptrCallback, DelegateCallback delegateCallback, LIVESTITCH_ECALLBACK ptrECallback, DelegateECallback delegateECallback)
    {
        handle_ = h;
        id_ = id;
        ptrCallback_ = ptrCallback;
        delegateCallback_ = delegateCallback;
        ptrECallback_ = ptrECallback;
        delegateECallback_ = delegateECallback;
        map_.Add(id_.ToInt32(), this);
    }

    ~Livestitch()
    {
        Dispose(false);
    }

    public void Dispose()  // Follow the Dispose pattern - public nonvirtual.
    {
        Dispose(true);
        map_.Remove(id_.ToInt32());
        GC.SuppressFinalize(this);
    }

#if !(NETFX_CORE || NETCOREAPP || WINDOWS_UWP)
    [SecurityPermission(SecurityAction.Demand, UnmanagedCode = true)]
#endif

    protected virtual void Dispose(bool disposing)
    {
        // Note there are three interesting states here:
        // 1) CreateFile failed, _handle contains an invalid handle
        // 2) We called Dispose already, _handle is closed.
        // 3) _handle is null, due to an async exception before
        //    calling CreateFile. Note that the finalizer runs
        //    if the constructor fails.
        if (handle_ != null && !handle_.IsInvalid)
        {
            // Free the handle
            handle_.Dispose();
        }
        // SafeHandle records the fact that we've called Dispose.
    }

#if !(NETFX_CORE || NETCOREAPP || WINDOWS_UWP)
    public class SafeLivestitchHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        private static extern void imagepro_stitch_delete(IntPtr h);
        
        public SafeLivestitchHandle()
            : base(true)
        {
        }
        
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        override protected bool ReleaseHandle()
        {
            // Here, we must obey all rules for constrained execution regions.
            imagepro_stitch_delete(handle);
            return true;
        }
    };
#else
    public class SafeLivestitchHandle : SafeHandle
    {
        [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        private static extern void imagepro_stitch_delete(IntPtr h);

        public SafeLivestitchHandle()
            : base(IntPtr.Zero, true)
        {
        }

        override protected bool ReleaseHandle()
        {
            imagepro_stitch_delete(handle);
            return true;
        }

        public override bool IsInvalid
        {
            get { return base.handle == IntPtr.Zero || base.handle == (IntPtr)(-1); }
        }
    };
#endif
    public delegate void DelegateCallback(IntPtr outData, int stride, int outW, int outH, int curW, int curH, int curType,
                                        int posX, int posY, eQuality quality, float sharpness, int bUpdate, int bSize);
    public delegate void DelegateECallback(eEvent evt);
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LIVESTITCH_CALLBACK(IntPtr ctx, IntPtr outData, int stride, int outW, int outH, int curW, int curH, int curType,
                                        int posX, int posY, eQuality quality, float sharpness, int bUpdate, int bSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LIVESTITCH_ECALLBACK(IntPtr ctx, eEvent evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr IMAGEPRO_MALLOC(IntPtr size);

    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_init(IMAGEPRO_MALLOC pfun);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern SafeLivestitchHandle imagepro_stitch_newV2(eFormat format, int bGlobalShutter, int videoW, int videoH, int background, LIVESTITCH_CALLBACK pEdfFun, LIVESTITCH_ECALLBACK pEventFun, IntPtr ctx);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern SafeLivestitchHandle imagepro_stitch_newV3(eFormat format, int bGlobalShutter, int videoW, int videoH, int background, ePrecision precision, eThreshold threshold, LIVESTITCH_CALLBACK pEdfFun, LIVESTITCH_ECALLBACK pEventFun, IntPtr ctx);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_stitch_delete(SafeLivestitchHandle handle);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_stitch_start(SafeLivestitchHandle handle);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr imagepro_stitch_stop(SafeLivestitchHandle handle, int normal, int crop);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_stitch_readdata(SafeLivestitchHandle handle, IntPtr data, int w, int h, int roix = 0, int roiy = 0, int roiw = 0, int roih = 0);

    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl), Obsolete]
    private static extern int imagepro_stitch_pull(SafeLivestitchHandle handle, Toupcam.SafeCamHandle hToupcam, int bFeed, IntPtr pImageData, int bits, int rowPitch, out Toupcam.FrameInfoV2 pInfo);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl), Obsolete]
    private static extern int imagepro_stitch_pullV3(SafeLivestitchHandle handle, Toupcam.SafeCamHandle hToupcam, int bFeed, IntPtr pImageData, int bits, int rowPitch, out Toupcam.FrameInfoV3 pInfo);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int imagepro_stitch_pullV4(SafeLivestitchHandle handle, Toupcam.SafeCamHandle hToupcam, int bFeed, IntPtr pImageData, int bits, int rowPitch, out Toupcam.FrameInfoV4 pInfo);
}
