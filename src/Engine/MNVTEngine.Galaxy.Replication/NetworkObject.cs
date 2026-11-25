// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

namespace MNVTEngine;

/// <summary>Who decides a networked object's state.</summary>
public enum NetworkRole
{
    /// <summary>This side owns the object and its state is sent to others (the server).</summary>
    Authority,

    /// <summary>This side mirrors an object owned elsewhere; its transform is driven by snapshots (a client).</summary>
    Remote,
}

/// <summary>
/// Marks a GameObject as replicated. The server assigns a <see cref="NetworkId"/> and tells clients to spawn the
/// object from its <see cref="Prefab"/>; a <see cref="NetworkTransform"/> on the same object then replicates its
/// transform. A networked object must be a prefab (so clients can instantiate it) with its <see cref="Prefab"/> set to
/// that prefab's asset.
/// </summary>
[ComponentId("MNVTEngine.NetworkObject")]
public sealed class NetworkObject : Component
{
    /// <summary>The prefab asset clients instantiate for this object when the server spawns it.</summary>
    [SerializeField]
    public AssetRef Prefab { get; set; }

    /// <summary>When the server uses interest management (a relevance radius), this object is still sent to every
    /// client regardless of distance (e.g. a boss, an objective).</summary>
    [SerializeField]
    public bool AlwaysRelevant { get; set; }

    /// <summary>The server-assigned id, unique within a session; 0 until the object is spawned on the network.</summary>
    public uint NetworkId { get; internal set; }

    /// <summary>The client that owns this object, or 0 for the server. Reserved for owner-authoritative gameplay.</summary>
    public uint Owner { get; internal set; }

    /// <summary>Whether this side owns the object (server) or mirrors it (client).</summary>
    public NetworkRole Role { get; internal set; } = NetworkRole.Authority;

    /// <summary>True once the object has a network id.</summary>
    public bool IsSpawned => NetworkId != 0;
}
