using Microsoft.Extensions.Logging;
using MNVTEngine.Graphics;
using MNVTEngine.Serialization;

namespace MNVTEngine.Runtime;

/// <summary>Environment (sky) images: textures loaded with a full mip chain, whatever their import settings.</summary>
public sealed partial class ResourceCache
{
    private readonly Dictionary<Guid, Texture2D?> _environments = [];

    /// <summary>
    /// An environment image with mipmaps (reflections blur by sampling smaller mips); null, logged once, when the asset is
    /// not a texture or cannot be decoded. Reloaded when the asset changes.
    /// </summary>
    public Texture2D? GetEnvironmentTexture(AssetRef asset)
    {
        if (asset.IsNone)
        {
            return null;
        }

        if (_environments.TryGetValue(asset.Guid, out var cached))
        {
            return cached;
        }

        Texture2D? texture = null;
        var name = _assets.Get(asset.Guid)?.Path ?? asset.ToString();
        try
        {
            if (_assets.GetAssetType(asset.Guid) != AssetTypes.Texture)
            {
                throw new InvalidDataException($"{asset} is not a texture asset in this project.");
            }

            texture = _device.CreateTexture(Image.Decode(_assets.ReadAllBytes(asset.Guid), name), new SamplerState(TextureFilter.Linear, TextureWrap.Repeat, GenerateMipmaps: true), name + " (environment)");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
        {
            LogEnvironmentFailed(_logger, ex, name);
        }

        _environments.Add(asset.Guid, texture);
        return texture;
    }

    private void EvictEnvironments(IReadOnlyCollection<Guid> changed)
    {
        foreach (var guid in changed)
        {
            if (_environments.Remove(guid, out var texture))
            {
                texture?.Dispose();
            }
        }
    }

    private void DisposeEnvironments()
    {
        foreach (var texture in _environments.Values)
        {
            texture?.Dispose();
        }

        _environments.Clear();
    }

    [LoggerMessage(EventId = 3008, Level = LogLevel.Error, Message = "Environment image '{Asset}' could not be loaded; the scene has no environment lighting")]
    private static partial void LogEnvironmentFailed(ILogger logger, Exception exception, string asset);
}
