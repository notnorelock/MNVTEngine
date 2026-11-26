// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MNVTEngine.Http;

/// <summary>Defaults of an <see cref="HttpApi"/> instance; each request can override headers and the timeout.</summary>
public sealed class HttpOptions
{
    /// <summary>Relative request URLs resolve against it (a trailing slash is added when missing).</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Headers sent with every request (a request's own headers win).</summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Larger responses fail with <see cref="HttpErrorKind.TooLarge"/> instead of filling memory.</summary>
    public long MaxResponseBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>How bodies are written and responses read: camelCase by default, never <c>TypeNameHandling</c>.</summary>
    public JsonSerializerSettings Json { get; set; } = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        DateParseHandling = DateParseHandling.DateTimeOffset,
        TypeNameHandling = TypeNameHandling.None,
    };
}

/// <summary>One request. Interceptors may change it before it is sent.</summary>
public sealed class HttpRequest(HttpMethod method, string url)
{
    public HttpMethod Method { get; set; } = method;

    /// <summary>Absolute, or relative to <see cref="HttpOptions.BaseUrl"/>.</summary>
    public string Url { get; set; } = url;

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Query string parameters, escaped and appended to the URL; null values are skipped.</summary>
    public Dictionary<string, string?> Query { get; } = [];

    /// <summary>Sent as JSON; a <see cref="string"/> is sent as text and <see cref="HttpContent"/> as is.</summary>
    public object? Body { get; set; }

    /// <summary>Overrides <see cref="HttpOptions.Timeout"/> for this request.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Overrides <see cref="HttpOptions.MaxResponseBytes"/> for this request (a download larger than the API's answers).</summary>
    public long? MaxResponseBytes { get; set; }

    /// <summary>Sets the <c>Authorization: Bearer</c> header.</summary>
    public HttpRequest WithBearer(string token)
    {
        Headers["Authorization"] = "Bearer " + token;
        return this;
    }
}

public enum HttpErrorKind
{
    /// <summary>The server answered with a status outside 200-299; the body is in <see cref="HttpResponse.Text"/>.</summary>
    Status,

    /// <summary>No answer: DNS, refused or dropped connection, TLS failure.</summary>
    Network,

    Timeout,

    /// <summary>A success status whose body is not the expected JSON.</summary>
    Parse,

    TooLarge,
}

/// <summary>Why a request did not produce data.</summary>
public sealed record HttpError(HttpErrorKind Kind, string Message, Exception? Exception = null);

/// <summary>A request's outcome. Never thrown: check <see cref="Ok"/> or call <see cref="EnsureSuccess"/>.</summary>
public class HttpResponse
{
    private readonly JsonSerializerSettings _json;

    internal HttpResponse(HttpRequest request, HttpStatusCode? status, IReadOnlyDictionary<string, string> headers, string text, HttpError? error, JsonSerializerSettings json)
    {
        (Request, StatusCode, Headers, Text, Error, _json) = (request, status, headers, text, error, json);
    }

    public HttpRequest Request { get; }

    /// <summary>Null when no answer arrived (network errors, timeouts).</summary>
    public HttpStatusCode? StatusCode { get; }

    public int Status => (int?)StatusCode ?? 0;

    public bool Ok => Error is null;

    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The raw body (empty when there was none).</summary>
    public string Text { get; }

    public HttpError? Error { get; }

    /// <summary>The body of an error answer read as <typeparamref name="T"/> (e.g. an API's error record); default when it is not.</summary>
    public T? ErrorData<T>()
    {
        if (Text.Length == 0)
        {
            return default;
        }

        try
        {
            return JsonConvert.DeserializeObject<T>(Text, _json);
        }
        catch (JsonException)
        {
            return default; // not that shape: callers fall back to Error.Message
        }
    }

    /// <summary>Throws <see cref="HttpRequestFailedException"/> unless the request succeeded.</summary>
    public HttpResponse EnsureSuccess() => Ok ? this : throw new HttpRequestFailedException(this);
}

/// <summary>A response with data read from its body.</summary>
public sealed class HttpResponse<T> : HttpResponse
{
    internal HttpResponse(HttpRequest request, HttpStatusCode? status, IReadOnlyDictionary<string, string> headers, string text, HttpError? error, JsonSerializerSettings json, T? data)
        : base(request, status, headers, text, error, json) => Data = data;

    /// <summary>The body as <typeparamref name="T"/>; default when the request failed or the body was empty.</summary>
    public T? Data { get; }

    public new HttpResponse<T> EnsureSuccess()
    {
        base.EnsureSuccess();
        return this;
    }
}

/// <summary>Thrown only by <see cref="HttpResponse.EnsureSuccess"/>, for callers who prefer exceptions.</summary>
public sealed class HttpRequestFailedException : Exception
{
    public HttpRequestFailedException(HttpResponse response)
        : base(response?.Error?.Message, response?.Error?.Exception) => Response = response;

    public HttpRequestFailedException()
    {
    }

    public HttpRequestFailedException(string message)
        : base(message)
    {
    }

    public HttpRequestFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public HttpResponse? Response { get; }
}
