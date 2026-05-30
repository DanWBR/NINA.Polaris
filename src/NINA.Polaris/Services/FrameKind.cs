namespace NINA.Polaris.Services;

/// <summary>
/// Tags a frame broadcast over <c>/ws/image-stream</c> with the panel
/// it belongs to, so the browser can route it to that panel's canvas
/// only instead of fanning every frame out to every visible canvas.
///
/// <para>Wire-encoded as a single <see cref="int"/> at offset 20 of
/// the stream header. <see cref="Live"/> is the legacy default (kind=0)
/// and is the only kind that feeds the WASM live-stack accumulator.</para>
///
/// <para>The enum int values are part of the on-wire protocol; do NOT
/// renumber. Add new kinds at the end.</para>
/// </summary>
public enum FrameKind {
    /// <summary>LIVE tab capture or sequence-engine frame. Goes to
    /// liveCanvas + feeds the running mean stacker.</summary>
    Live = 0,
    /// <summary>PREVIEW tab one-off snap. Goes to previewCanvas only.</summary>
    Preview = 1,
    /// <summary>FOCUS tab manual + V-curve auto-focus exposures.
    /// Goes to focusCanvas / manualFocusCanvas.</summary>
    Focus = 2,
    /// <summary>VIDEO tab planetary stream + recording frames.
    /// Goes to videoCaptureCanvas only.</summary>
    Video = 3,
    /// <summary>SKY-tab inset slew preview (background capture loop
    /// auto-fired while the mount is slewing). Goes to
    /// slewPreviewCanvas only.</summary>
    SlewPreview = 4
}
