namespace NINA.Camera.SonySdk;

/// <summary>
/// SDK lifetime + availability probe for Sony α-series support.
/// Same role as <c>CanonEdsdkRegistry</c> for Canon.
///
/// <para>
/// Status: <b>skeleton driver</b>. The Sony stack has two
/// complementary paths covering different camera generations —
/// see <c>docs/dslr-windows-sony.md</c> for the full discussion:
/// </para>
///
/// <list type="bullet">
/// <item><description>
/// <b>Wi-Fi REST</b> (Camera Remote API v1.90) — covers older
/// bodies (α7 / α7R / α7S originals, α6000-series, NEX, QX
/// lens cameras). Pure HTTP / JSON, no native binaries, no
/// EULA, fully cross-platform. Reference implementation:
/// <a href="https://github.com/nantcom/SonyCameraSDK">nantcom/SonyCameraSDK</a>
/// (MS-PL). Easiest path to a working driver.
/// </description></item>
/// <item><description>
/// <b>USB SCRSDK v2.x</b> — covers current bodies (α7 III+, α1,
/// α9 II, FX3, α6700). C-style native API, ships binaries for
/// Windows + Linux (including arm64) so this driver shows up on
/// Raspberry Pi too. Get the SDK from
/// <a href="https://developer.sony.com/imaging-products/camera-remote-sdk/">developer.sony.com</a>.
/// </description></item>
/// </list>
///
/// <para>
/// Both can co-exist in this project — the camera implementation
/// can detect by IP / USB enumeration which path applies. Once
/// either lands, flip <see cref="IsAvailable"/> to return true
/// when the binding succeeds.
/// </para>
/// </summary>
public static class SonySdkRegistry {

    /// <summary>Currently returns false unconditionally — the
    /// integration is a skeleton. The UI surfaces this as "(not
    /// installed)" with a link to <c>docs/dslr-windows-sony.md</c>.</summary>
    public static bool IsAvailable => false;

    public static void EnsureInitialized() {
        throw new NotImplementedException(
            "Sony Camera Remote SDK integration is not implemented yet. " +
            "See docs/dslr-windows-sony.md for the open work.");
    }
}
