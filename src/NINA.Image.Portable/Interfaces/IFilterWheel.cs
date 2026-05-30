namespace NINA.Image.Interfaces;

/// <summary>
/// Common contract every filter-wheel backend honours so the sequencer,
/// flat-wizard, capture endpoint, and PHD2 dither hook stay backend-
/// agnostic. Implementations: <c>IndiFilterWheel</c> (any INDI EFW),
/// <c>AscomComFilterWheel</c> (direct COM on Windows for ZWO EFW,
/// QHY CFW, Pegasus FilterMaster, etc.).
///
/// <para>Positions are zero-indexed, names are arbitrary strings (the
/// ASCOM Platform spec leaves naming to the driver / user). Filter
/// changes are asynchronous, callers should await the SetPositionAsync
/// call and treat IsMoving as a defensive check before the next
/// exposure.</para>
/// </summary>
public interface IFilterWheel {
    string DeviceName { get; }
    bool IsConnected { get; }
    int Position { get; }
    bool IsMoving { get; }
    string[] FilterNames { get; }
    int FilterCount { get; }
    string CurrentFilterName { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task SetPositionAsync(int position, CancellationToken ct = default);
    Task SetFilterByNameAsync(string filterName, CancellationToken ct = default);
}
