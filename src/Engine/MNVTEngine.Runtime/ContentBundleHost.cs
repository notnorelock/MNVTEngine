// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-27.

using System.Text;
using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Bundles;
using MNVTEngine.IO.Packages;
using MNVTEngine.Scenes;

namespace MNVTEngine.Runtime;

/// <summary>
/// Loads content bundles for <see cref="ContentBundles"/>: checks the file against the game's policy, mounts it into the
/// asset database (apart from the game's assets) and instantiates its root prefab with a registry limited to the
/// components the game allows. Games get one from <see cref="GameRuntime"/>; the editor sets one while in Play Mode.
/// </summary>
public sealed partial class ContentBundleHost(AssetDatabase assets, ComponentRegistry registry, ILogger logger) : IContentBundleHost
{
    public ContentBundle Load(string file, ContentBundleLoadOptions options, PackageEncryptionKey? key = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var bundle = ContentBundleFile.Open(file, options.Policy, key);
        BundleMount mount;
        try
        {
            mount = assets.MountBundle(bundle);
        }
        catch
        {
            bundle.Dispose();
            throw;
        }

        LogMounted(logger, bundle.Manifest.Name, bundle.Manifest.Kind, bundle.Assets.Count, mount.Folder);
        var serializer = new SceneSerializer(registry.Where(options.AllowComponent), assets);
        var rootPath = $"{mount.Folder}/{bundle.Root.Path}";
        return new ContentBundle(bundle.Manifest, (scene, parent) =>
        {
            var json = Encoding.UTF8.GetString(assets.ReadAllBytes(bundle.Manifest.Root));
            var instance = serializer.InstantiatePrefab(json, scene, parent, rootPath);
            return new ContentInstance(instance.Root, [.. instance.Diagnostics.Select(d => d.ToString())]);
        }, () =>
        {
            assets.UnmountBundle(mount);
            LogUnmounted(logger, bundle.Manifest.Name);
        });
    }

    [LoggerMessage(EventId = 3120, Level = LogLevel.Information, Message = "Content bundle '{Name}' ({Kind}, {Assets} assets) loaded at {Folder}")]
    private static partial void LogMounted(ILogger logger, string name, string kind, int assets, string folder);

    [LoggerMessage(EventId = 3121, Level = LogLevel.Information, Message = "Content bundle '{Name}' unloaded")]
    private static partial void LogUnmounted(ILogger logger, string name);
}
