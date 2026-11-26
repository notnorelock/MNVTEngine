// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using MNVTEngine.Bundles;

namespace MNVTEngine;

/// <summary>How gameplay code loads a bundle: what the bundle may hold, and which components its prefabs may create.</summary>
/// <param name="AllowComponent">
/// Component types the bundle's prefabs may create (besides <see cref="Transform"/>); any other component in them is
/// kept as an inert <see cref="Scenes.MissingComponent"/> and reported. Give only components that are plain data or
/// presentation (sprites, tilemaps, the game's own descriptors), never ones that act for the player.
/// </param>
public sealed record ContentBundleLoadOptions(ContentBundlePolicy Policy, Func<Type, bool> AllowComponent);

/// <summary>A copy of a bundle's root prefab in a scene, and what loading it reported (components left out, missing references).</summary>
public sealed record ContentInstance(GameObject Root, IReadOnlyList<string> Problems);

/// <summary>Implemented by the game host; gameplay code uses <see cref="ContentBundles"/>.</summary>
public interface IContentBundleHost
{
    /// <exception cref="ContentBundleException">Not a bundle the options accept, or it lists assets already loaded.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    ContentBundle Load(string file, ContentBundleLoadOptions options, MNVTEngine.IO.Packages.PackageEncryptionKey? key = null);
}

/// <summary>
/// A loaded content bundle (<c>.mnvtbundle</c>, see docs/architecture/content-bundles.md). Its assets load by GUID like
/// the game's until it is disposed; destroy its instances first, or they keep only what was already loaded.
/// </summary>
public sealed class ContentBundle(ContentBundleManifest manifest, Func<Scene, Transform?, ContentInstance> instantiate, Action unload) : IDisposable
{
    private bool _disposed;

    public ContentBundleManifest Manifest { get; } = manifest ?? throw new ArgumentNullException(nameof(manifest));

    /// <summary>The prefab the bundle's content starts from.</summary>
    public AssetRef Root => new(Manifest.Root);

    /// <summary>Adds a copy of the root prefab to <paramref name="scene"/>, with only the components the load options allow.</summary>
    /// <exception cref="ObjectDisposedException">The bundle was unloaded.</exception>
    /// <exception cref="Serialization.SerializationException">The prefab is unreadable.</exception>
    public ContentInstance Instantiate(Scene scene, Transform? parent = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        return instantiate(scene, parent);
    }

    /// <summary>Unloads the bundle: its assets are no longer found.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            unload();
        }
    }
}

/// <summary>
/// Content bundles for gameplay code: worlds, avatars and other content made by players or creators, packaged with
/// <c>mnvt package bundle</c> and loaded at run time from a file the game downloaded. Bundles are untrusted: they carry
/// no code, cannot replace the game's assets, and their prefabs create only the components the game allows.
/// </summary>
public static class ContentBundles
{
    private static IContentBundleHost? _host;

    /// <summary>Loads a bundle file; an encrypted one with its <paramref name="key"/> (it stays encrypted on disk and is read through the key).</summary>
    /// <exception cref="ContentBundleException">Not a bundle the options accept (every problem is listed), the key does not open it, or it lists assets already loaded.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="InvalidOperationException">No game is running.</exception>
    public static ContentBundle Load(string file, ContentBundleLoadOptions options, MNVTEngine.IO.Packages.PackageEncryptionKey? key = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);
        return (_host ?? throw new InvalidOperationException("Content bundles load only while a game runs.")).Load(file, options, key);
    }

    /// <summary>Set by the game host while a game runs; null clears it.</summary>
    public static void SetHost(IContentBundleHost? host) => _host = host;
}
