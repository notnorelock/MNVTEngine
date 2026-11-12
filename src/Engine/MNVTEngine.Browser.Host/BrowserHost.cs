using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading.Channels;
using CefSharp;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.BrowserHosting.Host;

/// <summary>The command loop: one game connection, any number of browsers.</summary>
internal sealed class BrowserHost(NamedPipeClientStream pipe, CancellationToken stopping)
{
    private readonly ConcurrentDictionary<int, BrowserSession> _sessions = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _resources = new();
    private readonly Channel<JObject> _outgoing = Channel.CreateUnbounded<JObject>(new UnboundedChannelOptions { SingleReader = true });
    private int _nextResource;

    public string Session { get; private set; } = "";

    /// <summary>Queues a message to the game; any thread (CEF callbacks).</summary>
    public void Send(JObject message) => _outgoing.Writer.TryWrite(message);

    public BrowserSession? FindByCefId(int cefId) => _sessions.Values.FirstOrDefault(s => s.CefId == cefId);

    public async Task RunAsync(JObject hello)
    {
        Session = hello.Value<string>("session") ?? throw new InvalidDataException("hello without a session id.");
        var writer = WriteLoopAsync();
        var ready = BrowserProtocol.New(BrowserProtocol.Ready);
        ready["cef"] = Cef.CefVersion;
        ready["chromium"] = Cef.ChromiumVersion;
        ready["cefSharp"] = Cef.CefSharpVersion;
        ready["processId"] = Environment.ProcessId;
        Send(ready);
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var message = await BrowserProtocol.ReadAsync(pipe, stopping).ConfigureAwait(false);
                if (message is null || message.Value<string>("type") == BrowserProtocol.Shutdown)
                {
                    break;
                }

                try
                {
                    Handle(message);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or InvalidCastException or Newtonsoft.Json.JsonException)
                {
                    // A bad command fails that command only; the game hears about it.
                    var failed = BrowserProtocol.New(BrowserProtocol.Failed, message.Value<int?>("browser") ?? 0);
                    failed["command"] = message.Value<string>("type");
                    failed["error"] = ex.Message;
                    Send(failed);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The game exited.
        }
        finally
        {
            _outgoing.Writer.TryComplete();
            foreach (var pending in _resources.Values)
            {
                pending.TrySetCanceled(CancellationToken.None);
            }
        }

        await writer.ConfigureAwait(false);
    }

    /// <summary>Asks the game for a file of <c>Assets/Web</c>; null when it has none or does not answer in time.</summary>
    public async Task<JObject?> RequestResourceAsync(string assetPath)
    {
        var id = Interlocked.Increment(ref _nextResource);
        var pending = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _resources[id] = pending;
        var request = BrowserProtocol.New(BrowserProtocol.Resource);
        request["id"] = id;
        request["path"] = assetPath;
        Send(request);
        try
        {
            return await pending.Task.WaitAsync(TimeSpan.FromSeconds(15), stopping).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _resources.TryRemove(id, out _);
        }
    }

    public void CloseAll()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    private void Handle(JObject message)
    {
        var type = message.Value<string>("type");
        var id = message.Value<int>("browser");
        if (type == BrowserProtocol.Create)
        {
            if (_sessions.ContainsKey(id))
            {
                throw new InvalidOperationException($"Browser {id} exists already.");
            }

            _sessions[id] = new BrowserSession(this, id, message);
            return;
        }

        if (type == BrowserProtocol.ResourceResult)
        {
            if (_resources.TryRemove(message.Value<int>("id"), out var pending))
            {
                pending.TrySetResult(message);
            }

            return;
        }

        if (!_sessions.TryGetValue(id, out var session))
        {
            return; // closed meanwhile: commands in flight are dropped
        }

        if (type == BrowserProtocol.Close)
        {
            _sessions.TryRemove(id, out _);
            session.Dispose();
            Send(BrowserProtocol.New(BrowserProtocol.Closed, id));
            return;
        }

        session.Handle(type!, message);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await BrowserProtocol.WriteAsync(pipe, message, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            Console.Error.WriteLine($"MNVTBrowserHost: writing to the game failed: {ex.Message}");
        }
    }
}
