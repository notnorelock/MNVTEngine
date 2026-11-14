using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Versioning;

/// <summary>One build attempt in the build state.</summary>
/// <param name="Result">"started", "succeeded", "failed" or "canceled" (a build that never reported stays "started").</param>
public sealed record BuildAttempt(Guid BuildId, string Version, string Scope, int Number, string Kind, DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc, string Result);

/// <summary>
/// Build numbers of the local build state (<c>artifacts/build-state</c>, ignored by Git). Allocation holds an exclusive
/// lock on the state, so builds in parallel processes never get the same number in one scope.
/// </summary>
public sealed class BuildCounter(string stateDirectory)
{
    public const string StateFile = "build-counter.json";
    public const int KeptAttempts = 200;
    public static readonly IReadOnlyList<string> Results = ["succeeded", "failed", "canceled"];
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(1);

    public string StateDirectory { get; } = Path.GetFullPath(stateDirectory);

    /// <summary>The default state folder of an engine tree; <c>MNVT_BUILD_STATE_DIR</c> overrides it.</summary>
    public static string DefaultDirectory(string engineRoot, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment("MNVT_BUILD_STATE_DIR") is { Length: > 0 } configured ? configured : Path.Combine(engineRoot, "artifacts", "build-state");
    }

    /// <summary>The next number of <paramref name="scope"/>, recorded as a started attempt.</summary>
    /// <exception cref="InvalidOperationException">The scope ran out of numbers.</exception>
    /// <exception cref="InvalidDataException">The state file is damaged (it is not reset: that could hand out numbers twice).</exception>
    public int Allocate(string scope, Guid buildId, string kind, Func<int, string> version, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(version);
        return Update(state =>
        {
            var counters = (JObject)(state["counters"] ??= new JObject());
            var number = ((int?)counters[scope] ?? 0) + 1;
            if (number > VersionConfig.MaxComponent)
            {
                throw new InvalidOperationException($"Build numbers of '{scope}' are used up ({VersionConfig.MaxComponent}); use the Global counter scope or a new version.");
            }

            counters[scope] = number;
            var attempts = (JArray)(state["builds"] ??= new JArray());
            attempts.Add(JObject.FromObject(new BuildAttempt(buildId, version(number), scope, number, kind, now, null, "started"), Serializer));
            while (attempts.Count > KeptAttempts)
            {
                attempts.RemoveAt(0);
            }

            return number;
        });
    }

    /// <summary>Records how a started build ended. False when the state does not know that build.</summary>
    public bool Complete(Guid buildId, string result, DateTimeOffset now)
    {
        if (!Results.Contains(result, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Result must be {string.Join(", ", Results)}.", nameof(result));
        }

        return Update(state =>
        {
            var attempt = (state["builds"] as JArray)?.OfType<JObject>().LastOrDefault(b => (Guid?)b["buildId"] == buildId);
            if (attempt is null)
            {
                return false;
            }

            attempt["result"] = result;
            attempt["finishedUtc"] = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            return true;
        });
    }

    public IReadOnlyList<BuildAttempt> Attempts() =>
        Update(state => (state["builds"] as JArray)?.Select(b => b.ToObject<BuildAttempt>(Serializer)!).ToList() ?? [], write: false);

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        DateParseHandling = DateParseHandling.DateTimeOffset,
    });

    private T Update<T>(Func<JObject, T> change, bool write = true)
    {
        Directory.CreateDirectory(StateDirectory);
        var path = Path.Combine(StateDirectory, StateFile);
        using var gate = AcquireLock(Path.Combine(StateDirectory, StateFile + ".lock"));
        JObject state;
        try
        {
            state = File.Exists(path)
                ? JObject.Parse(File.ReadAllText(path), new JsonLoadSettings()) is { } read && (int?)read["schemaVersion"] == 1 ? read : throw new InvalidDataException("unknown schema")
                : new JObject { ["schemaVersion"] = 1 };
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidCastException or FormatException)
        {
            throw new InvalidDataException($"The build state {path} is damaged ({ex.Message}). Fix it, or delete it to restart the build numbers.", ex);
        }

        var result = change(state);
        if (write)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, state.ToString(Formatting.Indented));
            File.Move(temporary, path, overwrite: true);
        }

        return result;
    }

    /// <summary>An exclusive handle on the lock file; other processes wait for it (up to a minute).</summary>
    private static FileStream AcquireLock(string path)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(Random.Shared.Next(5, 25));
            }
        }
    }
}
