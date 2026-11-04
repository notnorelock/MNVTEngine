using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using MNVTEngine.Assets;
using MNVTEngine.Graphics;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Runtime.Loading;

/// <summary>
/// Loads a scene over several frames so the window keeps responding and a loading screen can show real progress:
/// <list type="number">
/// <item><b>On a worker:</b> read the scene file and find the textures it references; decode each image.</item>
/// <item><b>On the game thread, a few milliseconds per frame:</b> create the GPU textures from the decoded images (the
/// device queues the uploads, no GPU wait), then deserialize the scene.</item>
/// </list>
/// Progress is completed steps over known steps: reading, one decode and one upload per texture, and deserializing.
/// <see cref="IsIndeterminate"/> stays true until the reference scan is done, because the total is unknown before that.
/// Worker threads never touch the graphics device or the scene.
/// </summary>
public sealed partial class SceneLoadOperation : IDisposable
{
    private readonly AssetDatabase _assets;
    private readonly ResourceCache _resources;
    private readonly Func<string, string, Scene> _deserialize;
    private readonly CancellationTokenSource _cancel = new();
    private readonly ConcurrentQueue<(Guid Guid, Image Image)> _decoded = new();
    private readonly ConcurrentQueue<Exception> _workerErrors = new();
    private readonly Task<string> _read;
    private int _textures = -1;
    private int _decodedCount;
    private int _uploaded;
    private int _failed;
    private bool _deserialized;

    /// <param name="deserialize">Turns (json, path) into a scene on the game thread; may throw <see cref="SerializationException"/>.</param>
    public SceneLoadOperation(string path, AssetDatabase assets, ResourceCache resources, Func<string, string, Scene> deserialize)
    {
        Path = path;
        _assets = assets;
        _resources = resources;
        _deserialize = deserialize;
        CurrentTask = $"Reading {path}";
        var token = _cancel.Token;
        _read = Task.Run(() => ReadAndDecode(token), token);
    }

    public string Path { get; }

    /// <summary>Human-readable step in progress ("Decoding Textures/hero.png").</summary>
    public string CurrentTask { get; private set; }

    /// <summary>True until the number of steps is known.</summary>
    public bool IsIndeterminate => Volatile.Read(ref _textures) < 0;

    /// <summary>0..1 from completed steps; 0 while indeterminate.</summary>
    public float Progress
    {
        get
        {
            var textures = Volatile.Read(ref _textures);
            return textures < 0 ? 0f : (1f + Volatile.Read(ref _decodedCount) + _uploaded + Volatile.Read(ref _failed) + (_deserialized ? 1f : 0f)) / (2f + (2f * textures));
        }
    }

    public bool IsDone => Result is not null || Error is not null || IsCancelled;

    public bool IsCancelled => _cancel.IsCancellationRequested;

    public Scene? Result { get; private set; }

    /// <summary>Why loading failed: unreadable file, invalid scene, or a texture that could not be decoded is only logged (it renders as missing).</summary>
    public Exception? Error { get; private set; }

    /// <summary>Does game-thread work for up to <paramref name="budget"/>; call once per frame until <see cref="IsDone"/>.</summary>
    public void Step(TimeSpan budget)
    {
        if (IsDone)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        while (_decoded.TryDequeue(out var item))
        {
            CurrentTask = $"Uploading {_assets.Get(item.Guid)?.Path}";
            _resources.AddDecodedTexture(item.Guid, item.Image);
            _uploaded++;
            if (watch.Elapsed >= budget)
            {
                return;
            }
        }

        if (_read.IsFaulted)
        {
            Error = _read.Exception!.InnerException;
            return;
        }

        if (!_read.IsCompletedSuccessfully || _uploaded + Volatile.Read(ref _failed) < _textures)
        {
            return;
        }

        CurrentTask = $"Building {Path}";
        try
        {
            Result = _deserialize(_read.Result, Path);
            _deserialized = true;
            CurrentTask = "Starting";
        }
        catch (SerializationException ex)
        {
            Error = ex;
        }
    }

    /// <summary>Errors of texture decoding (logged by the caller; the textures render as missing).</summary>
    public IEnumerable<Exception> TextureErrors => _workerErrors;

    /// <summary>Stops the workers; a cancelled operation never produces a scene.</summary>
    public void Cancel() => _cancel.Cancel();

    public void Dispose()
    {
        _cancel.Cancel();
        _cancel.Dispose();
    }

    private string ReadAndDecode(CancellationToken token)
    {
        string json;
        try
        {
            json = System.Text.Encoding.UTF8.GetString(_assets.ReadAllBytes(Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            throw new SerializationException(Path, ex.Message, [], ex);
        }

        var textures = AssetReference().Matches(json)
            .Select(m => Guid.TryParse(m.Groups[1].Value, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty && _assets.GetAssetType(g) == AssetTypes.Texture && !_resources.HasTexture(g))
            .Distinct()
            .ToList();
        Volatile.Write(ref _textures, textures.Count);
        Parallel.ForEach(textures, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, guid =>
        {
            CurrentTask = $"Decoding {_assets.Get(guid)?.Path}";
            try
            {
                _decoded.Enqueue((guid, Image.Decode(_assets.ReadAllBytes(guid), _assets.Get(guid)?.Path ?? guid.ToString())));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _workerErrors.Enqueue(ex); // the texture is created lazily (as missing) like any other failed texture
                Interlocked.Increment(ref _failed);
            }

            Interlocked.Increment(ref _decodedCount);
        });
        return json;
    }

    [GeneratedRegex("\"asset:([0-9a-fA-F-]{36})\"")]
    private static partial Regex AssetReference();
}
