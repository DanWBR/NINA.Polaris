using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NINA.Camera.CanonEdsdk.Native;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.FileFormat.FITS;
using NINA.Image.Interfaces;
using SkiaSharp;

namespace NINA.Camera.CanonEdsdk;

/// <summary>
/// <see cref="ICamera"/> implementation backed by the Canon EDSDK
/// (Windows-only). Captures are configured as RAW + JPEG so each
/// shutter trigger produces two assets that arrive in sequence on
/// the object-event handler: the camera-native CR2 (saved to disk
/// verbatim via the <see cref="IHasRawFile"/> hook) and the embedded
/// JPEG (decoded to a luminance plane for the live preview).
///
/// Threading: the EDSDK delivers events on its own thread. The
/// object-event handler resolves a per-capture
/// <c>TaskCompletionSource</c> back to whatever thread the
/// <see cref="CaptureAsync"/> call originated on.
///
/// Lifetime: the camera handle is opened in <see cref="ConnectAsync"/>
/// and released in <see cref="DisconnectAsync"/>. Always pair them.
/// </summary>
[SupportedOSPlatform("windows")]
public class CanonEdsdkCamera : ICamera {
    private readonly string _deviceId;
    private IntPtr _camRef;
    private bool _sessionOpen;
    private CaptureContext? _pending;

    // Hold delegates as fields so the GC doesn't collect them while
    // the native side still holds the callback pointer.
    private EdsdkNative.EdsObjectEventHandler? _objectHandler;
    private EdsdkNative.EdsStateEventHandler?  _stateHandler;

    public string DeviceName { get; private set; } = "Canon EOS";
    public bool IsConnected => _camRef != IntPtr.Zero && _sessionOpen;

    public CameraStates State { get; private set; } = CameraStates.NoState;

    // DSLRs don't report sensor temperature, no cooler, no binning,
    // no programmable ROI. Most properties are intentionally NaN/0
    // so the status broadcaster can hide the corresponding UI rows.
    public double Temperature   => double.NaN;
    public bool   CoolerOn      => false;
    public double CoolerPower   => 0;
    public int    BinX          => 1;
    public int    BinY          => 1;
    public int    BitDepth      => 14;   // typical Canon EOS sensor
    public int    MaxX          => 0;    // populated from JPEG preview when known
    public int    MaxY          => 0;
    public double PixelSizeX    => 0;
    public double PixelSizeY    => 0;
    public int    Gain          => SelectedIso;   // ISO is the DSLR equivalent

    public IReadOnlyList<int> IsoOptions { get; }
        = EdsdkConstants.IsoTable.Select(e => e.Iso).ToList();

    public int SelectedIso { get; private set; } = 800;

    public CameraCapabilities Capabilities => CameraCapabilities.Dslr;

    public CanonEdsdkCamera(string deviceId) {
        _deviceId = deviceId;
    }

    // ---- Connect / disconnect ------------------------------------

    public Task ConnectAsync(CancellationToken ct = default) {
        if (IsConnected) return Task.CompletedTask;

        _camRef = CanonEdsdkDiscovery.OpenCameraRefById(_deviceId);
        if (_camRef == IntPtr.Zero) {
            throw new InvalidOperationException(
                $"Canon camera '{_deviceId}' is no longer connected. " +
                "Unplug and re-plug the USB cable and try again.");
        }

        var err = EdsdkNative.EdsOpenSession(_camRef);
        if (err != EdsdkConstants.EDS_ERR_OK) {
            EdsdkNative.EdsRelease(_camRef);
            _camRef = IntPtr.Zero;
            throw new InvalidOperationException(
                $"EdsOpenSession failed (0x{err:X8}). Is another app " +
                "(Canon EOS Utility, DigiCamControl, BackyardEOS) " +
                "currently tethered to this camera?");
        }

        _sessionOpen = true;

        // Read identity for the status broadcaster.
        if (EdsdkNative.EdsGetDeviceInfo(_camRef, out var info) == EdsdkConstants.EDS_ERR_OK) {
            DeviceName = info.szDeviceDescription;
        }

        // Register event handlers. Cache as fields so the GC keeps
        // the delegates alive (Marshal.GetFunctionPointerForDelegate
        // doesn't take a reference).
        _objectHandler = OnObjectEvent;
        _stateHandler  = OnStateEvent;
        EdsdkNative.EdsSetObjectEventHandler(_camRef,
            EdsdkConstants.kEdsObjectEvent_All, _objectHandler, IntPtr.Zero);
        EdsdkNative.EdsSetCameraStateEventHandler(_camRef,
            0xFFFFFFFF, _stateHandler, IntPtr.Zero);

        // Tell the camera to deliver captures to the host instead of
        // its SD card, and fake the host-capacity probe so it doesn't
        // refuse to shoot ("destination full").
        uint saveTo = EdsdkConstants.kEdsSaveTo_Host;
        EdsdkNative.EdsSetPropertyData(_camRef, EdsdkConstants.kEdsPropID_SaveTo,
            0, sizeof(uint), ref saveTo);
        EdsdkNative.EdsSetCapacity(_camRef, EdsCapacity.Effectively_Unlimited);

        // Read current ISO so the UI starts in sync with the camera.
        if (EdsdkNative.EdsGetPropertyData(_camRef, EdsdkConstants.kEdsPropID_ISOSpeed,
                0, sizeof(uint), out var isoCode) == EdsdkConstants.EDS_ERR_OK) {
            SelectedIso = EdsdkConstants.IsoFromCode(isoCode);
        }

        State = CameraStates.Idle;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default) {
        if (!IsConnected) return Task.CompletedTask;
        try {
            EdsdkNative.EdsCloseSession(_camRef);
        } finally {
            EdsdkNative.EdsRelease(_camRef);
            _camRef = IntPtr.Zero;
            _sessionOpen = false;
            _objectHandler = null;
            _stateHandler = null;
            State = CameraStates.NoState;
        }
        return Task.CompletedTask;
    }

    // ---- Setters --------------------------------------------------

    /// <summary>DSLRs don't bin in-camera. Silent no-op so the
    /// sequencer's SetBinning call doesn't blow up.</summary>
    public Task SetBinningAsync(int binX, int binY, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetTemperatureAsync(double temperature, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetCoolerAsync(bool on, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetIsoAsync(int iso, CancellationToken ct = default) {
        if (!IsConnected) return Task.CompletedTask;
        var code = EdsdkConstants.IsoCodeFor(iso);
        var err = EdsdkNative.EdsSetPropertyData(_camRef,
            EdsdkConstants.kEdsPropID_ISOSpeed, 0, sizeof(uint), ref code);
        if (err == EdsdkConstants.EDS_ERR_OK) {
            SelectedIso = EdsdkConstants.IsoFromCode(code);
        }
        return Task.CompletedTask;
    }

    public Task AbortExposureAsync(CancellationToken ct = default) {
        if (!IsConnected) return Task.CompletedTask;
        // For bulb exposures, end the bulb immediately.
        EdsdkNative.EdsSendCommand(_camRef,
            EdsdkConstants.kEdsCameraCommand_BulbEnd, 0);
        _pending?.Completion.TrySetCanceled();
        return Task.CompletedTask;
    }

    // ---- Capture --------------------------------------------------

    public async Task<IImageData> CaptureAsync(double exposureSeconds,
            CaptureOptions? opts = null, CancellationToken ct = default) {
        if (!IsConnected)
            throw new InvalidOperationException("Camera not connected.");
        if (_pending != null)
            throw new InvalidOperationException("Another capture is already in flight.");

        // Honour per-capture ISO override.
        if (opts?.Iso is int iso) await SetIsoAsync(iso, ct);

        // Shutter speed: either pick the closest Tv enum or use Bulb.
        if (exposureSeconds <= 30.0) {
            var tvCode = EdsdkConstants.TvCodeFor(exposureSeconds);
            EdsdkNative.EdsSetPropertyData(_camRef, EdsdkConstants.kEdsPropID_Tv,
                0, sizeof(uint), ref tvCode);
        } else {
            // Bulb path also needs Tv set to the bulb code (0x0C) so
            // the camera accepts the BulbStart command afterwards.
            uint bulbCode = 0x0Cu;
            EdsdkNative.EdsSetPropertyData(_camRef, EdsdkConstants.kEdsPropID_Tv,
                0, sizeof(uint), ref bulbCode);
        }

        _pending = new CaptureContext(exposureSeconds, opts);
        using var reg = ct.Register(() => _pending?.Completion.TrySetCanceled());

        State = CameraStates.Exposing;

        if (exposureSeconds <= 30.0) {
            // Plain shutter trigger; camera waits exposureSeconds then
            // delivers DirItemRequestTransfer events for each asset.
            var err = EdsdkNative.EdsSendCommand(_camRef,
                EdsdkConstants.kEdsCameraCommand_TakePicture, 0);
            if (err != EdsdkConstants.EDS_ERR_OK) {
                _pending = null;
                State = CameraStates.Error;
                throw new InvalidOperationException(
                    $"TakePicture failed (0x{err:X8}).");
            }
        } else {
            // Bulb: BulbStart → sleep → BulbEnd. The camera dumps the
            // CR2 + JPEG after BulbEnd.
            EdsdkNative.EdsSendCommand(_camRef,
                EdsdkConstants.kEdsCameraCommand_BulbStart, 0);
            try {
                await Task.Delay(TimeSpan.FromSeconds(exposureSeconds), ct);
            } finally {
                EdsdkNative.EdsSendCommand(_camRef,
                    EdsdkConstants.kEdsCameraCommand_BulbEnd, 0);
            }
        }

        try {
            return await _pending.Completion.Task.WaitAsync(ct);
        } finally {
            _pending = null;
            State = CameraStates.Idle;
        }
    }

    // ---- Event handlers ------------------------------------------

    /// <summary>Called by EDSDK on its own thread when a captured
    /// file is ready to transfer. We download it into a managed
    /// buffer, attach it to the in-flight capture, and complete the
    /// awaiting Task once both raw and JPEG have arrived.</summary>
    private uint OnObjectEvent(uint inEvent, IntPtr inRef, IntPtr _) {
        if (inEvent != EdsdkConstants.kEdsObjectEvent_DirItemRequestTransfer) {
            if (inRef != IntPtr.Zero) EdsdkNative.EdsRelease(inRef);
            return EdsdkConstants.EDS_ERR_OK;
        }

        try {
            if (EdsdkNative.EdsGetDirectoryItemInfo(inRef, out var item) != EdsdkConstants.EDS_ERR_OK) {
                return EdsdkConstants.EDS_ERR_OK;
            }

            // Download into an in-memory stream the SDK manages.
            if (EdsdkNative.EdsCreateMemoryStream(item.Size, out var stream) != EdsdkConstants.EDS_ERR_OK) {
                return EdsdkConstants.EDS_ERR_OK;
            }
            try {
                if (EdsdkNative.EdsDownload(inRef, item.Size, stream) != EdsdkConstants.EDS_ERR_OK)
                    return EdsdkConstants.EDS_ERR_OK;
                EdsdkNative.EdsDownloadComplete(inRef);

                // Copy the bytes out of the SDK stream into a managed
                // array so we can let go of the native handle.
                EdsdkNative.EdsGetLength(stream, out var length);
                EdsdkNative.EdsGetPointer(stream, out var ptr);
                var bytes = new byte[(int)length];
                Marshal.Copy(ptr, bytes, 0, bytes.Length);

                AttachAsset(item.szFileName, bytes);
            } finally {
                EdsdkNative.EdsRelease(stream);
            }
        } finally {
            if (inRef != IntPtr.Zero) EdsdkNative.EdsRelease(inRef);
        }
        return EdsdkConstants.EDS_ERR_OK;
    }

    private uint OnStateEvent(uint inEvent, uint inParameter, IntPtr _)
        => EdsdkConstants.EDS_ERR_OK;

    /// <summary>Slot a downloaded asset into the pending capture. CR2
    /// goes to <see cref="IHasRawFile"/>; JPEG is decoded to a
    /// luminance plane for the live preview. When both have arrived
    /// (or after a short grace window for JPEG-only mode) we resolve
    /// the awaiting Task.</summary>
    private void AttachAsset(string fileName, byte[] bytes) {
        var pending = _pending;
        if (pending == null) return;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        bool isJpeg = ext is ".jpg" or ".jpeg";
        bool isRaw  = ext is ".cr2" or ".cr3";

        if (isJpeg) pending.JpegBytes = bytes;
        else if (isRaw) {
            pending.RawBytes = bytes;
            pending.RawExtension = ext;
        }

        // Done as soon as we have a JPEG (preview) — the RAW is
        // attached if it arrived first or arrives later in the same
        // sequence. Cameras configured for JPEG-only will never send
        // a CR2 and we proceed with just the preview.
        if (pending.JpegBytes != null) {
            pending.Completion.TrySetResult(BuildImageData(pending));
        } else if (pending.RawBytes != null && pending.RawDeadline == null) {
            // Camera in RAW-only mode: arm a tiny deadline so we don't
            // block forever waiting for a JPEG that's not coming.
            pending.RawDeadline = DateTime.UtcNow.AddMilliseconds(250);
            _ = Task.Run(async () => {
                await Task.Delay(300);
                if (pending.JpegBytes == null) {
                    pending.Completion.TrySetResult(BuildImageData(pending));
                }
            });
        }
    }

    private static IImageData BuildImageData(CaptureContext pending) {
        // Decode the JPEG (or, in RAW-only mode, fall back to a tiny
        // placeholder) to satisfy IImageData.Data — the rest of the
        // Polaris pipeline (live stack, stats, relay) only sees the
        // luminance plane. The CR2 is what users actually want on
        // disk; the JPEG is just for the on-screen preview.
        ushort[] pixels;
        int width = 1, height = 1;
        if (pending.JpegBytes != null) {
            using var bmp = SKBitmap.Decode(pending.JpegBytes);
            if (bmp != null) {
                width = bmp.Width;
                height = bmp.Height;
                pixels = new ushort[width * height];
                // Rec.601 luminance from RGB. The cast bumps 8-bit
                // values into the 16-bit range so the stretch /
                // histogram pipeline downstream behaves identically
                // to FITS captures.
                for (int y = 0; y < height; y++) {
                    for (int x = 0; x < width; x++) {
                        var c = bmp.GetPixel(x, y);
                        var luma = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                        pixels[y * width + x] = (ushort)Math.Clamp(luma * 256, 0, 65535);
                    }
                }
            } else {
                pixels = new ushort[1];
            }
        } else {
            pixels = new ushort[1];
        }

        var props = new ImageProperties {
            Width = width, Height = height, BitDepth = 16,
            IsBayered = false,
            BayerPattern = NINA.Core.Enum.BayerPatternEnum.None
        };
        var meta = new ImageMetaData {
            CreationTime = DateTime.UtcNow,
            Camera = new ImageMetaData.CameraInfo {
                Gain = pending.Options?.Iso ?? 0
            },
            Exposure = new ImageMetaData.ExposureInfo {
                ExposureTime = pending.ExposureSeconds,
                ImageType = pending.Options?.ImageType ?? "LIGHT",
                Filter = pending.Options?.Filter ?? ""
            },
            Target = new ImageMetaData.TargetInfo {
                Name = pending.Options?.TargetName ?? ""
            }
        };
        return new BaseImageData(pixels, props, meta) {
            RawFileBytes     = pending.RawBytes,
            RawFileExtension = pending.RawExtension
        };
    }

    private sealed class CaptureContext {
        public double ExposureSeconds { get; }
        public CaptureOptions? Options { get; }
        public TaskCompletionSource<IImageData> Completion { get; } = new();
        public byte[]? RawBytes { get; set; }
        public string? RawExtension { get; set; }
        public byte[]? JpegBytes { get; set; }
        public DateTime? RawDeadline { get; set; }

        public CaptureContext(double seconds, CaptureOptions? opts) {
            ExposureSeconds = seconds;
            Options = opts;
        }
    }
}
