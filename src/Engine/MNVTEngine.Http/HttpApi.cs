// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Net;
using System.Text;
using Newtonsoft.Json;

namespace MNVTEngine.Http;

/// <summary>
/// An HTTP client instance with defaults (<see cref="HttpOptions"/>), in the spirit of Axios. Requests return an
/// <see cref="HttpResponse{T}"/> instead of throwing for HTTP, network, timeout and parse failures; only cancellation
/// by the caller throws. In game code, <c>await</c> resumes on the game thread. Create one per service and keep it:
/// it pools connections. Thread-safe once its interceptors are added.
/// </summary>
public sealed class HttpApi : IDisposable
{
    private readonly HttpClient _client;
    private readonly List<Func<HttpRequest, ValueTask>> _requestInterceptors = [];
    private readonly List<Func<HttpResponse, ValueTask>> _responseInterceptors = [];

    public HttpApi(HttpOptions? options = null)
        : this(options, new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), AutomaticDecompression = DecompressionMethods.All })
    {
    }

    /// <summary>With a custom handler (tests, proxies); the instance disposes it.</summary>
    public HttpApi(HttpOptions? options, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Options = options ?? new HttpOptions();
        if (Options.BaseUrl is { } baseUrl && !baseUrl.AbsolutePath.EndsWith('/'))
        {
            Options.BaseUrl = new UriBuilder(baseUrl) { Path = baseUrl.AbsolutePath + "/" }.Uri;
        }

        _client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan }; // timeouts are per request
    }

    public HttpOptions Options { get; }

    /// <summary>Runs before every request, in the order added (e.g. adds an auth header).</summary>
    public HttpApi OnRequest(Func<HttpRequest, ValueTask> interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        _requestInterceptors.Add(interceptor);
        return this;
    }

    /// <summary>Runs after every response, failed ones included (e.g. signs out on 401).</summary>
    public HttpApi OnResponse(Func<HttpResponse, ValueTask> interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        _responseInterceptors.Add(interceptor);
        return this;
    }

    public Task<HttpResponse<T>> GetAsync<T>(string url, Action<HttpRequest>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync<T>(Build(HttpMethod.Get, url, null, configure), cancellationToken);

    public Task<HttpResponse<T>> PostAsync<T>(string url, object? body = null, Action<HttpRequest>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync<T>(Build(HttpMethod.Post, url, body, configure), cancellationToken);

    public Task<HttpResponse<T>> PutAsync<T>(string url, object? body = null, Action<HttpRequest>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync<T>(Build(HttpMethod.Put, url, body, configure), cancellationToken);

    public Task<HttpResponse<T>> PatchAsync<T>(string url, object? body = null, Action<HttpRequest>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync<T>(Build(HttpMethod.Patch, url, body, configure), cancellationToken);

    public Task<HttpResponse<T>> DeleteAsync<T>(string url, Action<HttpRequest>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync<T>(Build(HttpMethod.Delete, url, null, configure), cancellationToken);

    /// <summary>
    /// Sends <paramref name="request"/> and reads the body as <typeparamref name="T"/> (<see cref="string"/> gives the text,
    /// <see cref="byte"/>[] the raw bytes).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<HttpResponse<T>> SendAsync<T>(HttpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var interceptor in _requestInterceptors)
        {
            await interceptor(request).ConfigureAwait(false);
        }

        var response = await ExchangeAsync<T>(request, cancellationToken).ConfigureAwait(false);
        foreach (var interceptor in _responseInterceptors)
        {
            await interceptor(response).ConfigureAwait(false);
        }

        return response;
    }

    public void Dispose() => _client.Dispose();

    private static HttpRequest Build(HttpMethod method, string url, object? body, Action<HttpRequest>? configure)
    {
        var request = new HttpRequest(method, url) { Body = body };
        configure?.Invoke(request);
        return request;
    }

    private async Task<HttpResponse<T>> ExchangeAsync<T>(HttpRequest request, CancellationToken cancellationToken)
    {
        var empty = (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
        HttpResponse<T> Fail(HttpErrorKind kind, string message, Exception? ex = null, HttpStatusCode? status = null, IReadOnlyDictionary<string, string>? headers = null, string text = "") =>
            new(request, status, headers ?? empty, text, new HttpError(kind, message, ex), Options.Json, default);

        if (!TryResolve(request, out var uri))
        {
            return Fail(HttpErrorKind.Network, $"'{request.Url}' is not a valid http(s) URL.");
        }

        using var message = new HttpRequestMessage(request.Method, uri)
        {
            Content = request.Body switch
            {
                null => null,
                HttpContent content => content,
                string text => new StringContent(text, Encoding.UTF8, "text/plain"),
                var body => new StringContent(JsonConvert.SerializeObject(body, Options.Json), Encoding.UTF8, "application/json"),
            },
        };
        foreach (var (name, value) in Options.Headers.Concat(request.Headers).GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
        {
            // Content headers (Content-Type, ...) only exist with a body; a configured one replaces the default.
            if (!message.Headers.TryAddWithoutValidation(name, value) && message.Content is { } body)
            {
                body.Headers.Remove(name);
                body.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout ?? Options.Timeout);
        try
        {
            using var answer = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var headers = answer.Headers.Concat(answer.Content.Headers).GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)), StringComparer.OrdinalIgnoreCase);
            var limit = request.MaxResponseBytes ?? Options.MaxResponseBytes;
            if (await ReadLimitedAsync(answer.Content, limit, timeout.Token).ConfigureAwait(false) is not { } bytes)
            {
                return Fail(HttpErrorKind.TooLarge, $"The response is larger than {limit} bytes.", status: answer.StatusCode, headers: headers);
            }

            var binary = typeof(T) == typeof(byte[]) && answer.IsSuccessStatusCode;
            var text = binary ? "" : Charset(answer.Content.Headers.ContentType?.CharSet).GetString(bytes);
            if (binary)
            {
                return new HttpResponse<T>(request, answer.StatusCode, headers, text, null, Options.Json, (T)(object)bytes);
            }

            if (!answer.IsSuccessStatusCode)
            {
                return Fail(HttpErrorKind.Status, $"{uri.Host} answered {(int)answer.StatusCode} {answer.ReasonPhrase}.", status: answer.StatusCode, headers: headers, text: text);
            }

            try
            {
                var data = typeof(T) == typeof(string) ? (T)(object)text : text.Length == 0 ? default : JsonConvert.DeserializeObject<T>(text, Options.Json);
                return new HttpResponse<T>(request, answer.StatusCode, headers, text, null, Options.Json, data);
            }
            catch (JsonException ex)
            {
                return Fail(HttpErrorKind.Parse, $"The answer from {uri.Host} is not the expected data: {ex.Message}", ex, answer.StatusCode, headers, text);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(HttpErrorKind.Timeout, $"{uri.Host} did not answer in time.", ex);
        }
        catch (HttpRequestException ex)
        {
            return Fail(HttpErrorKind.Network, $"Could not reach {uri.Host}: {ex.Message}", ex);
        }
    }

    private bool TryResolve(HttpRequest request, out Uri uri)
    {
        var ok = Uri.TryCreate(request.Url, UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https"
            || (Options.BaseUrl is { } baseUrl && Uri.TryCreate(baseUrl, request.Url.TrimStart('/'), out uri!));
        if (!ok || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var query = request.Query.Where(p => p.Value is not null).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value!)).ToList();
        if (query.Count > 0)
        {
            uri = new UriBuilder(uri) { Query = (uri.Query.Length > 1 ? uri.Query[1..] + "&" : "") + string.Join("&", query) }.Uri;
        }

        return true;
    }

    /// <summary>The body, or null when it exceeds <paramref name="limit"/> bytes (checked while reading, not only from Content-Length).</summary>
    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, long limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit)
        {
            return null;
        }

        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static Encoding Charset(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(name.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8; // unknown charset from the server: UTF-8 is what JSON APIs send anyway
        }
    }
}
