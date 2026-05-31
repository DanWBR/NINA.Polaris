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

    /// <summary>Optional-feature flags. Drives whether the UI shows
    /// the "Edit filter names" controls -- ASCOM doesn't expose the
    /// names through the driver surface (the host app has to manage
    /// them externally), but INDI's <c>FILTER_NAME</c> text vector
    /// is writable.</summary>
    FilterWheelCapabilities Capabilities => FilterWheelCapabilities.Default;

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task SetPositionAsync(int position, CancellationToken ct = default);
    Task SetFilterByNameAsync(string filterName, CancellationToken ct = default);

    /// <summary>Push a new set of filter names INTO the driver so
    /// they persist across reconnects -- INDI <c>FILTER_NAME</c>
    /// text vector. The array length must match
    /// <see cref="FilterCount"/>. Throws by default; backends opt
    /// in via <see cref="Capabilities"/><c>.SupportsEditNames</c>.</summary>
    Task SetFilterNamesAsync(string[] names, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Editing filter names is not supported by this driver");
}

/// <summary>Optional-feature flags for <see cref="IFilterWheel"/>.</summary>
public record FilterWheelCapabilities(bool SupportsEditNames = false) {
    public static readonly FilterWheelCapabilities Default = new();
}
