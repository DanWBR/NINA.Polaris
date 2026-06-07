using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
#if !(NETFX_CORE || NETCOREAPP || WINDOWS_UWP)
using System.Security.Permissions;
using System.Runtime.ConstrainedExecution;
#endif
using System.Collections.Generic;
using System.Threading;

internal class Livestack : IDisposable
{
    public const int NUM_MIN = 1;
    public const int NUM_MAX = 99;
    public const int NUM_DEF = 10;
    
    public enum eError : uint
    {
        eNONE,
        eEVALFAIL,
        eNOENOUGHSTARS,
        eNOENOUGHMATCHES
    };
    
    public enum eMode : uint
    {
        eNONE,
        eSTACK,
        eMEAN
    };
    
    public enum eType : uint
    {
        eNONE,
        ePLANET,
        eDEEPSKY
    };
    
    public static Livestack New(eMode emode, eType etype, DelegateCallback delegateCallback)
    {
		IntPtr id = new IntPtr(Interlocked.Increment(ref sid_));
		LIVESTACK_CALLBACK ptrCallback = delegate (IntPtr ctx, int width, int height, int type, eError err, IntPtr data)
		{
            Object obj = null;
            if (map_.TryGetValue(ctx.ToInt32(), out obj) && (obj != null))
            {
                Livestack pthis = obj as Livestack;
                if (pthis != null)
                    pthis.delegateCallback_(width, height, type, err, data);
            }
		};
        SafeLivestackHandle h = imagepro_livestack_new(emode, etype, ptrCallback, id);
        if (h == null || h.IsInvalid || h.IsClosed)
            return null;
        return new Livestack(h, id, ptrCallback, delegateCallback);
    }

    public void Start()
    {
        imagepro_livestack_start(handle_);
    }

    public void Stop()
    {
        imagepro_livestack_stop(handle_);
    }

    public void Ref(IntPtr data, int width, int height, int depth)
    {
        imagepro_livestack_ref(handle_, data, width, height, depth);
    }

    public void Ref(byte[] data, int width, int height, int depth)
    {
        imagepro_livestack_ref(handle_, data, width, height, depth);
    }

    public void Ref(ushort[] data, int width, int height, int depth)
    {
        imagepro_livestack_ref(handle_, data, width, height, depth);
    }

    public void Add(IntPtr data, int width, int height, int depth)
    {
        imagepro_livestack_add(handle_, data, width, height, depth);
    }

    public void Add(byte[] data, int width, int height, int depth)
    {
        imagepro_livestack_add(handle_, data, width, height, depth);
    }

    public void Add(ushort[] data, int width, int height, int depth)
    {
        imagepro_livestack_add(handle_, data, width, height, depth);
    }

    public void SetNum(int num)
    {
        imagepro_livestack_setnum(handle_, num);
    }
	
    private static int sid_ = 0;
    private static Dictionary<int, Object> map_ = new Dictionary<int, Object>();
	
    private SafeLivestackHandle handle_;
	private IntPtr id_;
	private DelegateCallback delegateCallback_;
	private LIVESTACK_CALLBACK ptrCallback_;
    
	/*
        the object of Livestack must be obtained by static mothod New, it cannot be obtained by obj = new Livestack (The constructor is private on purpose)
    */
    private Livestack(SafeLivestackHandle h, IntPtr id, LIVESTACK_CALLBACK ptrCallback, DelegateCallback delegateCallback)
    {
        handle_ = h;
		id_ = id;
		ptrCallback_ = ptrCallback;
		delegateCallback_ = delegateCallback;
        map_.Add(id_.ToInt32(), this);
    }
    
    ~Livestack()
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
    public class SafeLivestackHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        private static extern void imagepro_livestack_delete(IntPtr h);
        
        public SafeLivestackHandle()
            : base(true)
        {
        }
        
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        override protected bool ReleaseHandle()
        {
            // Here, we must obey all rules for constrained execution regions.
            imagepro_livestack_delete(handle);
            return true;
        }
    };
#else
    public class SafeLivestackHandle : SafeHandle
    {
        [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        private static extern void imagepro_livestack_delete(IntPtr h);
        
        public SafeLivestackHandle()
            : base(IntPtr.Zero, true)
        {
        }
        
        override protected bool ReleaseHandle()
        {
            imagepro_livestack_delete(handle);
            return true;
        }
        
        public override bool IsInvalid
        {
            get { return base.handle == IntPtr.Zero || base.handle == (IntPtr)(-1); }
        }
    };
#endif
    
	public delegate void DelegateCallback(int width, int height, int type, eError err, IntPtr data);
	
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LIVESTACK_CALLBACK(IntPtr ctx, int width, int height, int type, eError err, IntPtr data);
	
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern SafeLivestackHandle imagepro_livestack_new(eMode mode, eType type, LIVESTACK_CALLBACK fun, IntPtr ctx);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_livestack_start(SafeLivestackHandle handle);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_livestack_stop(SafeLivestackHandle handle);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_livestack_ref(SafeLivestackHandle handle, IntPtr data, int width, int height, int depth);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_livestack_ref(SafeLivestackHandle handle, byte[] data, int width, int height, int depth);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_livestack_ref(SafeLivestackHandle handle, ushort[] data, int width, int height, int depth);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_livestack_add(SafeLivestackHandle handle, IntPtr data, int width, int height, int depth);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_livestack_add(SafeLivestackHandle handle, byte[] data, int width, int height, int depth);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_livestack_add(SafeLivestackHandle handle, ushort[] data, int width, int height, int depth);
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_livestack_setalign(SafeLivestackHandle handle, int align); /* align: true or false */
    [DllImport("imagepro.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void imagepro_livestack_setnum(SafeLivestackHandle handle, int num);
}