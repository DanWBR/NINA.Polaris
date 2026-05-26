namespace NINA.Camera.CanonEdsdk.Native;

/// <summary>
/// Constant identifiers from the Canon EDSDK headers. Names mirror the
/// upstream C symbols (kEdsPropID_*, kEdsCameraCommand_*, etc.) so the
/// official EDSDK documentation translates directly to this file.
///
/// Source: EDSDK 13.x/14.x, <c>EDSDKTypes.h</c>.
/// </summary>
public static class EdsdkConstants {

    // ---- Error codes ----------------------------------------------
    public const uint EDS_ERR_OK = 0x00000000;
    public const uint EDS_ERR_DEVICE_BUSY = 0x00000081;
    public const uint EDS_ERR_DEVICE_NOT_FOUND = 0x00000080;
    public const uint EDS_ERR_OBJECT_NOTREADY = 0x000000A5;

    // ---- Property IDs (kEdsPropID_*) ------------------------------
    public const uint kEdsPropID_ProductName     = 0x00000002;
    public const uint kEdsPropID_BodyIDEx        = 0x00000015;
    public const uint kEdsPropID_OwnerName       = 0x00000004;
    public const uint kEdsPropID_FirmwareVersion = 0x00000007;
    public const uint kEdsPropID_BatteryLevel    = 0x00000008;
    public const uint kEdsPropID_SaveTo          = 0x0000000B;
    public const uint kEdsPropID_AEMode          = 0x00000400;
    public const uint kEdsPropID_Tv              = 0x00000402;
    public const uint kEdsPropID_Av              = 0x00000403;
    public const uint kEdsPropID_ISOSpeed        = 0x00000401;
    public const uint kEdsPropID_ImageQuality    = 0x00000100;
    public const uint kEdsPropID_AvailableShots  = 0x0000080B;

    // ---- Camera commands (kEdsCameraCommand_*) --------------------
    public const uint kEdsCameraCommand_TakePicture            = 0x00000000;
    public const uint kEdsCameraCommand_ExtendShutDownTimer    = 0x00000001;
    public const uint kEdsCameraCommand_BulbStart              = 0x00000002;
    public const uint kEdsCameraCommand_BulbEnd                = 0x00000003;
    public const uint kEdsCameraCommand_DoEvfAf                = 0x00000102;
    public const uint kEdsCameraCommand_PressShutterButton     = 0x00000004;

    // PressShutterButton parameters
    public const int kEdsCameraCommand_ShutterButton_OFF        = 0x00000000;
    public const int kEdsCameraCommand_ShutterButton_Halfway    = 0x00000001;
    public const int kEdsCameraCommand_ShutterButton_Completely = 0x00000003;

    // ---- Object events (kEdsObjectEvent_*) ------------------------
    public const uint kEdsObjectEvent_All                       = 0x00000200;
    public const uint kEdsObjectEvent_DirItemRequestTransfer    = 0x00000208;
    public const uint kEdsObjectEvent_DirItemRemoved            = 0x00000209;

    // ---- SaveTo destinations --------------------------------------
    public const uint kEdsSaveTo_Camera = 1;
    public const uint kEdsSaveTo_Host   = 2;
    public const uint kEdsSaveTo_Both   = 3;

    // ---- Tv (shutter speed) enum values ---------------------------
    // Subset of the full table, covers Bulb, 1/8000..1/1 and the
    // common long exposures up to 30s. EDSDK exposes ~80 entries; we
    // ship the ones an astrophotography workflow actually needs.
    public static readonly (uint Code, double Seconds, string Label)[] TvTable = new[] {
        (0x0Cu, 0.0,      "Bulb"),         // Bulb mode, shutter open until BulbEnd
        (0x10u, 30.0,     "30\""),
        (0x13u, 25.0,     "25\""),
        (0x15u, 20.0,     "20\""),
        (0x18u, 15.0,     "15\""),
        (0x1Bu, 13.0,     "13\""),
        (0x1Du, 10.0,     "10\""),
        (0x20u, 8.0,      "8\""),
        (0x23u, 6.0,      "6\""),
        (0x25u, 5.0,      "5\""),
        (0x28u, 4.0,      "4\""),
        (0x2Bu, 3.2,      "3.2\""),
        (0x2Du, 2.5,      "2.5\""),
        (0x30u, 2.0,      "2\""),
        (0x33u, 1.6,      "1.6\""),
        (0x35u, 1.3,      "1.3\""),
        (0x38u, 1.0,      "1\""),
        (0x3Bu, 0.8,      "0.8\""),
        (0x3Du, 0.6,      "0.6\""),
        (0x40u, 0.5,      "0.5\""),
        (0x43u, 0.4,      "0.4\""),
        (0x45u, 0.3,      "0.3\""),
        (0x48u, 1.0/4,    "1/4"),
        (0x4Bu, 1.0/5,    "1/5"),
        (0x4Du, 1.0/6,    "1/6"),
        (0x50u, 1.0/8,    "1/8"),
        (0x53u, 1.0/10,   "1/10"),
        (0x55u, 1.0/13,   "1/13"),
        (0x58u, 1.0/15,   "1/15"),
        (0x5Bu, 1.0/20,   "1/20"),
        (0x5Du, 1.0/25,   "1/25"),
        (0x60u, 1.0/30,   "1/30"),
        (0x63u, 1.0/40,   "1/40"),
        (0x65u, 1.0/50,   "1/50"),
        (0x68u, 1.0/60,   "1/60"),
        (0x6Bu, 1.0/80,   "1/80"),
        (0x6Du, 1.0/100,  "1/100"),
        (0x70u, 1.0/125,  "1/125"),
        (0x73u, 1.0/160,  "1/160"),
        (0x75u, 1.0/200,  "1/200"),
        (0x78u, 1.0/250,  "1/250"),
        (0x7Bu, 1.0/320,  "1/320"),
        (0x7Du, 1.0/400,  "1/400"),
        (0x80u, 1.0/500,  "1/500"),
        (0x83u, 1.0/640,  "1/640"),
        (0x85u, 1.0/800,  "1/800"),
        (0x88u, 1.0/1000, "1/1000"),
        (0x8Bu, 1.0/1250, "1/1250"),
        (0x8Du, 1.0/1600, "1/1600"),
        (0x90u, 1.0/2000, "1/2000"),
        (0x93u, 1.0/2500, "1/2500"),
        (0x95u, 1.0/3200, "1/3200"),
        (0x98u, 1.0/4000, "1/4000"),
        (0x9Bu, 1.0/5000, "1/5000"),
        (0x9Du, 1.0/6400, "1/6400"),
        (0xA0u, 1.0/8000, "1/8000"),
    };

    /// <summary>Find the closest Tv enum entry to <paramref name="seconds"/>.
    /// Exposures longer than the longest non-Bulb entry (30s) fall back to
    /// the Bulb code (0x0C), the caller has to drive BulbStart/BulbEnd
    /// itself.</summary>
    public static uint TvCodeFor(double seconds) {
        if (seconds > 30.0) return 0x0Cu;  // Bulb
        var best = TvTable[1];
        var bestDelta = Math.Abs(best.Seconds - seconds);
        for (int i = 1; i < TvTable.Length; i++) {
            var d = Math.Abs(TvTable[i].Seconds - seconds);
            if (d < bestDelta) { bestDelta = d; best = TvTable[i]; }
        }
        return best.Code;
    }

    // ---- ISO speed enum -------------------------------------------
    // Same approach as Tv, only the values DSLRs typically expose for
    // astrophotography. The "Auto" entry (0x00) is intentionally omitted;
    // automatic ISO doesn't make sense for predictable stacking.
    public static readonly (uint Code, int Iso)[] IsoTable = new[] {
        (0x28u,    50),
        (0x40u,   100),
        (0x48u,   200),
        (0x50u,   400),
        (0x58u,   800),
        (0x60u,  1600),
        (0x68u,  3200),
        (0x70u,  6400),
        (0x78u, 12800),
        (0x80u, 25600),
        (0x88u, 51200),
        (0x90u,102400),
    };

    public static uint IsoCodeFor(int iso) {
        var best = IsoTable[1];   // ISO 100 default
        var bestDelta = Math.Abs(best.Iso - iso);
        foreach (var e in IsoTable) {
            var d = Math.Abs(e.Iso - iso);
            if (d < bestDelta) { bestDelta = d; best = e; }
        }
        return best.Code;
    }

    public static int IsoFromCode(uint code) {
        foreach (var e in IsoTable) {
            if (e.Code == code) return e.Iso;
        }
        return 0;
    }
}
