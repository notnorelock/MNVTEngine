# WebSocket client (MNVTEngine.Galaxy.WebSockets)

`WebSocketConnection` keeps a text WebSocket open to a backend for live updates: notifications, presence, chat. Game
rooms use Galaxy's own UDP transport; this is for the web services next to them. It ships with the `networking`
module and is headless (no scenes, graphics or platform).

```csharp
using MNVTEngine.Galaxy.WebSockets;

public sealed class LiveFeed : Component
{
    private WebSocketConnection? _live;

    public override void Start()
    {
        var options = new WebSocketOptions { Url = new Uri("wss://api.example.com/v1/live") };
        options.Headers["Authorization"] = $"Bearer {Session.Token}";
        _live = new WebSocketConnection(options);
        _live.Opened += () => Log.Info("live");
        _live.Message += text => Handle(JsonConvert.DeserializeObject<LiveEvent>(text)); // on the game thread
        _live.Disconnected += reason => Log.Warn($"live feed lost: {reason}"); // it reconnects by itself
        _live.Start();
    }

    public override void OnDestroy() => _live?.Dispose();
}
```

- **Events** (`Opened`, `Message`, `Disconnected`) are raised on the synchronization context that was current at
  `Start`: in a component, the game thread, also after the component's `DontDestroyOnLoad` object moved on to later
  scenes. None are raised after `Close`/`Dispose`.
- **Reconnects.** A lost connection or a failed attempt is retried after `ReconnectDelay`, doubling up to
  `MaxReconnectDelay`, with jitter so clients do not return in step. `Opened` fires again on every connection, which
  is the place to fetch what was missed. `Reconnect = false` stops at the first loss (`State` becomes `Closed`).
- **Headers** are read on every attempt, so a rotated token can be set before the next one.
- **Limits.** A ping every `KeepAlive` (20 s) keeps proxies from closing an idle socket. An incoming message over
  `MaxMessageBytes` (1 MB) closes the connection with code 1009. `Send` queues up to `MaxQueuedMessages` and returns
  false when the socket is not open or the queue is full; nothing is kept for a later connection.
- **Text only.** `Send`/`SendJson` (Newtonsoft) send text frames; binary messages from the server are skipped.
- **Tests and proxies** pass their own transport to the constructor: a function that returns an open `WebSocket`
  (the engine's tests use `WebSocket.CreateFromStream` over loopback TCP).
