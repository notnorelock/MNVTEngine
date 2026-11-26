// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MNVTEngine.Galaxy.Server;

/// <summary>
/// A headless dedicated game server. Subclass it and override the hooks to implement game logic. <see cref="Run"/>
/// drives a fixed-rate loop that pumps the network and calls <see cref="OnTick"/>; connection and message hooks fire
/// from inside it. Every hook runs inside a guard that logs and swallows exceptions (except out-of-memory), so a
/// single malformed message or a bug in one handler cannot bring the server down. The server never touches files,
/// graphics or assets — it depends only on the networking layer — which is what keeps a dedicated server free of the
/// client-side file and resource errors it has no business hitting.
/// </summary>
public abstract class GameServer : IDisposable
{
    private readonly GameServerOptions _options;
    private readonly GalaxyServer _server;
    private bool _running;
    private ServerTickStats _timings;
    private double _totalMs;

    protected GameServer(GameServerOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        ILoggerFactory factory = loggerFactory ?? NullLoggerFactory.Instance;
        Logger = factory.CreateLogger(GetType());
        _server = new GalaxyServer(factory.CreateLogger<GalaxyServer>());
        _server.ClientConnected += id => Guard(() => OnClientConnected(id), "OnClientConnected");
        _server.ClientDisconnected += (id, reason) => Guard(() => OnClientDisconnected(id, reason), "OnClientDisconnected");
        _server.MessageReceived += (id, message) => Guard(() => OnMessage(id, message), "OnMessage");
        _timings = ServerTickStats.Empty(1000.0 / options.TickRate);
    }

    /// <summary>A logger named after the concrete server type.</summary>
    protected ILogger Logger { get; }

    /// <summary>The server options.</summary>
    protected GameServerOptions Options => _options;

    /// <summary>
    /// How long ticks take (network pump and <see cref="OnTick"/>) since the start or <see cref="ResetTimings"/>. A
    /// snapshot replaced after every tick, so another thread (a health endpoint, a load test) may read it.
    /// </summary>
    public ServerTickStats Timings => Volatile.Read(ref _timings);

    /// <summary>Starts the <see cref="Timings"/> over, e.g. after a warm-up; call it on the thread that ticks.</summary>
    public void ResetTimings()
    {
        _totalMs = 0;
        Volatile.Write(ref _timings, ServerTickStats.Empty(1000.0 / _options.TickRate));
    }

    /// <summary>The connected clients' ids.</summary>
    protected IReadOnlyCollection<uint> Clients => _server.Clients;

    /// <summary>Binds the socket and fires <see cref="OnStarted"/>. Returns the actual local endpoint. Use this with
    /// <see cref="TickOnce"/> to embed the server in an external loop; otherwise call <see cref="Run"/>.</summary>
    public IPEndPoint Start()
    {
        if (_running)
        {
            throw new InvalidOperationException("The server is already running.");
        }

        IPEndPoint local = _server.Start(_options.Port, _options.MaxClients, _options.Identity);
        _running = true;
        Guard(OnStarted, "OnStarted");
        return local;
    }

    /// <summary>Runs one iteration: pumps the network (firing connection and message hooks) and calls
    /// <see cref="OnTick"/>. For embedding in an external loop or in tests.</summary>
    public void TickOnce(float deltaSeconds)
    {
        long start = Stopwatch.GetTimestamp();
        _server.Tick();
        Guard(() => OnTick(deltaSeconds), "OnTick");
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        ServerTickStats before = _timings;
        _totalMs += ms;
        long ticks = before.Ticks + 1;
        Volatile.Write(ref _timings, before with
        {
            Ticks = ticks,
            LastMs = ms,
            AverageMs = _totalMs / ticks,
            MaxMs = Math.Max(before.MaxMs, ms),
            OverBudget = before.OverBudget + (ms > before.BudgetMs ? 1 : 0),
        });
    }

    /// <summary>Runs the fixed-rate loop until <paramref name="cancellationToken"/> is cancelled, then stops.</summary>
    public void Run(CancellationToken cancellationToken = default)
    {
        if (!_running)
        {
            Start();
        }

        double tickSeconds = 1.0 / _options.TickRate;
        var clock = Stopwatch.StartNew();
        double nextDue = clock.Elapsed.TotalSeconds;

        while (!cancellationToken.IsCancellationRequested)
        {
            TickOnce((float)tickSeconds);

            nextDue += tickSeconds;
            double remaining = nextDue - clock.Elapsed.TotalSeconds;
            if (remaining <= 0)
            {
                nextDue = clock.Elapsed.TotalSeconds; // Behind schedule: resync instead of spiralling.
                continue;
            }

            if (remaining > 0.002 && cancellationToken.WaitHandle.WaitOne((int)((remaining - 0.002) * 1000)))
            {
                break; // Cancelled during the wait.
            }

            while (clock.Elapsed.TotalSeconds < nextDue && !cancellationToken.IsCancellationRequested)
            {
                Thread.SpinWait(64);
            }
        }

        Stop();
    }

    /// <summary>Stops the server and fires <see cref="OnStopping"/>.</summary>
    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        Guard(OnStopping, "OnStopping");
        _server.Stop();
        _running = false;
    }

    /// <summary>Sends a message to one client.</summary>
    protected void Send(uint clientId, Message message, DeliveryMethod method = DeliveryMethod.Reliable) =>
        _server.Send(clientId, message, method);

    /// <summary>Sends a message to every connected client.</summary>
    protected void SendToAll(Message message, DeliveryMethod method = DeliveryMethod.Reliable) =>
        _server.SendToAll(message, method);

    /// <summary>Disconnects a client.</summary>
    protected void Kick(uint clientId) => _server.Disconnect(clientId);

    /// <summary>Called once after the socket is bound.</summary>
    protected virtual void OnStarted()
    {
    }

    /// <summary>Called once as the server stops.</summary>
    protected virtual void OnStopping()
    {
    }

    /// <summary>Called when a client finishes connecting.</summary>
    protected virtual void OnClientConnected(uint clientId)
    {
    }

    /// <summary>Called when a client disconnects.</summary>
    protected virtual void OnClientDisconnected(uint clientId, DisconnectReason reason)
    {
    }

    /// <summary>Called for each message from a client. The <paramref name="message"/> is valid only for this call.</summary>
    protected virtual void OnMessage(uint clientId, Message message)
    {
    }

    /// <summary>Called once per tick with the seconds since the previous tick.</summary>
    protected virtual void OnTick(float deltaSeconds)
    {
    }

    private void Guard(Action action, string hook)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogHookFailed(Logger, hook, ex);
        }
    }

    public void Dispose()
    {
        Stop();
        _server.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly Action<ILogger, string, Exception> HookFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(9130, nameof(LogHookFailed)),
            "A game-server hook '{Hook}' threw; the server kept running");

    private static void LogHookFailed(ILogger logger, string hook, Exception exception) => HookFailed(logger, hook, exception);
}
