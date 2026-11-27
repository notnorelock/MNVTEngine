// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-27.

using System.Net;
using System.Text;

namespace MNVTEngine.Http.Tests;

public sealed class HttpApiTests
{
    private sealed record Player(string DisplayName, int Level);

    private sealed record Problem(string Code, string Message);

    /// <summary>Answers every request with <paramref name="answer"/> and remembers what was sent.</summary>
    private sealed class Recorder(List<HttpProgress> seen) : IProgress<HttpProgress>
    {
        public void Report(HttpProgress value) => seen.Add(value);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Sent.Add((request, body));
            return await answer(request, cancellationToken);
        }
    }

    private static StubHandler Json(HttpStatusCode status, string json) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));

    private static HttpApi Api(StubHandler handler, Action<HttpOptions>? configure = null)
    {
        var options = new HttpOptions { BaseUrl = new Uri("https://api.example.com/v1") };
        configure?.Invoke(options);
        return new HttpApi(options, handler);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_ResolvesAgainstTheBaseUrl_AddsQueryAndHeaders_AndReadsCamelCaseJson()
    {
        var handler = Json(HttpStatusCode.OK, """{"displayName":"Nova","level":7}""");
        using var api = Api(handler, o => o.Headers["X-Client"] = "game");

        var response = await api.GetAsync<Player>("players/me", r =>
        {
            r.Query["fields"] = "name level";
            r.Query["skip"] = null;
            r.WithBearer("t0ken");
        }, Token);

        Assert.True(response.Ok);
        Assert.Equal(200, response.Status);
        Assert.Equal(new Player("Nova", 7), response.Data);
        var sent = handler.Sent.Single().Request;
        Assert.Equal("https://api.example.com/v1/players/me?fields=name%20level", sent.RequestUri!.AbsoluteUri);
        Assert.Equal("game", sent.Headers.GetValues("X-Client").Single());
        Assert.Equal("Bearer t0ken", sent.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Post_SendsTheBodyAsCamelCaseJson()
    {
        var handler = Json(HttpStatusCode.Created, "{}");
        using var api = Api(handler);

        var response = await api.PostAsync<Player>("players", new Player("Nova", 1), cancellationToken: Token);

        Assert.True(response.Ok);
        Assert.Equal(HttpMethod.Post, handler.Sent.Single().Request.Method);
        Assert.Equal("""{"displayName":"Nova","level":1}""", handler.Sent.Single().Body);
        Assert.Equal("application/json", handler.Sent.Single().Request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task ErrorStatus_IsAResult_WithTheTypedErrorBody()
    {
        using var api = Api(Json(HttpStatusCode.Conflict, """{"code":"email_taken","message":"Taken."}"""));

        var response = await api.PostAsync<Player>("players", new { }, cancellationToken: Token);

        Assert.False(response.Ok);
        Assert.Equal(HttpErrorKind.Status, response.Error!.Kind);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(response.Data);
        Assert.Equal(new Problem("email_taken", "Taken."), response.ErrorData<Problem>());
        Assert.Throws<HttpRequestFailedException>(() => response.EnsureSuccess());
    }

    [Fact]
    public async Task UnexpectedJson_IsAParseError()
    {
        using var api = Api(Json(HttpStatusCode.OK, "<html>maintenance</html>"));

        var response = await api.GetAsync<Player>("players/me", cancellationToken: Token);

        Assert.Equal(HttpErrorKind.Parse, response.Error!.Kind);
        Assert.Equal("<html>maintenance</html>", response.Text);
    }

    [Fact]
    public async Task OversizedResponse_FailsWhileReading_EvenWithoutContentLength()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[4096])), // no Content-Length header set by the stub
        }));
        using var api = Api(handler, o => o.MaxResponseBytes = 1024);

        var response = await api.GetAsync<string>("big", cancellationToken: Token);

        Assert.Equal(HttpErrorKind.TooLarge, response.Error!.Kind);
    }

    [Fact]
    public async Task Bytes_ComeRaw_WithTheirOwnSizeLimit()
    {
        var body = new byte[] { 0, 0xFF, 0x80, 7 }; // not text
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }));
        using var api = Api(handler, o => o.MaxResponseBytes = 2);

        Assert.Equal(HttpErrorKind.TooLarge, (await api.GetAsync<byte[]>("file", cancellationToken: Token)).Error!.Kind);
        var response = await api.GetAsync<byte[]>("file", r => r.MaxResponseBytes = 4, Token);
        Assert.True(response.Ok);
        Assert.Equal(body, response.Data);
    }

    [Fact]
    public async Task Progress_ReportsEachChunkAndTheTotal()
    {
        var body = new byte[200_000]; // more than one 80 KiB chunk
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }));
        using var api = Api(handler, o => o.MaxResponseBytes = body.Length);
        var seen = new List<HttpProgress>();
        var progress = new Recorder(seen);

        var response = await api.GetAsync<byte[]>("file", r => r.Progress = progress, Token);

        Assert.True(response.Ok);
        Assert.Equal(new HttpProgress(0, body.Length), seen[0]);
        Assert.True(seen.Count >= 3);
        Assert.Equal(body.Length, seen[^1].Received);
        Assert.Equal(1f, seen[^1].Fraction);
        Assert.Null(new HttpProgress(5, null).Fraction);
    }

    [Fact]
    public async Task SlowServer_TimesOut_ButCallerCancellationThrows()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var api = Api(handler);

        var timedOut = await api.GetAsync<string>("slow", r => r.Timeout = TimeSpan.FromMilliseconds(50), Token);
        Assert.Equal(HttpErrorKind.Timeout, timedOut.Error!.Kind);
        Assert.Null(timedOut.StatusCode);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = api.GetAsync<string>("slow", cancellationToken: cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ConnectionFailure_IsANetworkError()
    {
        using var api = Api(new StubHandler((_, _) => throw new HttpRequestException("refused")));

        var response = await api.GetAsync<string>("x", cancellationToken: Token);

        Assert.Equal(HttpErrorKind.Network, response.Error!.Kind);
        Assert.IsType<HttpRequestException>(response.Error.Exception);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/x")]
    public async Task NonHttpUrls_AreRejectedWithoutSending(string url)
    {
        var handler = Json(HttpStatusCode.OK, "{}");
        using var api = Api(handler);

        var response = await api.GetAsync<string>(url, cancellationToken: Token);

        Assert.False(response.Ok);
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task Interceptors_RunInOrder_AroundEveryRequest()
    {
        var handler = Json(HttpStatusCode.Unauthorized, "");
        using var api = Api(handler);
        var log = new List<string>();
        api.OnRequest(r =>
            {
                log.Add("auth");
                r.WithBearer("fresh");
                return ValueTask.CompletedTask;
            })
            .OnRequest(_ =>
            {
                log.Add("trace");
                return ValueTask.CompletedTask;
            })
            .OnResponse(r =>
            {
                log.Add("response " + r.Status);
                return ValueTask.CompletedTask;
            });

        await api.DeleteAsync<string>("session", cancellationToken: Token);

        Assert.Equal(["auth", "trace", "response 401"], log);
        Assert.Equal("Bearer fresh", handler.Sent.Single().Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task StringResult_GivesTheRawText_AndAConfiguredContentTypeWins()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("-----BEGIN KEY-----") }));
        using var api = Api(handler);

        var response = await api.PutAsync<string>("key", "raw", r => r.Headers["Content-Type"] = "application/x-pem-file", Token);

        Assert.Equal("-----BEGIN KEY-----", response.Data);
        Assert.Equal("application/x-pem-file", handler.Sent.Single().Request.Content!.Headers.ContentType!.MediaType);
    }
}
