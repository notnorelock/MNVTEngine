using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MNVTEngine.BrowserHosting;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Browser;

/// <summary>Settings of the browser host process (all browsers of a game share it).</summary>
public sealed record BrowserServiceOptions
{
    /// <summary>MNVTBrowserHost.exe; null finds it (see <see cref="BrowserService.FindHost"/>).</summary>
    public string? HostPath { get; init; }

    /// <summary>A folder for cookies, local storage and the HTTP cache; null keeps them in memory (a temporary folder deleted at exit).</summary>
    public string? CachePath { get; init; }

    /// <summary>Replaces Chromium's user agent string for every browser.</summary>
    public string? UserAgent { get; init; }

    public string Locale { get; init; } = "en-US";

    /// <summary>Let Chromium use the GPU (WebGL, video decoding, compositing) before the pixels are copied back for the game.</summary>
    public bool Gpu { get; init; } = true;

    /// <summary>Chrome DevTools at http://localhost:port (development only; 0 is off).</summary>
    public int RemoteDebuggingPort { get; init; }
}

public enum BrowserHostState
{
    NotStarted,
    Starting,
    Running,

    /// <summary>The host stopped (crashed or was killed); the next browser created or reloaded starts it again.</summary>
    Exited,

    /// <summary>The host executable is missing or did not start.</summary>
    Failed,
}

/// <summary>
/// Every embedded browser of the game, and the connection to <c>MNVTBrowserHost.exe</c>, which starts with the first
/// browser. A scene service: <c>scene.GetService&lt;BrowserService&gt;()</c>. <see cref="Update"/> runs once a frame on
/// the game thread (the browser module's system) and raises the browsers' events.
/// </summary>
public sealed partial class BrowserService : IDisposable
{
    /// <summary>Largest file <c>mnvt://game/</c> serves (base64 must fit a protocol message).</summary>
    public const int MaxResourceBytes = 12 * 1024 * 1024;

    private readonly ILogger _logger;
    private readonly BrowserServiceOptions _options;
    private readonly Dictionary<int, BrowserInstance> _browsers = [];
    private readonly ConcurrentQueue<JObject> _incoming = new();
    private Channel<JObject>? _outgoing;
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private CancellationTokenSource? _connection;
    private int _nextId;
    private bool _disposed;

    public BrowserService(ILogger logger, BrowserServiceOptions? options = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new BrowserServiceOptions();
    }

    public BrowserHostState HostState { get; private set; }

    /// <summary>Why the host failed or exited.</summary>
    public string? HostError { get; private set; }

    public int? HostProcessId { get; private set; }

    public string? ChromiumVersion { get; private set; }

    public string? CefVersion { get; private set; }

    public IReadOnlyCollection<BrowserInstance> Browsers => _browsers.Values;

    /// <summary>Reads a file of the game's assets by path relative to Assets (<c>Web/index.html</c>) for <c>mnvt://game/</c>; null when missing.</summary>
    public Func<string, byte[]?>? ResourceReader { get; set; }

    /// <summary>Upload statistics of the last frame (set by the texture pass).</summary>
    public (int Uploads, long Bytes, double Milliseconds) LastUploads { get; internal set; }

    internal bool IsHostRunning => HostState is BrowserHostState.Running or BrowserHostState.Starting;

    /// <summary>Opens a browser (and starts the browser host if needed). Dispose the instance to close it.</summary>
    public BrowserInstance Create(BrowserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var browser = new BrowserInstance(this, ++_nextId, options);
        lock (_browsers)
        {
            _browsers.Add(browser.Id, browser);
        }

        Recreate(browser);
        return browser;
    }

    /// <summary>Delivers the host's messages: browsers' events, script results, resource requests. Game thread, once a frame.</summary>
    public void Update()
    {
        while (_incoming.TryDequeue(out var message))
        {
            switch (message.Value<string>("type"))
            {
                case BrowserProtocol.Ready:
                    (HostState, CefVersion, ChromiumVersion, HostProcessId) = (BrowserHostState.Running, message.Value<string>("cef"), message.Value<string>("chromium"), message.Value<int?>("processId"));
                    LogHostReady(_logger, ChromiumVersion ?? "?", HostProcessId ?? 0);
                    break;
                case BrowserProtocol.Resource:
                    AnswerResource(message);
                    break;
                case HostLost:
                    OnHostLost(message.Value<string>("reason") ?? "The browser host stopped.", message.Value<bool>("failed"));
                    break;
                default:
                    if (_browsers.TryGetValue(message.Value<int>("browser"), out var browser))
                    {
                        if (message.Value<string>("type") == BrowserProtocol.Console)
                        {
                            var (level, text) = (message.Value<string>("level") ?? "", message.Value<string>("message") ?? "");
                            LogConsole(_logger, browser.Id, level, text);
                        }

                        browser.Dispatch(message);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// The host executable: <c>MNVT_BROWSER_HOST</c>, then next to the game's executable (exports), then the engine's
    /// build output (development: the editor, the Player, tests).
    /// </summary>
    public static string? FindHost()
    {
        if (Environment.GetEnvironmentVariable("MNVT_BROWSER_HOST") is { Length: > 0 } configured)
        {
            return File.Exists(configured) ? configured : null;
        }

        if (Path.Combine(AppContext.BaseDirectory, BrowserProtocol.HostFileName) is var exported && File.Exists(exported))
        {
            return exported;
        }

        var roots = new List<string>();
        if (Environment.GetEnvironmentVariable("MNVT_ENGINE_ROOT") is { Length: > 0 } engineRoot)
        {
            roots.Add(engineRoot);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            roots.Add(directory.FullName);
        }

        return roots
            .SelectMany(root => Configurations.Select(configuration => Path.Combine(root, "src", "Engine", "MNVTEngine.Browser.Host", "bin", configuration, "net11.0-windows", "win-x64", BrowserProtocol.HostFileName)))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var browser in _browsers.Values.ToList())
        {
            browser.Dispose();
        }

        StopHost(graceful: true);
    }

    internal void Send(JObject message)
    {
        if (_outgoing is null || !_outgoing.Writer.TryWrite(message))
        {
            // No host: the command is dropped; the browser is Crashed or Failed and says so.
        }
    }

    internal void Close(BrowserInstance browser)
    {
        bool removed;
        lock (_browsers)
        {
            removed = _browsers.Remove(browser.Id);
        }

        if (removed)
        {
            Send(BrowserProtocol.New(BrowserProtocol.Close, browser.Id));
        }
    }

    /// <summary>Creates the browser in the host (starting the host when it is not running).</summary>
    internal void Recreate(BrowserInstance browser)
    {
        if (!IsHostRunning)
        {
            StartHost();
        }

        if (HostState == BrowserHostState.Failed)
        {
            browser.OnHostLost(HostError ?? "The browser host did not start.", failed: true);
            return;
        }

        browser.OnCreating();
        var options = browser.Options;
        var create = BrowserProtocol.New(BrowserProtocol.Create, browser.Id);
        if (options.Html is { } html && browser.Url.Length == 0)
        {
            create["html"] = html;
        }
        else
        {
            create["url"] = browser.Url;
        }

        (create["width"], create["height"], create["transparent"], create["frameRate"]) = (browser.Width, browser.Height, options.Transparent, browser.FrameRate);
        (create["profile"], create["domains"], create["capabilities"], create["muted"]) = (options.Profile.ToString(), new JArray(options.AllowedDomains), (int)options.Capabilities, browser.Muted);
        Send(create);
    }

    internal void LogRequestFailed(int browser, string request, Exception exception) => LogRequestHandlerFailed(_logger, exception, browser, request);

    internal void LogCommandFailed(int browser, string error) => LogHostCommandFailed(_logger, browser, error);

    private const string HostLost = "__hostLost";

    private static readonly string[] Configurations = ["Release", "Debug"];

    private void StartHost()
    {
        StopHost(graceful: false);
        if (!OperatingSystem.IsWindows())
        {
            Fail("The embedded browser runs on Windows only.");
            return;
        }

        var hostPath = _options.HostPath ?? FindHost();
        if (hostPath is null || !File.Exists(hostPath))
        {
            Fail($"{BrowserProtocol.HostFileName} was not found (looked next to the game and in the engine's build output). Build src/Engine/MNVTEngine.Browser.Host, or set MNVT_BROWSER_HOST.");
            return;
        }

        var session = Guid.NewGuid().ToString("N")[..12];
        var pipeName = $"mnvt-browser-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{session}";
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BrowserProtocol.PipeBufferBytes, BrowserProtocol.PipeBufferBytes);
        _outgoing = Channel.CreateUnbounded<JObject>(new UnboundedChannelOptions { SingleReader = true });
        _connection = new CancellationTokenSource();
        var start = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!,
        };
        foreach (var argument in new[] { BrowserProtocol.PipeArgument, pipeName, BrowserProtocol.ParentArgument, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            _process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned no process.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Fail($"{hostPath} did not start: {ex.Message}");
            return;
        }

        var process = _process;
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                LogHostOutput(_logger, e.Data);
            }
        };
        process.BeginErrorReadLine();
        HostState = BrowserHostState.Starting;
        HostError = null;
        LogHostStarting(_logger, hostPath);
        _ = RunConnectionAsync(_pipe, _outgoing, process, session, _connection.Token);
    }

    private async Task RunConnectionAsync(NamedPipeServerStream pipe, Channel<JObject> outgoing, Process process, string session, CancellationToken cancel)
    {
        string reason;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            }

            var hello = BrowserProtocol.New(BrowserProtocol.Hello);
            (hello["version"], hello["session"], hello["locale"], hello["gpu"]) = (BrowserProtocol.Version, session, _options.Locale, _options.Gpu);
            (hello["cachePath"], hello["userAgent"], hello["remoteDebuggingPort"]) = (_options.CachePath, _options.UserAgent, _options.RemoteDebuggingPort);
            await BrowserProtocol.WriteAsync(pipe, hello, cancel).ConfigureAwait(false);
            var writer = WriteLoopAsync(pipe, outgoing, cancel);
            while (await BrowserProtocol.ReadAsync(pipe, cancel).ConfigureAwait(false) is { } message)
            {
                if (message.Value<string>("type") == BrowserProtocol.Surface)
                {
                    // Straight to the browser: the texture pass may run before the next Update.
                    if (Browser(message.Value<int>("browser")) is { } browser)
                    {
                        browser.OnSurface(message.Value<string>("name") ?? "", message.Value<int>("width"), message.Value<int>("height"));
                    }

                    continue;
                }

                _incoming.Enqueue(message);
            }

            outgoing.Writer.TryComplete();
            await writer.ConfigureAwait(false);
            reason = process.WaitForExit(2000) ? $"The browser host exited (code {process.ExitCode})." : "The browser host closed the connection.";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return; // the game stopped the host
        }
        catch (OperationCanceledException)
        {
            reason = "The browser host did not connect within 30 seconds.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or Newtonsoft.Json.JsonException)
        {
            reason = $"The connection to the browser host failed: {ex.Message}";
        }

        var lost = new JObject { ["type"] = HostLost, ["reason"] = reason };
        _incoming.Enqueue(lost);
    }

    private static async Task WriteLoopAsync(NamedPipeServerStream pipe, Channel<JObject> outgoing, CancellationToken cancel)
    {
        try
        {
            await foreach (var message in outgoing.Reader.ReadAllAsync(cancel).ConfigureAwait(false))
            {
                await BrowserProtocol.WriteAsync(pipe, message, cancel).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
        {
            // The reader sees the broken pipe too and reports the host as lost.
        }
    }

    private BrowserInstance? Browser(int id)
    {
        lock (_browsers)
        {
            return _browsers.GetValueOrDefault(id);
        }
    }

    private void AnswerResource(JObject request)
    {
        var path = request.Value<string>("path") ?? "";
        byte[]? data = null;
        try
        {
            data = ResourceReader?.Invoke(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
        {
            LogResourceFailed(_logger, ex, path);
        }

        if (data is { Length: > MaxResourceBytes })
        {
            LogResourceTooLarge(_logger, path, data.Length);
            data = null;
        }

        var answer = BrowserProtocol.New(BrowserProtocol.ResourceResult);
        (answer["id"], answer["ok"]) = (request.Value<int>("id"), data is not null);
        if (data is not null)
        {
            answer["data"] = Convert.ToBase64String(data);
        }
        else
        {
            LogResourceMissing(_logger, path);
        }

        Send(answer);
    }

    private void OnHostLost(string reason, bool failed)
    {
        if (_disposed || HostState is BrowserHostState.Failed or BrowserHostState.Exited or BrowserHostState.NotStarted)
        {
            return;
        }

        HostState = failed ? BrowserHostState.Failed : BrowserHostState.Exited;
        HostError = reason;
        LogHostLost(_logger, reason);
        StopHost(graceful: false);
        foreach (var browser in _browsers.Values.ToList())
        {
            browser.OnHostLost(reason, failed);
        }
    }

    private void Fail(string reason)
    {
        HostState = BrowserHostState.Failed;
        HostError = reason;
        LogHostFailed(_logger, reason);
    }

    private void StopHost(bool graceful)
    {
        var (process, pipe, outgoing, connection) = (_process, _pipe, _outgoing, _connection);
        (_process, _pipe, _outgoing, _connection) = (null, null, null, null);
        if (graceful && outgoing is not null && pipe is { IsConnected: true })
        {
            outgoing.Writer.TryWrite(BrowserProtocol.New(BrowserProtocol.Shutdown));
            outgoing.Writer.TryComplete();
            process?.WaitForExit(3000);
        }

        outgoing?.Writer.TryComplete();
        connection?.Cancel();
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // It exited meanwhile.
            }

            process.Dispose();
        }

        pipe?.Dispose();
        connection?.Dispose();
        if (HostState is BrowserHostState.Running or BrowserHostState.Starting)
        {
            HostState = BrowserHostState.NotStarted;
        }
    }

    [LoggerMessage(EventId = 2600, Level = LogLevel.Information, Message = "Starting the browser host {Path}")]
    private static partial void LogHostStarting(ILogger logger, string path);

    [LoggerMessage(EventId = 2601, Level = LogLevel.Information, Message = "Browser host ready: Chromium {Chromium}, process {ProcessId}")]
    private static partial void LogHostReady(ILogger logger, string chromium, int processId);

    [LoggerMessage(EventId = 2602, Level = LogLevel.Error, Message = "Embedded browser unavailable: {Reason}")]
    private static partial void LogHostFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 2603, Level = LogLevel.Warning, Message = "Browser host lost: {Reason}")]
    private static partial void LogHostLost(ILogger logger, string reason);

    [LoggerMessage(EventId = 2604, Level = LogLevel.Debug, Message = "Browser host: {Line}")]
    private static partial void LogHostOutput(ILogger logger, string line);

    [LoggerMessage(EventId = 2605, Level = LogLevel.Debug, Message = "Browser {Browser} console [{Level}]: {Message}")]
    private static partial void LogConsole(ILogger logger, int browser, string level, string message);

    [LoggerMessage(EventId = 2606, Level = LogLevel.Warning, Message = "mnvt://game/ could not read {Path}")]
    private static partial void LogResourceFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 2607, Level = LogLevel.Warning, Message = "mnvt://game/ file {Path} is {Bytes} bytes, more than the 12 MB a page file may have")]
    private static partial void LogResourceTooLarge(ILogger logger, string path, int bytes);

    [LoggerMessage(EventId = 2608, Level = LogLevel.Warning, Message = "mnvt://game/ has no file {Path} (a page asked for it; game files live in Assets/Web)")]
    private static partial void LogResourceMissing(ILogger logger, string path);

    [LoggerMessage(EventId = 2609, Level = LogLevel.Error, Message = "Browser {Browser}: request handler '{Request}' failed")]
    private static partial void LogRequestHandlerFailed(ILogger logger, Exception exception, int browser, string request);

    [LoggerMessage(EventId = 2610, Level = LogLevel.Warning, Message = "Browser {Browser}: {Error}")]
    private static partial void LogHostCommandFailed(ILogger logger, int browser, string error);
}
