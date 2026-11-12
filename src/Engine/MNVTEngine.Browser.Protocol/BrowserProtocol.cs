using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.BrowserHosting;

/// <summary>
/// Messages between the game and <c>MNVTBrowserHost.exe</c>. Each is a JSON object with <c>"type"</c>, usually
/// <c>"browser"</c> (the browser id) and, for requests that get an answer, <c>"id"</c> (the request id). The host is
/// started with <c>--pipe name</c> and connects to the game's pipe server.
/// </summary>
public static class BrowserProtocol
{
    public const int Version = 1;

    /// <summary>Largest message accepted (JavaScript results, page messages): 16 MB.</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>Pipe buffers: writes of small messages return without waiting for the other side to read.</summary>
    public const int PipeBufferBytes = 64 * 1024;

    public const string HostFileName = "MNVTBrowserHost.exe";
    public const string PipeArgument = "--pipe";
    public const string ParentArgument = "--parent";

    // Game to host.
    public const string Hello = "hello", Create = "create", Navigate = "navigate", LoadHtml = "loadHtml", Reload = "reload", Stop = "stop",
        Back = "back", Forward = "forward", Evaluate = "evaluate", Resize = "resize", Visibility = "visibility", Focus = "focus", Zoom = "zoom",
        UserAgent = "userAgent", FrameRate = "frameRate", Mute = "mute", MouseMove = "mouseMove", MouseButton = "mouseButton", MouseWheel = "mouseWheel",
        Key = "key", PostToPage = "postToPage", RequestResult = "requestResult", ResourceResult = "resourceResult", Close = "close", Shutdown = "shutdown",
        CrashRenderer = "crashRenderer";

    // Host to game.
    public const string Ready = "ready", Created = "created", Failed = "failed", LoadStarted = "loadStarted", LoadCompleted = "loadCompleted",
        LoadError = "loadError", Title = "title", Address = "address", NavigationState = "navigationState", Console = "console", Message = "message",
        Request = "request", EvaluateResult = "evaluateResult", Crashed = "crashed", Closed = "closed", Resource = "resource", Blocked = "blocked",
        Surface = "surface", Log = "log";

    private static readonly JsonSerializerSettings Settings = new() { TypeNameHandling = TypeNameHandling.None, MaxDepth = 64 };

    /// <summary>Writes one length-prefixed message. Not thread-safe: callers serialize writes.</summary>
    public static async Task WriteAsync(PipeStream pipe, JObject message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(message);
        var body = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
        if (body.Length > MaxMessageBytes)
        {
            throw new InvalidDataException($"Browser message '{message.Value<string>("type")}' is {body.Length} bytes; the limit is {MaxMessageBytes}.");
        }

        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one message; null when the other side closed the pipe.</summary>
    /// <exception cref="InvalidDataException">A malformed or oversized message.</exception>
    public static async Task<JObject?> ReadAsync(PipeStream pipe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        var header = new byte[4];
        if (!await ReadExactlyAsync(pipe, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException($"Browser message length {length} is out of range.");
        }

        var body = new byte[length];
        if (!await ReadExactlyAsync(pipe, body, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(body))) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader) is { } message && message.Value<string>("type") is not null
            ? message
            : throw new InvalidDataException("Browser message without a type.");
    }

    public static JObject New(string type, int browser = 0) => new() { ["type"] = type, ["browser"] = browser };

    public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);

    private static async Task<bool> ReadExactlyAsync(PipeStream pipe, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}
