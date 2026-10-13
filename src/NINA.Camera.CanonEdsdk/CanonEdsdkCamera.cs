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
/// Threading: every SDK call runs on the one EDSDK thread
/// (<see cref="CanonEdsdkRegistry"/>), which also pumps EdsGetEvent; the
/// object events, and the downloads in them, run there too, and resolve a
/// per-capture <c>TaskCompletionSource</c> the caller awaits.
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
        CanonEdsdkRegistry.EnsureInitialized();
        return CanonEdsdkRegistry.Invoke(ConnectOnSdkThread);
    }


    private void ConnectOnSdkThread() {
        if (IsConnected) return;

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
        // kEdsStateEvent_All (0x300), as EDSDKTypes.h defines it. 0xFFFFFFFF
        // here never returned with an EOS R8: the connect hung inside this call.
        EdsdkNative.EdsSetCameraStateEventHandler(_camRef,
            EdsdkConstants.kEdsStateEvent_All, _stateHandler, IntPtr.Zero);

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
    }

    public Task DisconnectAsync(CancellationToken ct = default) {
        if (!IsConnected) return Task.CompletedTask;
        return CanonEdsdkRegistry.Invoke(DisconnectOnSdkThread);
    }

    private void DisconnectOnSdkThread() {
        if (!IsConnected) return;
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
        return CanonEdsdkRegistry.Invoke(() => {
            var code = EdsdkConstants.IsoCodeFor(iso);
            var err = EdsdkNative.EdsSetPropertyData(_camRef,
                EdsdkConstants.kEdsPropID_ISOSpeed, 0, sizeof(uint), ref code);
            if (err == EdsdkConstants.EDS_ERR_OK) {
                SelectedIso = EdsdkConstants.IsoFromCode(code);
            }
        });
    }

    public Task AbortExposureAsync(CancellationToken ct = default) {
        if (!IsConnected) return Task.CompletedTask;
        _pending?.Completion.TrySetCanceled();
        // A bulb exposure holds the shutter: release it now.
        return CanonEdsdkRegistry.Invoke(() => EdsdkNative.EdsSendCommand(_camRef,
            EdsdkConstants.kEdsCameraCommand_PressShutterButton,
            EdsdkConstants.kEdsCameraCommand_ShutterButton_OFF));
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

        var pending = new CaptureContext(exposureSeconds, opts);
        _pending = pending;
        using var reg = ct.Register(() => pending.Completion.TrySetCanceled());
        State = CameraStates.Exposing;

        try {
            // The shutter goes through PressShutterButton, fully pressed without
            // autofocus and then released, the way Canon's own samples shoot.
            // TakePicture is the old DSLR command, and newer bodies (the R
            // series) answer it with an error or a focus attempt.
            if (exposureSeconds <= 30.0) {
                // Closest Tv, then press and release; the camera exposes for
                // Tv and then raises DirItemRequestTransfer for each file,
                // which the EDSDK thread picks up while pumping events.
                var err = await CanonEdsdkRegistry.Invoke(() => {
                    var tvCode = EdsdkConstants.TvCodeFor(exposureSeconds);
                    EdsdkNative.EdsSetPropertyData(_camRef, EdsdkConstants.kEdsPropID_Tv,
                        0, sizeof(uint), ref tvCode);
                    return PressAndRelease();
                });
                if (err != EdsdkConstants.EDS_ERR_OK) {
                    State = CameraStates.Error;
                    throw new InvalidOperationException($"The camera refused to shoot (0x{err:X8}).");
                }
            } else {
                // Longer than 30 s needs Bulb, which EDSDK cannot select: the
                // camera has to be on B. Hold the shutter for the exposure,
                // then release; the files come after the release.
                var err = await CanonEdsdkRegistry.Invoke(() => EdsdkNative.EdsSendCommand(_camRef,
                    EdsdkConstants.kEdsCameraCommand_PressShutterButton,
                    EdsdkConstants.kEdsCameraCommand_ShutterButton_Completely_NonAF));
                if (err != EdsdkConstants.EDS_ERR_OK) {
                    State = CameraStates.Error;
                    throw new InvalidOperationException(
                        $"The camera refused to start a {exposureSeconds:0}s exposure (0x{err:X8}). " +
                        "Exposures longer than 30 s need the camera's mode dial on B.");
                }
                try {
                    await Task.Delay(TimeSpan.FromSeconds(exposureSeconds), ct);
                } finally {
                    await CanonEdsdkRegistry.Invoke(() => EdsdkNative.EdsSendCommand(_camRef,
                        EdsdkConstants.kEdsCameraCommand_PressShutterButton,
                        EdsdkConstants.kEdsCameraCommand_ShutterButton_OFF));
                }
            }

            // A camera that shoots but never sends the file (saving to the
            // card only, a full card) must not hold the request forever.
            var limit = TimeSpan.FromSeconds(Math.Max(0, exposureSeconds) + 60);
            try {
                return await pending.Completion.Task.WaitAsync(limit, ct);
            } catch (TimeoutException) {
                throw new TimeoutException(
                    $"The camera took the picture but did not send it to Polaris within " +
                    $"{limit.TotalSeconds:F0} s. Check that it is not set to save to the card only.");
            }
        } finally {
            _pending = null;
            if (State != CameraStates.Error) State = CameraStates.Idle;
        }
    }

    /// <summary>Press the shutter fully without autofocus, then release it;
    /// on the EDSDK thread. Released even when the press failed, as Canon's
    /// sample does, so the camera is never left with the button held.</summary>
    private uint PressAndRelease() {
        var err = EdsdkNative.EdsSendCommand(_camRef,
            EdsdkConstants.kEdsCameraCommand_PressShutterButton,
            EdsdkConstants.kEdsCameraCommand_ShutterButton_Completely_NonAF);
        var off = EdsdkNative.EdsSendCommand(_camRef,
            EdsdkConstants.kEdsCameraCommand_PressShutterButton,
            EdsdkConstants.kEdsCameraCommand_ShutterButton_OFF);
        return err != EdsdkConstants.EDS_ERR_OK ? err : off;
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

        // Done as soon as we have a JPEG (preview), the RAW is
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
        // The preview: the JPEG the camera sent, or the full-size JPEG inside
        // the RAW when it sent only that (CanonPreview). The RAW itself is
        // what reaches the disk, untouched, through IHasRawFile.
        ushort[] pixels = new ushort[1];
        int width = 1, height = 1;
        bool mosaic = false;
        SKBitmap? bmp = null;
        try {
            if (pending.JpegBytes != null) bmp = SKBitmap.Decode(pending.JpegBytes);
            if ((bmp == null || bmp.Width <= 0) && pending.RawBytes != null) {
                bmp?.Dispose();
                bmp = CanonPreview.LargestEmbeddedJpeg(pending.RawBytes);
            }
            if (bmp != null && bmp.Width > 0 && bmp.Height > 0) {
                width = bmp.Width;
                height = bmp.Height;
                pixels = CanonPreview.ToRggb(bmp);
                mosaic = true;
            }
        } finally {
            bmp?.Dispose();
        }

        var props = new ImageProperties {
            Width = width, Height = height, BitDepth = 16,
            // 8-bit JPEG samples spread over 16 bits: say so, as the INDI
            // DSLR path does, so nothing reads them as 16-bit data.
            SignificantBitDepth = mosaic ? 8 : 0,
            IsBayered = mosaic,
            BayerPattern = mosaic ? NINA.Core.Enum.BayerPatternEnum.RGGB : NINA.Core.Enum.BayerPatternEnum.None
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