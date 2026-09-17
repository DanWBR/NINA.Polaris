namespace NINA.Image.Interfaces;

/// <summary>Common surface for mechanical camera rotators.  Keeping this
/// deliberately small lets INDI and Alpaca devices share the equipment card,
/// sequencer instruction, and FITS metadata path.</summary>
public interface IRotator {
    string DeviceName { get; }
    bool IsConnected { get; }
    double Position { get; }
    bool IsMoving { get; }
    bool IsReversed { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task MoveToAsync(double degrees, CancellationToken ct = default);
    Task ReverseAsync(bool reversed, CancellationToken ct = default);
    Task AbortAsync(CancellationToken ct = default);
}
