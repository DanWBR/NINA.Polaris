using System.Runtime.InteropServices;

namespace NINA.Camera.CanonEdsdk.Native;

/// <summary>
/// P/Invoke surface for the Canon EDSDK (<c>EDSDK.dll</c>). Function
/// names, argument order and pointer types mirror the upstream C
/// headers so the official Canon documentation translates directly.
///
/// <para>
/// The native DLLs are <b>not</b> distributed in this repo. Canon's
/// EDSDK EULA requires each developer to register on the Canon
/// imaging-developer site, accept the licence, and download the
/// matching version. Drop <c>EDSDK.dll</c> + <c>EdsImage.dll</c> +
/// the helper DLLs next to the Polaris executable (or into
/// <c>plugins/canon-edsdk/</c>) and Windows will resolve them via
/// the standard DLL search path.
/// </para>
///
/// <para>
/// .NET 10's <see cref="LibraryImportAttribute"/> source generator
/// produces the marshalling code at compile time rather than via
/// runtime reflection, faster on startup, AOT-friendly, and the
/// preferred replacement for <see cref="DllImportAttribute"/>.
/// </para>
/// </summary>
public static partial class EdsdkNative {
    private const string DLL = "EDSDK.dll";

    // ---- Lifecycle ------------------------------------------------
    [LibraryImport(DLL)]
    public static partial uint EdsInitializeSDK();

    [LibraryImport(DLL)]
    public static partial uint EdsTerminateSDK();

    [LibraryImport(DLL)]
    public static partial uint EdsRelease(IntPtr inRef);

    // ---- Camera enumeration --------------------------------------
    [LibraryImport(DLL)]
    public static partial uint EdsGetCameraList(out IntPtr outCameraListRef);

    [LibraryImport(DLL)]
    public static partial uint EdsGetChildCount(IntPtr inRef, out int outCount);

    [LibraryImport(DLL)]
    public static partial uint EdsGetChildAtIndex(IntPtr inRef, int inIndex, out IntPtr outRef);

    // Struct contains inline ByValTStr fields, which the LibraryImport
    // source generator can't auto-marshal. The runtime DllImport
    // marshaller handles it fine, so this one function falls back.
    [DllImport(DLL, CharSet = CharSet.Ansi)]
    public static extern uint EdsGetDeviceInfo(IntPtr inCameraRef, out EdsDeviceInfo outDeviceInfo);

    // ---- Session ---------------------------------------------------
    [LibraryImport(DLL)]
    public static partial uint EdsOpenSession(IntPtr inCameraRef);

    [LibraryImport(DLL)]
    public static partial uint EdsCloseSession(IntPtr inCameraRef);

    // ---- Properties ------------------------------------------------
    // Variant that ships a uint payload (the common case, Tv, ISO,
    // SaveTo, and most camera-state values are uint32-sized).
    [LibraryImport(DLL)]
    public static partial uint EdsSetPropertyData(IntPtr inRef, uint inPropertyID,
        uint inParam, int inPropertySize, ref uint inPropertyData);

    [LibraryImport(DLL)]
    public static partial uint EdsGetPropertyData(IntPtr inRef, uint inPropertyID,
        uint inParam, int inPropertySize, out uint outPropertyData);

    [LibraryImport(DLL)]
    public static partial uint EdsGetPropertySize(IntPtr inRef, uint inPropertyID,
        uint inParam, out uint outDataType, out int outSize);

    // SaveTo capacity workaround, required before EdsCameraCommand_TakePicture
    // with SaveTo=Host, otherwise the camera believes its destination is
    // full and refuses to shoot.
    [LibraryImport(DLL)]
    public static partial uint EdsSetCapacity(IntPtr inCameraRef, EdsCapacity inCapacity);

    // ---- Commands --------------------------------------------------
    [LibraryImport(DLL)]
    public static partial uint EdsSendCommand(IntPtr inCameraRef, uint inCommand, int inParam);

    // ---- Event handlers --------------------------------------------
    // Camera events emit on a thread the SDK manages, marshal back to
    // the managed driver via the handlers we register here.
    public delegate uint EdsObjectEventHandler(uint inEvent, IntPtr inRef, IntPtr inContext);
    public delegate uint EdsPropertyEventHandler(uint inEvent, uint inPropertyID, uint inParam, IntPtr inContext);
    public delegate uint EdsStateEventHandler(uint inEvent, uint inParameter, IntPtr inContext);

    [LibraryImport(DLL)]
    public static partial uint EdsSetObjectEventHandler(IntPtr inCameraRef, uint inEvent,
        EdsObjectEventHandler inObjectEventHandler, IntPtr inContext);

    [LibraryImport(DLL)]
    public static partial uint EdsSetPropertyEventHandler(IntPtr inCameraRef, uint inEvent,
        EdsPropertyEventHandler inPropertyEventHandler, IntPtr inContext);

    [LibraryImport(DLL)]
    public static partial uint EdsSetCameraStateEventHandler(IntPtr inCameraRef, uint inEvent,
        EdsStateEventHandler inStateEventHandler, IntPtr inContext);

    [LibraryImport(DLL)]
    public static partial uint EdsGetEvent();

    // ---- Item transfer (file download) ----------------------------
    [DllImport(DLL, CharSet = CharSet.Ansi)]
    public static extern uint EdsGetDirectoryItemInfo(IntPtr inDirItemRef,
        out EdsDirectoryItemInfo outDirItemInfo);

    [LibraryImport(DLL)]
    public static partial uint EdsCreateMemoryStream(ulong inBufferSize, out IntPtr outStream);

    [LibraryImport(DLL)]
    public static partial uint EdsDownload(IntPtr inDirItemRef, ulong inReadSize, IntPtr outStream);

    [LibraryImport(DLL)]
    public static partial uint EdsDownloadComplete(IntPtr inDirItemRef);

    [LibraryImport(DLL)]
    public static partial uint EdsGetPointer(IntPtr inStreamRef, out IntPtr outPointer);

    [LibraryImport(DLL)]
    public static partial uint EdsGetLength(IntPtr inStreamRef, out ulong outLength);
}

/// <summary>Payload for <see cref="EdsdkNative.EdsSetCapacity"/>. Pretending
/// the host has effectively-infinite storage lets the camera proceed
/// even though we're streaming the file directly off-card.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EdsCapacity {
    public int NumberOfFreeClusters;
    public int BytesPerSector;
    public int Reset;

    public static EdsCapacity Effectively_Unlimited => new() {
        NumberOfFreeClusters = 0x7FFFFFFB,
        BytesPerSector = 0x1000,
        Reset = 1
    };
}
