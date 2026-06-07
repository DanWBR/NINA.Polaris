using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

/*
    a thin wrapper class for imagelib.dll (P/Invoke)
*/
public class ImageLib
{
    public enum IMAGEFORMAT : uint
    {
        IMAGEFORMAT_UNKNOWN,
        IMAGEFORMAT_BMP,
        IMAGEFORMAT_JPG,
        IMAGEFORMAT_PNG,
        IMAGEFORMAT_TIF,
        IMAGEFORMAT_GIF,
        IMAGEFORMAT_PCX,
        IMAGEFORMAT_TGA,
        IMAGEFORMAT_PSD,
        IMAGEFORMAT_ICO,
        IMAGEFORMAT_EMF,
        IMAGEFORMAT_WMF,
        IMAGEFORMAT_JBG,
        IMAGEFORMAT_WBMP,
        IMAGEFORMAT_JP2,
        IMAGEFORMAT_J2K,
        IMAGEFORMAT_DCM,
        IMAGEFORMAT_DNG,
        IMAGEFORMAT_WEBP,
        IMAGEFORMAT_SVS,
        IMAGEFORMAT_MAX
    };
    
    public const byte IL_MERIT_ZERO           = 0;
    public const byte IL_MERIT_NONE           = 1;
    public const byte IL_MERIT_LOW            = 16;
    public const byte IL_MERIT_NORMAL         = 64;
    public const byte IL_MERIT_HIGH           = 128;
    public const byte IL_MERTI_MAX            = 255;

    public const byte GIFCODEC_LZW            = 0;
    public const byte GIFCODEC_RLE            = 1;
    public const byte GIFCODEC_NONE           = 2;

    public const byte DCMCODEC_LOSSLESS_JPEG  = 0;
    public const byte DCMCODEC_RLE            = 1;
    public const byte DCMCODEC_NONE           = 2;

    public const byte DNGCODEC_LOSSLESS       = 0;
    public const byte DNGCODEC_NONE           = 1;

    public const byte DEF_COMPRESSION_LEVEL   = 50;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER  /* https://docs.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-bitmapinfoheader */
    {
        public uint         biSize;
        public int          biWidth;
        public int          biHeight;
        public ushort       biPlanes;
        public ushort       biBitCount;
        public uint         biCompression;
        public uint         biSizeImage;
        public int          biXPelsPerMeter;
        public int          biYPelsPerMeter;
        public uint         biClrUsed;
        public uint         biClrImportant;
    }

    public struct ExtFmt
    {
        public string       ext;    /* file name extension,such as jpg,bmp,tif,gif */
        public IMAGEFORMAT  fmt;    /* image format */
    };
    
    public struct FmtCodec
    {
        public IMAGEFORMAT  fmt;    /* image format */
        public byte         code;   /* this codec support compression? */
        public byte         decode; /* this codec support decompression? */
        public string       name;   /* codec name */
        public uint         codec;  /* codec */
    };
    
    public struct XIMAGEINFO
    {
        public IMAGEFORMAT  iType;              /* original image format */
        public uint         iCodec;             /* to tiff: see s_codec */
                                                /* to gif: LZW(default), RLE, NONE */
                                                /* to dng: 0->lossless jpeg compress, 1->none */
        public bool         bAppend;            /* used for TIFF, GIF: append file? TRUE or FALSE */
        public bool         bInterlaced;        /* used for PNG, GIF, TRUE or FALSE */
        public bool         bPreview;           /* decoding this image for previewing, so sometimes we may decode it with low quality, TRUE or FALSE */
        public byte         nQuality;           /* used for saving JPEG & JP2000, 0..100 */
                                                /* used for saving PNG, 0..100, compression level */
        public byte         nSmoothing;         /* used for saving JPEG & JP2000: 0..100 */
        public bool         bProgressive;       /* used for JPEG: is it a progressive jpeg? TRUE or FALSE */
        public bool         bOptimize;          /* used for saving JPEG: optimize entropy encoding? TRUE or FALSE */
        public int          nPage;              /* used for TIF, GIF, ICO: current page */
        public int          nPages;             /* used for TIF, GIF, ICO: total number of pages */
        public uint         nDelay;             /* used for GIF: (100ms) */
        public bool         bOpenFile;          /* open file */
        public double       dPPM;
        public int          ExpoTimeA;          /* expo time */
        public int          ExpoTimeB;
        public int          latitude;
        public int          longitude;
        public byte[]       extTag;
        public ulong        timestamp;          /* timestamp in microseconds, 0 or -1 means NA */
        public uint         seq;                /* sequence number */
        public bool         bImageUniqueID;
        public string       cDateTimeOrg;
        public string       cDateTimeDig;
        public string       cCamera;
        public string       cSN;
        public string       cCopyright;
        public string       cArtist;
        public string       cComment;
        public string       cDescription;
        public string       cError;
        public bool         bHintGreyScale;
    };
    
    public struct FmtInfo
    {
        public IMAGEFORMAT  fmt;    /* image format */
        public string       ext;
        public string       abbr;   /* abbr. for format's name */
        public string       name;   /* image format's name */
        public byte         merit;  /* it's merit, [0, 255], see IL_MERIT_* */
        public bool         bpp1;
        public bool         bpp4;
        public bool         bpp8;
        public bool         bpp16;
        public bool         bpp24;
        public bool         bpp32;
        public bool         gray;
        public bool         multipage;
        public bool         motion;
        public bool         exif;   /* support exif */
    };
    
    [DllImport("kernel32.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern void RtlZeroMemory(IntPtr dst, int length);
    [DllImport("imagelib.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ImageLib_Open([MarshalAs(UnmanagedType.LPWStr)] string file, IntPtr pInfo);
    [DllImport("imagelib.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ImageLib_OpenMemory(IntPtr pData, int nLength, IntPtr pInfo);
    [DllImport("imagelib.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ImageLib_Free(IntPtr pDIB);
    [DllImport("imagelib.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImageLib_Save([MarshalAs(UnmanagedType.LPWStr)] string file, IntPtr pDIB, IntPtr pInfo);
    [DllImport("imagelib.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImageLib_CanSave(IntPtr pDIB, IntPtr pInfo);
    [DllImport("imagelib.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern IMAGEFORMAT ImageLib_GetExtFmt([MarshalAs(UnmanagedType.LPWStr)] string file);
    [DllImport("imagelib.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ImageLib_FmtInfo();
    [DllImport("imagelib.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ImageLib_ExtFmt();
    [DllImport("imagelib.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ImageLib_Codec();
    [DllImport("imagelib.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ImageLib_GetFmtInfo(IMAGEFORMAT iType);
    [DllImport("imagelib.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImageLib_SupportExif(IMAGEFORMAT iType);
    
    private static void CopyString(IntPtr ptr, string str, int length)
    {
        if (str != null)
        {
            byte[] ba = Encoding.Default.GetBytes(str);
            Marshal.Copy(ba, 0, ptr, ba.Length);
        }
    }

    /* only for compatibility with .Net 4.0 and below */
    private static IntPtr IncIntPtr(IntPtr p, int offset)
    {
        return new IntPtr(p.ToInt64() + offset);
    }

    private static XIMAGEINFO ToXIMAGEINFO(IntPtr ptr)
    {
        XIMAGEINFO p = new XIMAGEINFO();
        p.iType = (IMAGEFORMAT)Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, 4);
        p.iCodec = (uint)Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, 4);
        p.bAppend = Convert.ToBoolean(Marshal.ReadByte(ptr));
        ptr = IncIntPtr(ptr, 1);
        p.bInterlaced = Convert.ToBoolean(Marshal.ReadByte(ptr));
        ptr = IncIntPtr(ptr, 1);
        p.bPreview = Convert.ToBoolean(Marshal.ReadByte(ptr));
        ptr = IncIntPtr(ptr, 1);
        p.nQuality = Marshal.ReadByte(ptr);
        ptr = IncIntPtr(ptr, 1);
        p.nSmoothing = Marshal.ReadByte(ptr);
        ptr = IncIntPtr(ptr, 1);
        p.bProgressive = Convert.ToBoolean(Marshal.ReadByte(ptr));
        ptr = IncIntPtr(ptr, 1);
        p.bOptimize = Convert.ToBoolean(Marshal.ReadByte(ptr));
        ptr = IncIntPtr(ptr, 2);
        p.nPage = Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, 4);
        p.nPages = Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, 4);
        p.nDelay = (uint)Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, 4);
        p.bOpenFile = Convert.ToBoolean(Marshal.ReadByte(ptr));
        ptr = IncIntPtr(ptr, 4);
        p.ExpoTimeA = Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, 4);
        p.ExpoTimeB = Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, 4);
        p.latitude = Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, 4);
        p.longitude = Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, 4);
        ptr = IncIntPtr(ptr, IntPtr.Size);
        ptr = IncIntPtr(ptr, 4);
        p.timestamp = (ulong)Marshal.ReadInt64(ptr);
        ptr = IncIntPtr(ptr, 8);
        p.seq = (uint)Marshal.ReadInt32(ptr);
        ptr = IncIntPtr(ptr, 4);
        p.bImageUniqueID = Convert.ToBoolean(Marshal.ReadByte(ptr));
        ptr = IncIntPtr(ptr, 1);
        p.cDateTimeOrg = Marshal.PtrToStringAnsi(ptr);
        ptr = IncIntPtr(ptr, 24);
        p.cDateTimeDig = Marshal.PtrToStringAnsi(ptr);
        ptr = IncIntPtr(ptr, 24);
        p.cCamera = Marshal.PtrToStringAnsi(ptr);
        ptr = IncIntPtr(ptr, 32);
        p.cSN = Marshal.PtrToStringAnsi(ptr);
        ptr = IncIntPtr(ptr, 32);
        p.cCopyright = Marshal.PtrToStringAnsi(ptr);
        ptr = IncIntPtr(ptr, 256);
        p.cArtist = Marshal.PtrToStringAnsi(ptr);
        ptr = IncIntPtr(ptr, 256);
        p.cComment = Marshal.PtrToStringAnsi(ptr);
        ptr = IncIntPtr(ptr, 256);
        p.cDescription = Marshal.PtrToStringAnsi(ptr);
        ptr = IncIntPtr(ptr, 256);
        p.cError = Marshal.PtrToStringAnsi(ptr);
        ptr = IncIntPtr(ptr, 256);
        p.bHintGreyScale = Convert.ToBoolean(Marshal.ReadByte(ptr));
        return p;
    }
    
    private static IntPtr FromXIMAGEINFO(ref XIMAGEINFO pInfo)
    {
        IntPtr ptr = Marshal.AllocHGlobal(4096);
        RtlZeroMemory(ptr, 4096);
        IntPtr qtr = ptr;
        Marshal.WriteInt32(qtr, (Int32)pInfo.iType);
        qtr = IncIntPtr(qtr, 4);
        Marshal.WriteInt32(qtr, (Int32)pInfo.iCodec);
        qtr = IncIntPtr(qtr, 4);
        Marshal.WriteByte(qtr, pInfo.bAppend ? (byte)1 : (byte)0);
        qtr = IncIntPtr(qtr, 1);
        Marshal.WriteByte(qtr, pInfo.bInterlaced ? (byte)1 : (byte)0);
        qtr = IncIntPtr(qtr, 1);
        Marshal.WriteByte(qtr, pInfo.bPreview ? (byte)1 : (byte)0);
        qtr = IncIntPtr(qtr, 1);
        Marshal.WriteByte(qtr, pInfo.nQuality);
        qtr = IncIntPtr(qtr, 1);
        Marshal.WriteByte(qtr, pInfo.nSmoothing);
        qtr = IncIntPtr(qtr, 1);
        Marshal.WriteByte(qtr, pInfo.bProgressive ? (byte)1 : (byte)0);
        qtr = IncIntPtr(qtr, 1);
        Marshal.WriteByte(qtr, pInfo.bOptimize ? (byte)1 : (byte)0);
        qtr = IncIntPtr(qtr, 2);
        Marshal.WriteInt32(qtr, pInfo.nPage);
        qtr = IncIntPtr(qtr, 4);
        Marshal.WriteInt32(qtr, pInfo.nPages);
        qtr = IncIntPtr(qtr, 4);
        Marshal.WriteInt32(qtr, (Int32)pInfo.nDelay);
        qtr = IncIntPtr(qtr, 4);
        Marshal.WriteByte(qtr, pInfo.bOpenFile ? (byte)1 : (byte)0);
        qtr = IncIntPtr(qtr, 4);
        Marshal.WriteInt32(qtr, pInfo.ExpoTimeA);
        qtr = IncIntPtr(qtr, 4);
        Marshal.WriteInt32(qtr, pInfo.ExpoTimeB);
        qtr = IncIntPtr(qtr, 4);
        Marshal.WriteInt32(qtr, pInfo.latitude);
        qtr = IncIntPtr(qtr, 4);
        Marshal.WriteInt32(qtr, pInfo.longitude);
        qtr = IncIntPtr(qtr, 4);
        Marshal.WriteIntPtr(qtr, IntPtr.Zero);
        qtr = IncIntPtr(qtr, IntPtr.Size);
        Marshal.WriteInt32(qtr, 0);
        qtr = IncIntPtr(qtr, 4);
        Marshal.WriteInt64(qtr, (Int64)pInfo.timestamp);
        qtr = IncIntPtr(qtr, 8);
        Marshal.WriteInt32(qtr, (int)pInfo.seq);
        qtr = IncIntPtr(qtr, 4);
        Marshal.WriteByte(qtr, pInfo.bImageUniqueID ? (byte)1 : (byte)0);
        qtr = IncIntPtr(qtr, 1);
        CopyString(qtr, pInfo.cDateTimeOrg, 24);
        qtr = IncIntPtr(qtr, 24);
        CopyString(qtr, pInfo.cDateTimeDig, 24);
        qtr = IncIntPtr(qtr, 24);
        CopyString(qtr, pInfo.cCamera, 32);
        qtr = IncIntPtr(qtr, 32);
        CopyString(qtr, pInfo.cSN, 32);
        qtr = IncIntPtr(qtr, 32);
        CopyString(qtr, pInfo.cCopyright, 256);
        qtr = IncIntPtr(qtr, 256);
        CopyString(qtr, pInfo.cArtist, 256);
        qtr = IncIntPtr(qtr, 256);
        CopyString(qtr, pInfo.cComment, 256);
        qtr = IncIntPtr(qtr, 256);
        CopyString(qtr, pInfo.cDescription, 256);
        qtr = IncIntPtr(qtr, 256);
        CopyString(qtr, pInfo.cError, 256);
        qtr = IncIntPtr(qtr, 256);
        Marshal.WriteByte(qtr, pInfo.bHintGreyScale ? (byte)1 : (byte)0);
        return ptr;
    }

    public static int TDIBWIDTHBYTES(int bits)
    {
        return ((((bits) + 31) & (~31)) / 8);
    }

    public static IntPtr Open(string file, out XIMAGEINFO pInfo)
    {
        IntPtr ptr = Marshal.AllocHGlobal(4096);
        IntPtr ret = ImageLib_Open(file, ptr);
        pInfo = ToXIMAGEINFO(ptr);
        Marshal.FreeHGlobal(ptr);
        return ret;
    }
    
    public static IntPtr OpenMemory(IntPtr pData, int nLength, out XIMAGEINFO pInfo)
    {
        IntPtr ptr = Marshal.AllocHGlobal(4096);
        IntPtr ret = ImageLib_OpenMemory(pData, nLength, ptr);
        pInfo = ToXIMAGEINFO(ptr);
        Marshal.FreeHGlobal(ptr);
        return ret;
    }
    
    public static void Free(IntPtr pDIB)
    {
        ImageLib_Free(pDIB);
    }
    
    public static bool Save(string file, IntPtr pDIB, ref XIMAGEINFO pInfo)
    {
        IntPtr ptr = FromXIMAGEINFO(ref pInfo);
        bool ret = ImageLib_Save(file, pDIB, ptr);
        Marshal.FreeHGlobal(ptr);
        return ret;
    }
    
    public static bool CanSave(IntPtr pDIB, ref XIMAGEINFO pInfo)
    {
        IntPtr ptr = FromXIMAGEINFO(ref pInfo);
        bool ret = ImageLib_CanSave(pDIB, ptr);
        Marshal.FreeHGlobal(ptr);
        return ret;
    }
    
    public static IMAGEFORMAT GetExtFmt(string file)
    {
        return ImageLib_GetExtFmt(file);
    }
    
    private static FmtInfo ToFmtInfo(IntPtr p)
    {
        FmtInfo q = new FmtInfo();
        q.fmt = (IMAGEFORMAT)Marshal.ReadInt32(p);
        p = IncIntPtr(p, 4);
        q.ext = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p));
        p = IncIntPtr(p, IntPtr.Size);
        q.abbr = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p));
        p = IncIntPtr(p, IntPtr.Size);
        q.name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p));
        p = IncIntPtr(p, IntPtr.Size);
        q.merit = Marshal.ReadByte(p);
        p = IncIntPtr(p, 1);
        
        uint n = (uint)Marshal.ReadInt32(p);
        if ((n & 0x1) != 0)
            q.bpp1 = true;
        if ((n & 0x2) != 0)
            q.bpp4 = true;
        if ((n & 0x4) != 0)
            q.bpp8 = true;
        if ((n & 0x8) != 0)
            q.bpp16 = true;
        if ((n & 0x10) != 0)
            q.bpp24 = true;
        if ((n & 0x20) != 0)
            q.bpp32 = true;
        if ((n & 0x40) != 0)
            q.gray = true;
        if ((n & 0x80) != 0)
            q.multipage = true;
        if ((n & 0x100) != 0)
            q.motion = true;
        if ((n & 0x200) != 0)
            q.exif = true;
        return q;
    }

    public static FmtInfo GetFmtInfo(IMAGEFORMAT fmt)
    {
        IntPtr p = ImageLib_GetFmtInfo(fmt);
        if (p == IntPtr.Zero)
            return new FmtInfo();
        return ToFmtInfo(p);
    }
    
    public static FmtInfo[] GetFmtInfo()
    {
        IntPtr ptr = ImageLib_FmtInfo();
        List<FmtInfo> lst = new List<FmtInfo>();
        while (true)
        {
            if (Marshal.ReadInt32(ptr) == (int)IMAGEFORMAT.IMAGEFORMAT_UNKNOWN)
                break;
            lst.Add(ToFmtInfo(ptr));
            ptr = IncIntPtr(ptr, 4 * 3 + IntPtr.Size * 3);
        }
        return lst.ToArray();
    }

    private static ExtFmt ToExtFmt(IntPtr p)
    {
        ExtFmt q = new ExtFmt();
        q.ext = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p));
        p = IncIntPtr(p, IntPtr.Size);
        q.fmt = (IMAGEFORMAT)Marshal.ReadInt32(p);
        return q;
    }
    
    public static ExtFmt[] GetExtFmt()
    {
        IntPtr ptr = ImageLib_ExtFmt();
        List<ExtFmt> lst = new List<ExtFmt>();
        while (true)
        {
            if (Marshal.ReadIntPtr(ptr) == IntPtr.Zero)
                break;
            lst.Add(ToExtFmt(ptr));
            ptr = IncIntPtr(ptr, IntPtr.Size + 4);
        }
        return lst.ToArray();
    }
    
    private static FmtCodec ToFmtCodec(IntPtr p)
    {
        FmtCodec q = new FmtCodec();
        q.fmt = (IMAGEFORMAT)Marshal.ReadInt32(p);
        p = IncIntPtr(p, 4);
        q.code = Marshal.ReadByte(p);
        p = IncIntPtr(p, 1);
        q.decode = Marshal.ReadByte(p);
        p = IncIntPtr(p, 3);
        q.name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p));
        p = IncIntPtr(p, IntPtr.Size);
        q.codec = (uint)Marshal.ReadInt32(p);
        return q;
    }
    
    public static FmtCodec[] GetFmtCodec()
    {
        IntPtr ptr = ImageLib_Codec();
        List<FmtCodec> lst = new List<FmtCodec>();
        while (true)
        {
            if (Marshal.ReadInt32(ptr) == (int)IMAGEFORMAT.IMAGEFORMAT_UNKNOWN)
                break;
            lst.Add(ToFmtCodec(ptr));
            ptr = IncIntPtr(ptr, 4 * 3 + IntPtr.Size);
        }
        return lst.ToArray();
    }
    
    public static bool SupportExif(IMAGEFORMAT iType)
    {
        return ImageLib_SupportExif(iType);
    }
}
