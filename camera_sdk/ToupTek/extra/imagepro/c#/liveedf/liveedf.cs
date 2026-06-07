using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
#if !(NETFX_CORE || NETCOREAPP || WINDOWS_UWP)
using System.Security.Permissions;
using System.Runtime.ConstrainedExecution;
#endif
using System.Collections.Generic;
using System.Threading;

internal class Liveedf : IDisposable
{
    public enum eFormat : uint
    {
        eRGB24,
        eRGB48,
        eRGBA32,
        eRGBA64
    };

    public enum eMethod : uint
    {
        ePyr_Max,
        ePyr_Weighted,
        eStack
    };

    public enum eEvent : uint
    {
        eNONE,
        eERROR,
        eNOMEM /* out of memory */
    };

    public static Liveedf New(eFormat format, eMethod method, DelegateCallback delegateCallback, DelegateECallback delegateECallback)
    {
        IntPtr id = new IntPtr(Interlocked.Increment(ref sid_));
        LIVEEDF_CALLBACK ptrCallback = delegate (IntPtr ctx, int result, IntPtr outData, int stride, int outW, int outH, int outType)
        {
            Object obj = null;
            if (map_.TryGetValue(ctx.ToInt32(), out obj) && (obj != null))
            {
                Liveedf pthis = obj as Liveedf;
                if (pthis != null)
                    pthis.delegateCallback_(result, outData, stride, outW, outH, outType);
            }
        };
        LIVEEDF_ECALLBACK ptrECallback = delegate (IntPtr ctx, eEvent evt)
        {
            Object obj = null;
            if (map_.TryGetValue(ctx.ToInt32(), out obj) && (obj != null))
            {
                Liveedf pthis = obj as Liveedf;
                if (pthis != null)
                    pthis.delegateECallback_(evt);
            }
        };
        SafeLiveedfHandle h = imagepro_edf_newV2(format, method, ptrCallback, ptrECallback, id);
        if (h == null || h.IsInvalid || h.IsClosed)
            return null;
        return new Liveedf(h, id, ptrCallback, delegateCallback, ptrECallback, delegateECallback);
    }

    public void Start()
    {
        imagepro_edf_start(handle_);
    }

    public void Stop()
    {
        imagepro_edf_stop(handle_);
    }

    public void ReadData(IntPtr data, int stride)
    {
        imagepro_edf_readdata(handle_, data, stride);
    }

    [Obsolete]
    public int Pull(Toupcam.SafeCamHandle hToupcam, int bFeed, IntPtr pImageData, int bits, int rowPitch, out Toupcam.FrameInfoV2 pInfo)
    {
        return imagepro_edf_pull(handle_, hToupcam, bFeed, pImageData, bits, rowPitch, out pInfo);
    }

    [Obsolete]
    public int Pull(Toupcam.SafeCamHandle hToupcam, int bFeed, IntPtr pImageData, int bits, int rowPitch, out Toupcam.FrameInfoV3 pInfo)
    {
        return imagepro_edf_pullV3(handle_, hToupcam, bFeed, pImageData, bits, rowPitch, out pInfo);
    }

    public int Pull(Toupcam.SafeCamHandle hToupcam, int bFeed, IntPtr pImageData, int bits, int rowPitch, out Toupcam.FrameInfoV4 pInfo)
    {
        return imagepro_edf_pullV4(handle_, hToupcam, bFeed, pImageData, bits, rowPitch, out pInfo);
    }

    private static int sid_ = 0;
    private static Dictionary<int, Object> map_ = new Dictionary<int, Object>();
    
    private SafeLiveedfHandle handle_;
    private IntPtr id_;
    private DelegateCallback delegateCallback_;
    private DelegateECallback delegateECallback_;
    private LIVEEDF_CALLBACK ptrCallback_;
    private LIVEEDF_ECALLBACK ptrECallback_;

    /*
        the object of Liveedf must be obtained by static mothod New, it cannot be obtained by obj = new Liveedf (The constructor is private on purpose)
    */
    private Liveedf(SafeLiveedfHandle h, IntPtr id, LIVEEDF_CALLBACK ptrCallback, DelegateCallback delegateCallback, LIVEEDF_ECALLBACK ptrECallback, DelegateECallback delegateECallback)
    {
        handle_ = h;
        id_ = id;
        ptrCallback_ = ptrCallback;
        delegateCallback_ = delegateCallback;
        ptrECallback_ = ptrECallback;
        delegateECallback_ = delegateECallback;
        map_.Add(id_.ToInt32(), this);
    }
    
    ~Liveedf()
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
    public class SafeLiveedfHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        private static extern void imagepro_edf_delete(IntPtr h);
        
        public SafeLiveedfHandle()
            : base(true)
        {
        }
        
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        override protected bool ReleaseHandle()
        {
            // Here, we must obey all rules for constrained execution regions.
            imagepro_edf_delete(handle);
            return true;
        }
    };
#else
    public class SafeLiveedfHandle : SafeHandle
    {
        [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        private static extern void imagepro_edf_delete(IntPtr h);
        
        public SafeLiveedfHandle()
            : base(IntPtr.Zero, true)
        {
        }
        
        override protected bool ReleaseHandle()
        {
            imagepro_edf_delete(handle);
            return true;
        }
        
        public override bool IsInvalid
        {
            get { return base.handle == IntPtr.Zero || base.handle == (IntPtr)(-1); }
        }
    };
#endif

    public delegate void DelegateCallback(int result, IntPtr outData, int stride, int outW, int outH, int outType);
    public delegate void DelegateECallback(eEvent evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LIVEEDF_CALLBACK(IntPtr ctx, int result, IntPtr outData, int stride, int outW, int outH, int outType);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LIVEEDF_ECALLBACK(IntPtr ctx, eEvent evt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr IMAGEPRO_MALLOC(IntPtr size);

    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_init(IMAGEPRO_MALLOC pfun);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern SafeLiveedfHandle imagepro_edf_newV2(eFormat format, eMethod method, LIVEEDF_CALLBACK pEdfFun, LIVEEDF_ECALLBACK pEventFun, IntPtr ctx);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_edf_start(SafeLiveedfHandle handle);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_edf_stop(SafeLiveedfHandle handle);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_edf_readdata(SafeLiveedfHandle handle, IntPtr data, int stride);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl), Obsolete]
    private static extern int imagepro_edf_pull(SafeLiveedfHandle handle, Toupcam.SafeCamHandle hToupcam, int bFeed, IntPtr pImageData, int bits, int rowPitch, out Toupcam.FrameInfoV2 pInfo);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl), Obsolete]
    private static extern int imagepro_edf_pullV3(SafeLiveedfHandle handle, Toupcam.SafeCamHandle hToupcam, int bFeed, IntPtr pImageData, int bits, int rowPitch, out Toupcam.FrameInfoV3 pInfo);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int imagepro_edf_pullV4(SafeLiveedfHandle handle, Toupcam.SafeCamHandle hToupcam, int bFeed, IntPtr pImageData, int bits, int rowPitch, out Toupcam.FrameInfoV4 pInfo);
}