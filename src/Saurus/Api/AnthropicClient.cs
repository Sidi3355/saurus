using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Saurus.Core;

namespace Saurus.Api;

public sealed record ChatTurn(string Role, string Content);

public sealed record ExplainResult(string Text, long InputTokens, long OutputTokens);

public sealed class AnthropicException : Exception
{
    public bool Transient { get; }
    public AnthropicException(string message, bool transient = false) : base(message) => Transient = transient;
}

/// <summary>
/// Streaming client for the Anthropic Messages API.
///
/// Hand-rolled over HttpClient rather than the SDK for three reasons the guardrails
/// depend on: the endpoint is a pinned constant that is asserted before every send, the
/// retry policy is exactly one backed-off attempt with no library default underneath it,
/// and the timeout covers the whole streamed response rather than just the headers.
///
/// The API key is set per-request from a field and is never logged, serialised into an
/// error message, or written to disk by this class.
/// </summary>
public sealed class AnthropicClient : IExplainClient
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ExpectedHost = "api.anthropic.com";
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private string? _apiKey;

    public bool HasKey => !string.IsNullOrWhiteSpace(_apiKey);

    public AnthropicClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,       // the endpoint is fixed; a redirect is a red flag
            MaxConnectionsPerServer = 4
        };

        _http = new HttpClient(handler)
        {
            // Per-request timeouts are applied with a CancellationToken instead, so the
            // handler-level timeout is disabled to avoid two competing clocks.
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("saurus", "1.0"));
    }

    public void SetApiKey(string? key) => _apiKey = key;

    /// <summary>
    /// Opens a TLS connection at startup so the first real query does not pay for the
    /// handshake. Sends no request body and carries no credentials.
    /// </summary>
    public async Task PrewarmAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Options, Endpoint);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                       .ConfigureAwait(false);
        }
        catch { /* purely an optimisation */ }
    }

    /// <summary>
    /// Streams a completion. <paramref name="onDelta"/> is invoked on the calling context's
    /// thread pool for each text chunk as it arrives.
    /// </summary>
    /// <exception cref="AnthropicException">On any non-recoverable failure.</exception>
    public async Task<ExplainResult> StreamAsync(
        string model,
        int maxTokens,
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        Action<string> onDelta,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (!HasKey)
            throw new AnthropicException("No API key configured.");

        // Belt and braces: the key must only ever go to this host.
        var uri = new Uri(Endpoint);
        if (!string.Equals(uri.Host, ExpectedHost, StringComparison.Ordinal) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new AnthropicException("Refusing to send credentials to an unexpected endpoint.");

        var body = BuildBody(model, maxTokens, systemPrompt, turns);

        try
        {
            return await SendOnceAsync(body, onDelta, timeout, ct).ConfigureAwait(false);
        }
        catch (AnthropicException ex) when (ex.Transient)
        {
            // Exactly one retry, backed off. No retry storms: a second failure surfaces.
            Log.Warn($"transient failure, single retry: {ex.Message}");
            await Task.Delay(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
            return await SendOnceAsync(body, onDelta, timeout, ct).ConfigureAwait(false);
        }
    }

    private async Task<ExplainResult> SendOnceAsync(
        string body, Action<string> onDelta, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                              .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AnthropicException("Request timed out.", transient: true);
        }
        catch (HttpRequestException ex)
        {
            throw new AnthropicException($"Network error: {ex.Message}", transient: true);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw await BuildErrorAsync(resp, token).ConfigureAwait(false);

            return await ReadStreamAsync(resp, onDelta, token, ct).ConfigureAwait(false);
        }
    }

    private static async Task<AnthropicException> BuildErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var status = (int)resp.StatusCode;
        string detail;
        try
        {
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            detail = ExtractErrorMessage(raw) ?? raw;
            if (detail.Length > 300) detail = detail[..300];
        }
        catch { detail = resp.ReasonPhrase ?? "unknown"; }

        // Redact before this string can reach a log or a popup.
        detail = Log.Redact(detail);

        var transient = status is 408 or 409 or 429 or >= 500;

        var friendly = status switch
        {
            401 => "API key rejected (401). Re-run `dotnet run -- --set-key`.",
            403 => "API key lacks permission for this model (403).",
            404 => $"Model not found (404) — check `model` in config.json. {detail}",
            429 => "Rate limited by the API (429).",
            >= 500 => $"Anthropic service error ({status}).",
            _ => $"API error {status}: {detail}"
        };

        return new AnthropicException(friendly, transient);
    }

    private static string? ExtractErrorMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Parses the SSE stream. Only `data:` payloads are read; the `event:` line is
    /// redundant because every payload carries its own `type`.
    /// </summary>
    private static async Task<ExplainResult> ReadStreamAsync(
        HttpResponseMessage resp, Action<string> onDelta, CancellationToken token, CancellationToken userCt)
    {
        var sb = new StringBuilder();
        long inputTokens = 0, outputTokens = 0;
        var sawAnyContent = false;

        Stream stream;
        try { stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false); }
        catch (Exception ex) { throw new AnthropicException($"Could not open response stream: {ex.Message}", true); }

        using var reader = new StreamReader(stream, Encoding.UTF8);

        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line[5..].Trim();
                if (payload.Length == 0 || payload == "[DONE]") continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(payload); }
                catch { continue; }   // a malformed frame should not kill the stream

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeProp)) continue;

                    switch (typeProp.GetString())
                    {
                        case "message_start":
                            if (root.TryGetProperty("message", out var m) &&
                                m.TryGetProperty("usage", out var u0))
                            {
                                inputTokens = ReadLong(u0, "input_tokens")
                                            + ReadLong(u0, "cache_read_input_tokens")
                                            + ReadLong(u0, "cache_creation_input_tokens");
                                outputTokens = ReadLong(u0, "output_tokens");
                            }
                            break;

                        case "content_block_delta":
                            if (root.TryGetProperty("delta", out var d) &&
                                d.TryGetProperty("type", out var dt) &&
                                dt.GetString() == "text_delta" &&
                                d.TryGetProperty("text", out var t))
                            {
                                var chunk = t.GetString();
                                if (!string.IsNullOrEmpty(chunk))
                                {
                                    sawAnyContent = true;
                                    sb.Append(chunk);
                                    onDelta(chunk);
                                }
                            }
                            break;

                        case "message_delta":
                            if (root.TryGetProperty("usage", out var u1))
                            {
                                var o = ReadLong(u1, "output_tokens");
                                if (o > 0) outputTokens = o;
                            }
                            break;

                        case "error":
                            var em = root.TryGetProperty("error", out var e) &&
                                     e.TryGetProperty("message", out var emsg)
                                         ? emsg.GetString() ?? "stream error"
                                         : "stream error";
                            throw new AnthropicException(Log.Redact(em));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!userCt.IsCancellationRequested)
        {
            // Timed out mid-stream. Keep whatever arrived; retrying would double-bill for
            // tokens the API has already generated and charged for.
            if (sawAnyContent)
            {
                Log.Warn("stream timed out after partial content; keeping the partial answer");
                return new ExplainResult(sb.ToString(), inputTokens, outputTokens);
            }
            throw new AnthropicException("Request timed out.", transient: true);
        }
        catch (IOException ex)
        {
            if (sawAnyContent) return new ExplainResult(sb.ToString(), inputTokens, outputTokens);
            throw new AnthropicException($"Connection dropped: {ex.Message}", transient: true);
        }

        return new ExplainResult(sb.ToString(), inputTokens, outputTokens);
    }

    private static long ReadLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;

    private static string BuildBody(string model, int maxTokens, string systemPrompt,
                                    IReadOnlyList<ChatTurn> turns)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteNumber("max_tokens", maxTokens);
            w.WriteBoolean("stream", true);
            w.WriteString("system", systemPrompt);

            w.WriteStartArray("messages");
            foreach (var turn in turns)
            {
                w.WriteStartObject();
                w.WriteString("role", turn.Role);
                w.WriteString("content", turn.Content);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose() => _http.Dispose();
}
