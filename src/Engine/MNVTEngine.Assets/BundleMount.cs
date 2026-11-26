// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using MNVTEngine.Bundles;
using MNVTEngine.IO;

namespace MNVTEngine.Assets;

/// <summary>A content bundle mounted into an <see cref="AssetDatabase"/>: its assets sit under <see cref="Folder"/> (relative to <c>Assets/</c>).</summary>
public sealed record BundleMount(string Name, string Folder, ContentBundleManifest Manifest, IReadOnlyList<Guid> Assets, MountHandle Handle);
