// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Newtonsoft.Json;

namespace MNVTEngine.Galaxy.WebSockets;

/// <summary>Settings of a <see cref="WebSocketConnection"/>.</summary>
public sealed class WebSocketOptions
{
    /// <summary>The server, <c>ws://</c> or <c>wss://</c>.</summary>
    public required Uri Url { get; init; }

    /// <summary>Sent with every connection attempt (e.g. <c>Authorization</c>); change them between attempts if a token rotates.</summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often a ping keeps the connection (and proxies) alive.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>A larger incoming message closes the connection (code 1009).</summary>
    public int MaxMessageBytes { get; init; } = 1024 * 1024;

    /// <summary>Messages <see cref="WebSocketConnection.Send"/> may queue while the socket is busy; more are refused.</summary>
    public int MaxQueuedMessages { get; init; } = 256;

    /// <summary>Connect again after a lost connection or a failed attempt, waiting longer each time up to <see cref="MaxReconnectDelay"/>.</summary>
    public bool Reconnect { get; init; } = true;

    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);
}

public enum WebSocketConnectionState
{
    /// <summary>Not started yet.</summary>
    Idle,
    Connecting,
    Open,

    /// <summary>Lost or failed; waiting before the next attempt.</summary>
    Reconnecting,

    /// <summary>Stopped by <see cref="WebSocketConnection.Close"/>, or lost with <see cref="WebSocketOptions.Reconnect"/> off.</summary>
    Closed,
}

/// <summary>
/// A text WebSocket for game code (live updates from a backend: notifications, presence, chat), with reconnects,
/// keep-alive pings and a size limit. Events are raised on the synchronization context that was current at
/// <see cref="Start"/> (the game thread in a component), never after <see cref="Close"/>. Messages are text; JSON
/// helpers use Newtonsoft. See docs/engine/websockets.md.
/// </summary>
public sealed class WebSocketConnection : IDisposable
{
    private readonly WebSocketOptions _options;
    private readonly Func<Uri, IReadOnlyDictionary<string, string>, CancellationToken, Task<WebSocket>> _connect;
    private readonly CancellationTokenSource _stop = new();
    private SynchronizationContext? _context;
    private Channel<string>? _outbox;
    private volatile bool _closed;

    public WebSocketConnection(WebSocketOptions options)
        : this(options, null)
    {
    }

    /// <summary>With a custom transport (tests, proxies): <paramref name="connect"/> returns an open socket or throws.</summary>
    public WebSocketConnection(WebSocketOptions options, Func<Uri, IReadOnlyDictionary<string, string>, CancellationToken, Task<WebSocket>>? connect)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Url.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException($"A WebSocket URL starts with ws:// or wss://, not '{options.Url.Scheme}://'.", nameof(options));
        }

        _options = options;
        _connect = connect ?? ConnectClientAsync;
    }

    /// <summary>Each time a connection opens (after a reconnect too).</summary>
    public event Action? Opened;

    /// <summary>A text message from the server.</summary>
    public event Action<string>? Message;

    /// <summary>Each time a connection ends or an attempt fails, with the reason; a reconnect follows unless <see cref="State"/> is Closed.</summary>
    public event Action<string>? Disconnected;

    public WebSocketConnectionState State { get; private set; }

    public Uri Url => _options.Url;

    /// <summary>Starts connecting in the background. Call once.</summary>
    public void Start()
    {
        if (State != WebSocketConnectionState.Idle || _closed)
        {
            throw new InvalidOperationException("The connection was started already.");
        }

        _context = SynchronizationContext.Current;
        State = WebSocketConnectionState.Connecting;
        _ = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Queues a text message; false when not open or the queue is full (nothing is kept for a later connection).</summary>
    public bool Send(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return State == WebSocketConnectionState.Open && _outbox is { } outbox && outbox.Writer.TryWrite(text);
    }

    public bool SendJson(object value) => Send(JsonConvert.SerializeObject(value));

    /// <summary>Stops for good: closes the socket and stops reconnecting. No events are raised afterwards.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        State = WebSocketConnectionState.Closed;
        _stop.Cancel();
    }

    public void Dispose()
    {
        Close();
        _stop.Dispose();
    }

    private static async Task<WebSocket> ConnectClientAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            foreach (var (name, value) in headers)
            {
                socket.Options.SetRequestHeader(name, value);
            }

            await socket.ConnectAsync(url, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var delay = _options.ReconnectDelay;
        while (!stop.IsCancellationRequested)
        {
            string reason;
            WebSocket? socket = null;
            try
            {
                using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    attempt.CancelAfter(_options.ConnectTimeout);
                    socket = await _connect(_options.Url, new Dictionary<string, string>(_options.Headers), attempt.Token).ConfigureAwait(false);
                }

                _outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(_options.MaxQueuedMessages) { SingleReader = true });
                State = WebSocketConnectionState.Open;
                delay = _options.ReconnectDelay;
                Raise(() => Opened?.Invoke());
                var sending = SendLoopAsync(socket, _outbox.Reader, stop);
                reason = await ReceiveLoopAsync(socket, stop).ConfigureAwait(false);
                _outbox.Writer.TryComplete();
                await sending.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                reason = $"No answer from {_options.Url.Host} within {_options.ConnectTimeout.TotalSeconds:0} s.";
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException)
            {
                reason = ex.Message;
            }
            finally
            {
                _outbox?.Writer.TryComplete();
                _outbox = null;
                if (socket is not null)
                {
                    await CloseQuietlyAsync(socket).ConfigureAwait(false);
                }
            }

            if (stop.IsCancellationRequested)
            {
                break;
            }

            var again = _options.Reconnect;
            State = again ? WebSocketConnectionState.Reconnecting : WebSocketConnectionState.Closed;
            Raise(() => Disconnected?.Invoke(reason));
            if (!again)
            {
                return;
            }

            try
            {
                await Task.Delay(delay * (0.8 + (Random.Shared.NextDouble() * 0.4)), stop).ConfigureAwait(false); // jitter: clients do not return in step
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxReconnectDelay.Ticks));
        }
    }

    /// <summary>Reads messages until the connection ends; returns why it ended.</summary>
    private async Task<string> ReceiveLoopAsync(WebSocket socket, CancellationToken stop)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var frame = await socket.ReceiveAsync(buffer, stop).ConfigureAwait(false);
            if (frame.MessageType == WebSocketMessageType.Close)
            {
                return socket.CloseStatusDescription is { Length: > 0 } description ? description : $"Closed by the server ({socket.CloseStatus}).";
            }

            if (message.Length + frame.Count > _options.MaxMessageBytes)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Message too big", stop).ConfigureAwait(false);
                return $"The server sent a message over {_options.MaxMessageBytes} bytes.";
            }

            message.Write(buffer, 0, frame.Count);
            if (!frame.EndOfMessage)
            {
                continue;
            }

            if (frame.MessageType == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                Raise(() => Message?.Invoke(text));
            }

            message.SetLength(0); // binary messages are not part of this API: skipped
        }
    }

    private static async Task SendLoopAsync(WebSocket socket, ChannelReader<string> outbox, CancellationToken stop)
    {
        try
        {
            await foreach (var text in outbox.ReadAllAsync(stop).ConfigureAwait(false))
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, stop).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // the receive loop reports why the connection ended; what was queued is dropped with it
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Bye", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // closing a socket that is already broken: it is disposed below either way
        }
        finally
        {
            socket.Dispose();
        }
    }

    private void Raise(Action raise)
    {
        void Guarded()
        {
            if (!_closed)
            {
                raise();
            }
        }

        if (_context is { } context)
        {
            context.Post(_ => Guarded(), null);
        }
        else
        {
            Guarded();
        }
    }
}
