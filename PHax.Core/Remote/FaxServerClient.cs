using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using PHax.Core.Ipc;
using PHax.Core.Models;

namespace PHax.Core.Remote;

/// <summary>A request the server refused or could not answer. The message is suitable for showing to the user.</summary>
public sealed class FaxServerException(string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
    /// <summary>The server no longer accepts this PC's token (it was removed on the server).</summary>
    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;
    /// <summary>The server has this fax under the same id but with different content.</summary>
    public bool IsConflict => Status == HttpStatusCode.Conflict;
    public bool IsNotFound => Status == HttpStatusCode.NotFound;
}

public sealed record PairingResult(string Thumbprint, string Token, Guid ClientId);

/// <summary>
/// HTTPS client for the API a PHax server exposes to the PCs paired with it. Every connection is pinned to the
/// certificate thumbprint recorded during pairing; names and chains are deliberately not consulted, because the
/// certificate is self-signed and addresses on a small network change.
/// </summary>
public sealed class FaxServerClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(10);
    /// <summary>The server sends a heartbeat more often than this, so silence means the connection is dead.</summary>
    private static readonly TimeSpan EventSilenceLimit = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;

    public FaxServerClient(Uri baseUri, string thumbprint, string token)
    {
        _http = CreateHttp(baseUri, (_, cert, _, _) =>
            cert is not null && string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), thumbprint, StringComparison.OrdinalIgnoreCase));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static HttpClient CreateHttp(Uri baseUri, RemoteCertificateValidationCallback validate) => new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = validate },
    }) { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Exchanges a one-time code for a token. See <see cref="PairingProof"/> for why this first connection can be trusted.</summary>
    public static async Task<PairingResult> PairAsync(string address, string code, string clientName, CancellationToken ct = default)
    {
        var baseUri = ServerAddress.ToBaseUri(address) ?? throw new FaxServerException($"\"{address}\" is not a valid server address.");
        string? thumbprint = null;
        using var http = CreateHttp(baseUri, (_, cert, _, _) =>
        {
            thumbprint = cert?.GetCertHashString(HashAlgorithmName.SHA256);
            return thumbprint is not null;
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            // Connect first: the thumbprint is known only once TLS is up, and the proof has to include it.
            using (await http.GetAsync("v1/pair", HttpCompletionOption.ResponseHeadersRead, timeout.Token)) { }
            if (thumbprint is null) throw new FaxServerException("The server did not present a certificate.");
            var seen = thumbprint;
            var proof = await Task.Run(() => PairingProof.Client(code, seen), timeout.Token);
            using var response = await http.PostAsJsonAsync("v1/pair", new PairRequest(clientName, proof), PipeProtocol.Json, timeout.Token);
            await ThrowIfFailedAsync(response, timeout.Token);
            var result = await response.Content.ReadFromJsonAsync<PairResponse>(PipeProtocol.Json, timeout.Token)
                ?? throw new FaxServerException("The server sent an empty response.");
            var expected = await Task.Run(() => PairingProof.Server(code, seen), timeout.Token);
            if (!PairingProof.Matches(expected, result.ServerProof))
                throw new FaxServerException("The server could not prove it issued this code, so this PC was not connected.");
            return new PairingResult(seen, result.Token, result.ClientId);
        }
        catch (Exception ex) when (Unreachable(ex, ct)) { throw new FaxServerException($"Could not reach a PHax server at {address}.", null, ex); }
    }

    public Task<ServiceStatusDto?> GetStatusAsync(CancellationToken ct = default) => GetAsync<ServiceStatusDto>("v1/status", ct);
    public Task<RemoteConfigDto?> GetConfigAsync(CancellationToken ct = default) => GetAsync<RemoteConfigDto>("v1/config", ct);
    public async Task<List<Recipient>> GetRecipientsAsync(CancellationToken ct = default) => await GetAsync<List<Recipient>>("v1/recipients", ct) ?? [];

    public async Task<List<OutboundFax>> ListOutboundAsync(OutboundView view, string? search, CancellationToken ct = default) =>
        await GetAsync<List<OutboundFax>>($"v1/outbound?view={view}&search={Uri.EscapeDataString(search ?? "")}", ct) ?? [];

    public async Task<OutboundFax?> GetOutboundAsync(Guid id, CancellationToken ct = default)
    {
        try { return await GetAsync<OutboundFax>($"v1/outbound/{id}", ct); }
        catch (FaxServerException ex) when (ex.IsNotFound) { return null; }
    }

    /// <summary>
    /// Queues a prepared fax. Safe to repeat: the id makes the call idempotent, so an upload whose response was lost
    /// cannot dial twice. <see cref="FaxServerException.IsConflict"/> means this id was already used for other content.
    /// </summary>
    public async Task<OutboundFax> SubmitAsync(Guid id, SubmitFaxRequest fax, Stream tiff, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(JsonSerializer.Serialize(fax, PipeProtocol.Json)), "fax" },
            { new StreamContent(tiff), "document", "fax.tif" },
        };
        return await SendAsync<OutboundFax>(HttpMethod.Put, $"v1/outbound/{id}", form, TransferTimeout, ct)
            ?? throw new FaxServerException("The server sent an empty response.");
    }

    public async Task<string> OutboundActionAsync(Guid id, string action, CancellationToken ct = default) =>
        (await SendAsync<MessageDto>(HttpMethod.Post, $"v1/outbound/{id}/{action}", null, RequestTimeout, ct))?.Message ?? "";

    public async Task<OutboundFax> ResendAsync(Guid sourceId, Guid newId, CancellationToken ct = default) =>
        await SendAsync<OutboundFax>(HttpMethod.Post, $"v1/outbound/{sourceId}/resend/{newId}", null, RequestTimeout, ct)
            ?? throw new FaxServerException("The server sent an empty response.");

    public Task DeleteOutboundAsync(Guid id, CancellationToken ct = default) => SendAsync<object>(HttpMethod.Delete, $"v1/outbound/{id}", null, RequestTimeout, ct);
    public Task<byte[]> GetOutboundDocumentAsync(Guid id, CancellationToken ct = default) => GetBytesAsync($"v1/outbound/{id}/document", ct);

    public async Task<List<ReceivedFax>> ListReceivedAsync(bool includeIncomplete, string? search, CancellationToken ct = default) =>
        await GetAsync<List<ReceivedFax>>($"v1/inbound?includeIncomplete={includeIncomplete}&search={Uri.EscapeDataString(search ?? "")}", ct) ?? [];

    public async Task<int> GetUnreadCountAsync(CancellationToken ct = default) => (await GetAsync<CountDto>("v1/inbound/unread", ct))?.Count ?? 0;
    public Task SetReadAsync(Guid id, bool read, CancellationToken ct = default) => SendAsync<object>(HttpMethod.Post, $"v1/inbound/{id}/read", Json(new ReadRequest(read)), RequestTimeout, ct);
    public Task RenameReceivedAsync(Guid id, string name, CancellationToken ct = default) => SendAsync<object>(HttpMethod.Post, $"v1/inbound/{id}/rename", Json(new RenameRequest(name)), RequestTimeout, ct);
    public Task DeleteReceivedAsync(Guid id, CancellationToken ct = default) => SendAsync<object>(HttpMethod.Delete, $"v1/inbound/{id}", null, RequestTimeout, ct);
    public Task<byte[]> GetReceivedDocumentAsync(Guid id, CancellationToken ct = default) => GetBytesAsync($"v1/inbound/{id}/document", ct);
    public Task AnswerCallAsync(CancellationToken ct = default) => SendAsync<object>(HttpMethod.Post, "v1/answer", null, RequestTimeout, ct);

    /// <summary>Reads server events until the connection drops (an exception) or <paramref name="ct"/> is cancelled.</summary>
    public async Task ReadEventsAsync(Action<PipeEnvelope> onEvent, Action? onConnected, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/events");
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(RequestTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token);
            await ThrowIfFailedAsync(response, ct);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            onConnected?.Invoke();
            while (true)
            {
                using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
                silence.CancelAfter(EventSilenceLimit);
                var line = await reader.ReadLineAsync(silence.Token);
                if (line is null) throw new FaxServerException("The server closed the connection.");
                if (line.Length == 0) continue; // heartbeat
                PipeEnvelope? envelope;
                try { envelope = PipeProtocol.Deserialize(line); } catch (JsonException) { continue; }
                if (envelope is not null) onEvent(envelope);
            }
        }
        catch (Exception ex) when (Unreachable(ex, ct)) { throw new FaxServerException("The connection to the PHax server was lost.", null, ex); }
    }

    private static JsonContent Json<T>(T value) => JsonContent.Create(value, options: PipeProtocol.Json);

    private Task<T?> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, RequestTimeout, ct);

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, HttpContent? content, TimeSpan limit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            using var response = await _http.SendAsync(request, timeout.Token);
            await ThrowIfFailedAsync(response, timeout.Token);
            return response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>(PipeProtocol.Json, timeout.Token);
        }
        catch (Exception ex) when (Unreachable(ex, ct)) { throw new FaxServerException("Could not reach the PHax server.", null, ex); }
    }

    private async Task<byte[]> GetBytesAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TransferTimeout);
        try
        {
            using var response = await _http.GetAsync(path, timeout.Token);
            await ThrowIfFailedAsync(response, timeout.Token);
            return await response.Content.ReadAsByteArrayAsync(timeout.Token);
        }
        catch (Exception ex) when (Unreachable(ex, ct)) { throw new FaxServerException("Could not reach the PHax server.", null, ex); }
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? message = null;
        try { message = (await response.Content.ReadFromJsonAsync<ErrorDto>(PipeProtocol.Json, ct))?.Error; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { }
        throw new FaxServerException(message ?? (response.StatusCode == HttpStatusCode.Unauthorized
            ? "The PHax server no longer accepts this PC. Connect it again in Settings > Network."
            : $"The PHax server refused the request ({(int)response.StatusCode})."), response.StatusCode);
    }

    /// <summary>Network failures and timeouts, as opposed to the caller cancelling or the server answering with an error.</summary>
    private static bool Unreachable(Exception ex, CancellationToken caller) =>
        ex is HttpRequestException or IOException || (ex is OperationCanceledException && !caller.IsCancellationRequested);

    public void Dispose() => _http.Dispose();
}
