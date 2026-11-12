using System.Diagnostics;
using System.IO.Pipes;
using CefSharp;
using CefSharp.OffScreen;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.BrowserHosting.Host;

/// <summary>
/// The browser host process. Started by the game with <c>--pipe name --parent pid</c>: connects to the game's pipe,
/// waits for <c>hello</c>, initializes CEF, then serves commands until the game says <c>shutdown</c>, closes the pipe
/// or exits. Diagnostics go to stderr, which the game reads into its log.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        HostLayout.Install();
        return Run(args);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] // CefSharp loads only after HostLayout.Install
    private static int Run(string[] args)
    {
        // CEF starts its renderer, GPU and utility processes from this executable: they never return from here.
        var subprocess = CefSharp.BrowserSubprocess.SelfHost.Main(args);
        if (subprocess >= 0)
        {
            return subprocess;
        }

        var pipeName = Argument(args, BrowserProtocol.PipeArgument);
        if (pipeName is null)
        {
            Console.Error.WriteLine($"MNVTBrowserHost is started by games with the browser module ({BrowserProtocol.PipeArgument} <name> missing).");
            return 2;
        }

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(10_000);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"MNVTBrowserHost: the game's pipe '{pipeName}' did not accept the connection.");
            return 3;
        }

        using var stop = new CancellationTokenSource();
        if (int.TryParse(Argument(args, BrowserProtocol.ParentArgument), out var parentId))
        {
            WatchParent(parentId, stop);
        }

        // Cef.Initialize and Cef.Shutdown must run on the same thread: both happen here, on the main thread.
        var host = new BrowserHost(pipe, stop.Token);
        var hello = BrowserProtocol.ReadAsync(pipe, stop.Token).GetAwaiter().GetResult();
        if (hello?.Value<string>("type") != BrowserProtocol.Hello || hello.Value<int>("version") != BrowserProtocol.Version)
        {
            Console.Error.WriteLine($"MNVTBrowserHost: expected hello with protocol version {BrowserProtocol.Version}.");
            return 4;
        }

        var cacheRoot = hello.Value<string>("cachePath") is { Length: > 0 } persistent ? persistent : Path.Combine(Path.GetTempPath(), "MNVTBrowser", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!Cef.Initialize(CreateSettings(hello, cacheRoot, host), performDependencyCheck: HostLayout.ChromiumDirectory is null, browserProcessHandler: null))
        {
            Console.Error.WriteLine("MNVTBrowserHost: CEF failed to initialize (see cef.log in the browser cache folder).");
            return 5;
        }

        var exitCode = 0;
        try
        {
            host.RunAsync(hello).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"MNVTBrowserHost: connection to the game lost: {ex.Message}");
            exitCode = 6;
        }
        finally
        {
            host.CloseAll();
            Cef.Shutdown();
            if (hello.Value<string>("cachePath") is not { Length: > 0 })
            {
                DeleteTemporaryCache(cacheRoot);
            }
        }

        return exitCode;
    }

    private static CefSettings CreateSettings(JObject hello, string cacheRoot, BrowserHost host)
    {
        var settings = new CefSettings
        {
            WindowlessRenderingEnabled = true,
            MultiThreadedMessageLoop = true,
            BrowserSubprocessPath = Environment.ProcessPath,
            RootCachePath = cacheRoot,
            CachePath = cacheRoot,
            LogFile = Path.Combine(cacheRoot, "cef.log"),
            LogSeverity = LogSeverity.Warning,
            Locale = hello.Value<string>("locale") is { Length: > 0 } locale ? locale : "en-US",
            PersistSessionCookies = false,
        };
        if (HostLayout.ChromiumDirectory is { } chromium)
        {
            settings.ResourcesDirPath = chromium;
            settings.LocalesDirPath = Path.Combine(chromium, "locales");
        }

        if (hello.Value<string>("userAgent") is { Length: > 0 } userAgent)
        {
            settings.UserAgent = userAgent;
        }

        if (hello.Value<int?>("remoteDebuggingPort") is int port and > 0)
        {
            settings.RemoteDebuggingPort = port;
        }

        if (hello.Value<bool?>("gpu") == false)
        {
            settings.CefCommandLineArgs["disable-gpu"] = "1";
            settings.CefCommandLineArgs["disable-gpu-compositing"] = "1";
        }

        // Game pages may play sound without a click first; the game mutes browsers it wants silent.
        settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
        settings.EnableAudio(); // CefSharp.OffScreen adds --mute-audio; page audio goes to the game through GameAudioHandler
        settings.RegisterScheme(new CefCustomScheme
        {
            SchemeName = BrowserSecurityPolicy.GameScheme,
            SchemeHandlerFactory = new GameSchemeHandlerFactory(host),
            IsStandard = true,
            IsSecure = true,
            IsCorsEnabled = true,
            IsFetchEnabled = true,
        });
        return settings;
    }

    private static void WatchParent(int parentId, CancellationTokenSource stop)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(parentId);
        }
        catch (ArgumentException)
        {
            stop.Cancel(); // already gone
            return;
        }

        parent.EnableRaisingEvents = true;
        parent.Exited += (_, _) => stop.Cancel();
    }

    private static void DeleteTemporaryCache(string cacheRoot)
    {
        // CEF's subprocesses release their files a moment after Cef.Shutdown.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(cacheRoot, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 10)
                {
                    Console.Error.WriteLine($"MNVTBrowserHost: temporary cache {cacheRoot} not deleted: {ex.Message}");
                    return;
                }

                Thread.Sleep(200);
            }
        }
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
