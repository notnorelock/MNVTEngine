// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace MNVTEngine.Galaxy.WebSockets.Tests;

/// <summary>WebSocketConnection over loopback TCP pairs (the server side made with CreateFromStream, no HTTP).</summary>
public sealed class WebSocketConnectionTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly Loopback _loopback = new();

    public void Dispose() => _loopback.Dispose();

    private WebSocketConnection Connection(int maxMessageBytes = 1024 * 1024, bool reconnect = true, Action<WebSocketOptions>? configure = null)
    {
        var options = new WebSocketOptions
        {
            Url = new Uri("ws://game.example/live"),
            MaxMessageBytes = maxMessageBytes,
            Reconnect = reconnect,
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
        };
        configure?.Invoke(options);
        return new WebSocketConnection(options, _loopback.ConnectAsync);
    }

    [Fact]
    public async Task Messages_GoBothWays_AndHeadersReachTheTransport()
    {
        using var connection = Connection(configure: o => o.Headers["Authorization"] = "Bearer token");
        var opened = Signal(h => connection.Opened += h);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Message += text => received.TrySetResult(text);
        connection.Start();

        var server = await _loopback.Servers.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken);
        await opened.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal("Bearer token", _loopback.LastHeaders!["authorization"]);

        await server.SendAsync(Encoding.UTF8.GetBytes("zażółć {\"type\":\"hi\"}"), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        Assert.Equal("zażółć {\"type\":\"hi\"}", await received.Task.WaitAsync(Wait, TestContext.Current.CancellationToken));

        Assert.True(connection.SendJson(new { type = "ping" }));
        Assert.Equal("{\"type\":\"ping\"}", await ReadTextAsync(server));
    }

    [Fact]
    public async Task ALostConnection_ReconnectsAndOpensAgain()
    {
        using var connection = Connection();
        var opens = 0;
        var reopened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reasons = new ConcurrentQueue<string>();
        connection.Opened += () =>
        {
            if (Interlocked.Increment(ref opens) == 2)
            {
                reopened.TrySetResult();
            }
        };
        connection.Disconnected += reasons.Enqueue;
        connection.Start();

        var first = await _loopback.Servers.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken);
        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "server restart", TestContext.Current.CancellationToken);

        await reopened.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketConnectionState.Open, connection.State);
        Assert.Equal(2, _loopback.Attempts);
        Assert.Contains("server restart", reasons);
    }

    [Fact]
    public async Task AFailedAttempt_IsRetried()
    {
        _loopback.FailNext = 2;
        using var connection = Connection();
        var opened = Signal(h => connection.Opened += h);
        var failures = 0;
        connection.Disconnected += _ => Interlocked.Increment(ref failures);
        connection.Start();

        await opened.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(3, _loopback.Attempts);
        Assert.Equal(2, failures);
    }

    [Fact]
    public async Task AMessageOverTheLimit_EndsTheConnection()
    {
        using var connection = Connection(maxMessageBytes: 16, reconnect: false);
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Disconnected += closed.SetResult;
        var messages = 0;
        connection.Message += _ => messages++;
        connection.Start();

        var server = await _loopback.Servers.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken);
        await server.SendAsync(new byte[64], WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);

        Assert.Contains("16 bytes", await closed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(0, messages);
        Assert.Equal(WebSocketConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Close_StopsReconnecting_AndSendingIsRefused()
    {
        using var connection = Connection();
        var opened = Signal(h => connection.Opened += h);
        connection.Start();
        await opened.WaitAsync(Wait, TestContext.Current.CancellationToken);

        connection.Close();
        Assert.Equal(WebSocketConnectionState.Closed, connection.State);
        Assert.False(connection.Send("late"));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(1, _loopback.Attempts);
    }

    [Fact]
    public void OnlyWebSocketUrls_AreAccepted() =>
        Assert.Throws<ArgumentException>(() => new WebSocketConnection(new WebSocketOptions { Url = new Uri("https://game.example/live") }));

    private static Task Signal(Action<Action> subscribe)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe(() => done.TrySetResult());
        return done.Task;
    }

    private static async Task<string> ReadTextAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        var frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);
        return Encoding.UTF8.GetString(buffer, 0, frame.Count);
    }

    /// <summary>Hands the connection a client socket and keeps the matching server socket for the test.</summary>
    private sealed class Loopback : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentBag<IDisposable> _owned = [];
        private int _attempts;

        public Loopback() => _listener.Start();

        public Channel<WebSocket> Servers { get; } = Channel.CreateUnbounded<WebSocket>();

        public int Attempts => Volatile.Read(ref _attempts);

        public int FailNext { get; set; }

        public Dictionary<string, string>? LastHeaders { get; private set; }

        public async Task<WebSocket> ConnectAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            LastHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
            if (FailNext > 0)
            {
                FailNext--;
                throw new WebSocketException("refused for the test");
            }

            var client = new TcpClient();
            _owned.Add(client);
            await client.ConnectAsync((IPEndPoint)_listener.LocalEndpoint, cancellationToken);
            var accepted = await _listener.AcceptTcpClientAsync(cancellationToken);
            _owned.Add(accepted);
            var server = WebSocket.CreateFromStream(accepted.GetStream(), new WebSocketCreationOptions { IsServer = true });
            _owned.Add(server);
            Servers.Writer.TryWrite(server);
            return WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false });
        }

        public void Dispose()
        {
            foreach (var owned in _owned)
            {
                owned.Dispose();
            }

            _listener.Stop();
        }
    }
}
