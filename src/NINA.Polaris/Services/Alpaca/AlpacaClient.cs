using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services.Alpaca;

/// <summary>
/// Minimal Alpaca (ASCOM HTTP) client. Wraps the standard
/// <c>/api/v1/{devicetype}/{devicenumber}/{action}</c> URL convention and
/// the ClientID + ClientTransactionID query-parameter requirements.
///
/// Reference: <a href="https://ascom-standards.org/api/">Alpaca API</a>.
/// </summary>
public class AlpacaClient {
    private static readonly HttpClient SharedHttp = new() {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public string Host { get; }
    public int Port { get; }
    public string DeviceType { get; }
    public int DeviceNumber { get; }
    public int ClientId { get; }

    private int _txn;

    public AlpacaClient(string host, int port, string deviceType, int deviceNumber, int clientId = 1) {
        Host = host;
        Port = port;
        DeviceType = deviceType.ToLowerInvariant();
        DeviceNumber = deviceNumber;
        ClientId = clientId;
    }

    private string Url(string action) =>
        $"http://{Host}:{Port}/api/v1/{DeviceType}/{DeviceNumber}/{action}";

    private int NextTxn() => Interlocked.Increment(ref _txn);

    public async Task<T?> GetAsync<T>(string action, CancellationToken ct = default) {
        var url = $"{Url(action)}?ClientID={ClientId}&ClientTransactionID={NextTxn()}";
        var resp = await SharedHttp.GetFromJsonAsync<AlpacaResponse<T>>(url, ct);
        ThrowIfError(resp);
        return resp == null ? default : resp.Value;
    }

    public async Task<JsonElement?> GetRawAsync(string action, CancellationToken ct = default) {
        var url = $"{Url(action)}?ClientID={ClientId}&ClientTransactionID={NextTxn()}";
        using var resp = await SharedHttp.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    public async Task PutAsync(string action, Dictionary<string, string>? formFields = null,
        CancellationToken ct = default) {
        formFields ??= new();
        formFields["ClientID"] = ClientId.ToString();
        formFields["ClientTransactionID"] = NextTxn().ToString();
        var content = new FormUrlEncodedContent(formFields!);
        using var resp = await SharedHttp.PutAsync(Url(action), content, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<AlpacaResponse<object>>(cancellationToken: ct);
        ThrowIfError(body);
    }

    private static void ThrowIfError<T>(AlpacaResponse<T>? r) {
        if (r == null) return;
        if (r.ErrorNumber != 0) {
            throw new InvalidOperationException($"Alpaca error {r.ErrorNumber}: {r.ErrorMessage}");
        }
    }
}

/// <summary>Standard Alpaca JSON envelope.</summary>
public class AlpacaResponse<T> {
    [JsonPropertyName("Value")]
    public T? Value { get; set; }

    [JsonPropertyName("ClientTransactionID")]
    public int ClientTransactionID { get; set; }

    [JsonPropertyName("ServerTransactionID")]
    public int ServerTransactionID { get; set; }

    [JsonPropertyName("ErrorNumber")]
    public int ErrorNumber { get; set; }

    [JsonPropertyName("ErrorMessage")]
    public string ErrorMessage { get; set; } = "";
}
