// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.IO;

/// <summary>
/// Serves chosen folders of another source under other folders, and nothing else: <c>{ "Assets/Bundles/b1": "Assets" }</c>
/// shows the source's <c>Assets/x.png</c> as <c>Assets/Bundles/b1/x.png</c>. Content bundles mount this way, so their
/// files sit apart from the game's and their other entries (manifest, index) are not visible. Owns the inner source.
/// </summary>
public sealed class PrefixedFileSystemProvider : IFileSystemProvider
{
    private readonly IFileSystemProvider _inner;
    private readonly (string Outer, string Inner)[] _folders;

    /// <param name="folders">Outer folder to inner folder; folders must not contain one another.</param>
    /// <exception cref="ArgumentException">An invalid folder, or one inside another.</exception>
    public PrefixedFileSystemProvider(IFileSystemProvider inner, IReadOnlyDictionary<string, string> folders)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(folders);
        _folders = [.. folders.Select(f => (VirtualPath.Normalize(f.Key), VirtualPath.Normalize(f.Value)))];
        foreach (var (outer, _) in _folders)
        {
            if (outer.Length == 0 || _folders.Any(o => o.Outer != outer && VirtualPath.IsUnder(o.Outer, outer)))
            {
                throw new ArgumentException($"'{outer}' is empty or contains another folder.", nameof(folders));
            }
        }

        _inner = inner;
    }

    public string Name => _inner.Name;

    public string Kind => _inner.Kind;

    public (int Files, long Bytes)? Size => null; // only part of the source is visible

    public bool TryGetFile(string path, out VirtualFileInfo file)
    {
        file = default;
        if (ToInner(path) is not { } inner || !_inner.TryGetFile(inner, out var found))
        {
            return false;
        }

        file = found with { Path = path };
        return true;
    }

    public Stream OpenRead(string path) =>
        ToInner(path) is { } inner ? _inner.OpenRead(inner) : throw new FileNotFoundException($"'{path}' is not in this source.", path);

    public bool DirectoryExists(string path)
    {
        var canonical = VirtualPath.Normalize(path);
        return _folders.Any(f => VirtualPath.IsUnder(f.Outer, canonical)) || (ToInner(canonical) is { } inner && (inner.Length == 0 || _inner.DirectoryExists(inner)));
    }

    public IEnumerable<string> EnumerateFiles()
    {
        foreach (var file in _inner.EnumerateFiles())
        {
            foreach (var (outer, inner) in _folders)
            {
                if (VirtualPath.RelativeTo(file, inner) is { Length: > 0 } relative)
                {
                    yield return VirtualPath.Combine(outer, relative);
                }
            }
        }
    }

    public string? FindAsset(Guid assetGuid) => null; // assets are found through the index that mounted the source

    public void Dispose() => _inner.Dispose();

    /// <summary>The inner path of an outer one; null outside the visible folders.</summary>
    private string? ToInner(string path)
    {
        if (!VirtualPath.TryNormalize(path, out var canonical, out _))
        {
            return null;
        }

        foreach (var (outer, inner) in _folders)
        {
            if (VirtualPath.RelativeTo(canonical, outer) is { } relative)
            {
                return relative.Length == 0 ? inner : VirtualPath.Combine(inner, relative);
            }
        }

        return null;
    }
}
