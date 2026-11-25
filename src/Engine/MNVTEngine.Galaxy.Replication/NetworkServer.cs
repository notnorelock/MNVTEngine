// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using MNVTEngine.Galaxy;
using MNVTEngine.Galaxy.Replication;
using MNVTEngine.Mathematics;

namespace MNVTEngine;

/// <summary>
/// The authoritative side of replication. Add it to a scene to host: it opens a <see cref="GalaxyServer"/>, assigns a
/// network id to every <see cref="NetworkObject"/> in the scene, tells clients to spawn and despawn them, and sends a
/// transform snapshot of them all at a fixed rate. The server's own transforms are the source of truth; clients
/// mirror them. Runs only while the scene is playing.
/// </summary>
[ComponentId("MNVTEngine.NetworkServer")]
public sealed class NetworkServer : Component, IDisposable
{
    private sealed class Baseline
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        public int FramesSinceSent;
        public bool Has;
    }

    private readonly Dictionary<uint, NetworkObject> _objects = [];
    private readonly Dictionary<uint, Baseline> _baselines = [];
    private readonly Dictionary<uint, HashSet<uint>> _known = []; // clientId -> netIds it currently tracks (interest culling)
    private GalaxyServer? _server;
    private uint _nextId = 1;
    private float _accumulator;

    /// <summary>UDP port to listen on.</summary>
    [SerializeField]
    public int Port { get; set; } = 7777;

    /// <summary>Maximum connected clients.</summary>
    [SerializeField]
    public int MaxClients { get; set; } = 32;

    /// <summary>Transform snapshots sent per second.</summary>
    [SerializeField]
    public int SnapshotRate { get; set; } = 20;

    /// <summary>Only objects within this distance of a client's owned object are sent to it (interest management);
    /// 0 disables culling and sends every object to every client. An object marked
    /// <see cref="NetworkObject.AlwaysRelevant"/>, or owned by the client, is always sent.</summary>
    [SerializeField]
    public float RelevanceRadius { get; set; }

    /// <summary>How often an unchanged object is resent anyway, in seconds, so a client that missed its last update
    /// (snapshots are unreliable) does not hold a stale transform.</summary>
    [SerializeField]
    public float RefreshSeconds { get; set; } = 1f;

    private int RefreshFrames => Math.Max(1, (int)(RefreshSeconds * Math.Max(1, SnapshotRate)));

    /// <summary>Whether the server socket is open.</summary>
    public bool IsRunning => _server is not null;

    /// <summary>The actual bound port (useful when <see cref="Port"/> is 0 to pick any free port); 0 until started.</summary>
    public int BoundPort { get; private set; }

    public override void Start() => StartServer();

    /// <summary>Opens the server socket. Called automatically when the scene starts playing.</summary>
    public void StartServer()
    {
        if (_server is not null)
        {
            return;
        }

        _server = new GalaxyServer();
        _server.ClientConnected += OnClientConnected;
        _server.ClientDisconnected += OnClientDisconnected;
        _server.MessageReceived += OnMessage;
        BoundPort = _server.Start(Port, MaxClients).Port;
    }

    /// <summary>Raised for a client message that is not replication (a game's own message).</summary>
    public event Action<uint, Message>? MessageReceived;

    /// <summary>Raised when a client connects (e.g. to assign it ownership of a spawned player object).</summary>
    public event Action<uint>? ClientConnected;

    /// <summary>Raised when a client disconnects.</summary>
    public event Action<uint>? ClientDisconnected;

    /// <summary>Gives a client authority over an object: that client then drives its transform and the server
    /// accepts it. Pass 0 to return authority to the server.</summary>
    public void AssignOwnership(NetworkObject networkObject, uint clientId)
    {
        ArgumentNullException.ThrowIfNull(networkObject);
        if (_server is null || networkObject.NetworkId == 0)
        {
            return;
        }

        uint previous = networkObject.Owner;
        networkObject.Owner = clientId;
        if (previous is not 0 && previous != clientId)
        {
            SendOwnerAssign(previous, networkObject.NetworkId, owned: false);
        }

        if (clientId is not 0)
        {
            SendOwnerAssign(clientId, networkObject.NetworkId, owned: true);
        }
    }

    public override void OnDestroy() => Dispose();

    public void Dispose()
    {
        _server?.Dispose();
        _server = null;
        GC.SuppressFinalize(this);
    }

    public override void Update()
    {
        if (_server is null)
        {
            return;
        }

        _server.Tick();
        RegisterNewObjects();
        RemoveDestroyedObjects();

        _accumulator += Time.DeltaTime;
        float interval = 1f / Math.Max(1, SnapshotRate);
        if (_accumulator >= interval)
        {
            _accumulator = 0f;
            BroadcastSnapshot();
        }
    }

    /// <summary>Instantiates a prefab on the server and spawns it on every client.</summary>
    public NetworkObject Spawn(AssetRef prefab, Vector3 position)
    {
        GameObject instance = GameObject.Scene.Instantiate(prefab);
        instance.Transform.Position = position;
        NetworkObject networkObject = instance.GetComponent<NetworkObject>() ?? instance.AddComponent<NetworkObject>();
        networkObject.Prefab = prefab;
        networkObject.Role = NetworkRole.Authority;
        Assign(networkObject);
        BroadcastSpawn(networkObject);
        return networkObject;
    }

    /// <summary>Despawns a networked object everywhere and destroys it.</summary>
    public void Despawn(NetworkObject networkObject)
    {
        ArgumentNullException.ThrowIfNull(networkObject);
        if (_objects.Remove(networkObject.NetworkId))
        {
            _baselines.Remove(networkObject.NetworkId);
            BroadcastDespawn(networkObject.NetworkId);
        }

        networkObject.GameObject.Destroy();
    }

    private void RegisterNewObjects()
    {
        foreach (NetworkObject networkObject in GameObject.Scene.FindObjectsOfType<NetworkObject>())
        {
            // Register any networked object, whether it carries a prefab (clients instantiate it) or is authored in the
            // scene (clients bind their own copy by scene id).
            if (networkObject.NetworkId == 0 && !networkObject.IsDestroyed)
            {
                networkObject.Role = NetworkRole.Authority;
                Assign(networkObject);
                BroadcastSpawn(networkObject);
            }
        }
    }

    private void RemoveDestroyedObjects()
    {
        List<uint> dead = [];
        foreach ((uint id, NetworkObject networkObject) in _objects)
        {
            if (networkObject.IsDestroyed)
            {
                dead.Add(id);
            }
        }

        foreach (uint id in dead)
        {
            _objects.Remove(id);
            _baselines.Remove(id);
            BroadcastDespawn(id);
        }
    }

    private void Assign(NetworkObject networkObject)
    {
        networkObject.NetworkId = _nextId++;
        _objects[networkObject.NetworkId] = networkObject;
    }

    private void OnClientConnected(uint clientId)
    {
        foreach (NetworkObject networkObject in _objects.Values)
        {
            Message spawn = BuildSpawn(networkObject);
            _server!.Send(clientId, spawn, DeliveryMethod.Reliable);
            spawn.Release();
            if (networkObject.Owner == clientId)
            {
                SendOwnerAssign(clientId, networkObject.NetworkId, owned: true);
            }
        }

        ClientConnected?.Invoke(clientId);
    }

    private void OnClientDisconnected(uint clientId, DisconnectReason reason)
    {
        foreach (NetworkObject networkObject in _objects.Values)
        {
            if (networkObject.Owner == clientId)
            {
                networkObject.Owner = 0; // The owner left: the server takes authority back.
            }
        }

        _known.Remove(clientId);
        ClientDisconnected?.Invoke(clientId);
    }

    private void OnMessage(uint clientId, Message message)
    {
        if (message.Id != ReplicationProtocol.OwnerState)
        {
            MessageReceived?.Invoke(clientId, message);
            return;
        }

        uint networkId = message.ReadUInt();
        TransformState state = ReplicationProtocol.ReadState(message);
        if (message.Failed || !_objects.TryGetValue(networkId, out NetworkObject? networkObject) || networkObject.Owner != clientId || networkObject.IsDestroyed)
        {
            return; // Only the object's owner may drive it.
        }

        Transform transform = networkObject.Transform;
        if ((state.Flags & TransformState.PositionBit) != 0)
        {
            transform.Position = state.Position;
        }

        if ((state.Flags & TransformState.RotationBit) != 0)
        {
            transform.Rotation = state.Rotation;
        }

        if ((state.Flags & TransformState.ScaleBit) != 0)
        {
            transform.LocalScale = state.Scale;
        }
    }

    private void SendOwnerAssign(uint clientId, uint networkId, bool owned)
    {
        Message message = Message.Create(ReplicationProtocol.OwnerAssign);
        message.Write(networkId);
        message.Write(owned);
        _server!.Send(clientId, message, DeliveryMethod.Reliable);
        message.Release();
    }

    private void BroadcastSpawn(NetworkObject networkObject)
    {
        Message spawn = BuildSpawn(networkObject);
        _server!.SendToAll(spawn, DeliveryMethod.Reliable);
        spawn.Release();
    }

    private void BroadcastDespawn(uint networkId)
    {
        Message message = Message.Create(ReplicationProtocol.Despawn);
        message.Write(networkId);
        _server!.SendToAll(message, DeliveryMethod.Reliable);
        message.Release();
    }

    private void BroadcastSnapshot()
    {
        List<NetworkObject> replicated = [];
        foreach (NetworkObject networkObject in _objects.Values)
        {
            if (!networkObject.IsDestroyed && networkObject.GetComponent<NetworkTransform>() is not null)
            {
                replicated.Add(networkObject);
            }
        }

        if (replicated.Count == 0)
        {
            return;
        }

        // Delta: send an object only when its transform changed since last sent, or when it is due for a periodic
        // refresh. A newly connected client gets every object's state in the spawn, so this never starves one.
        var dirty = new Dictionary<uint, bool>(replicated.Count);
        foreach (NetworkObject networkObject in replicated)
        {
            Baseline baseline = _baselines.TryGetValue(networkObject.NetworkId, out Baseline? existing) ? existing : (_baselines[networkObject.NetworkId] = new Baseline());
            dirty[networkObject.NetworkId] = !baseline.Has || baseline.FramesSinceSent >= RefreshFrames || Changed(baseline, networkObject.Transform);
        }

        if (RelevanceRadius <= 0f)
        {
            List<NetworkObject> included = replicated.Where(o => dirty[o.NetworkId]).ToList();
            if (included.Count > 0)
            {
                Message message = BuildSnapshot(included);
                _server!.SendToAll(message, DeliveryMethod.Unreliable);
                message.Release();
            }
        }
        else
        {
            SendCulledSnapshots(replicated, dirty);
        }

        foreach (NetworkObject networkObject in replicated)
        {
            Baseline baseline = _baselines[networkObject.NetworkId];
            if (dirty[networkObject.NetworkId])
            {
                Store(baseline, networkObject.Transform);
            }
            else
            {
                baseline.FramesSinceSent++;
            }
        }
    }

    private void SendCulledSnapshots(List<NetworkObject> replicated, Dictionary<uint, bool> dirty)
    {
        float radiusSquared = RelevanceRadius * RelevanceRadius;
        foreach (uint clientId in _server!.Clients)
        {
            Vector3? view = ViewPosition(clientId);
            HashSet<uint> known = _known.TryGetValue(clientId, out HashSet<uint>? set) ? set : (_known[clientId] = []);
            var next = new HashSet<uint>();
            List<NetworkObject> included = [];
            foreach (NetworkObject networkObject in replicated)
            {
                if (!IsRelevant(networkObject, clientId, view, radiusSquared))
                {
                    continue;
                }

                next.Add(networkObject.NetworkId);
                if (dirty[networkObject.NetworkId] || !known.Contains(networkObject.NetworkId))
                {
                    included.Add(networkObject); // Dirty, or newly relevant to this client, so send it fresh.
                }
            }

            _known[clientId] = next;
            if (included.Count > 0)
            {
                Message message = BuildSnapshot(included);
                _server.Send(clientId, message, DeliveryMethod.Unreliable);
                message.Release();
            }
        }
    }

    private static bool IsRelevant(NetworkObject networkObject, uint clientId, Vector3? view, float radiusSquared)
    {
        if (networkObject.AlwaysRelevant || networkObject.Owner == clientId || view is not { } center)
        {
            return true;
        }

        Vector3 delta = networkObject.Transform.Position - center;
        return (delta.X * delta.X) + (delta.Y * delta.Y) + (delta.Z * delta.Z) <= radiusSquared;
    }

    private Vector3? ViewPosition(uint clientId)
    {
        foreach (NetworkObject networkObject in _objects.Values)
        {
            if (networkObject.Owner == clientId && !networkObject.IsDestroyed && networkObject.GetComponent<NetworkTransform>() is not null)
            {
                return networkObject.Transform.Position;
            }
        }

        return null; // The client owns nothing to view from: treat every object as relevant.
    }

    private static Message BuildSnapshot(List<NetworkObject> objects)
    {
        Message message = Message.Create(ReplicationProtocol.Snapshot);
        message.Write((ushort)objects.Count);
        foreach (NetworkObject networkObject in objects)
        {
            NetworkTransform transform = networkObject.GetComponent<NetworkTransform>()!;
            Transform current = networkObject.Transform;
            message.Write(networkObject.NetworkId);
            ReplicationProtocol.WriteState(message, transform.Flags, current.Position, current.Rotation, current.LocalScale);
        }

        return message;
    }

    private static bool Changed(Baseline baseline, Transform transform)
    {
        const float epsilon = 1e-4f;
        Vector3 position = transform.Position;
        if (MathF.Abs(position.X - baseline.Position.X) > epsilon || MathF.Abs(position.Y - baseline.Position.Y) > epsilon || MathF.Abs(position.Z - baseline.Position.Z) > epsilon)
        {
            return true;
        }

        Quaternion rotation = transform.Rotation;
        float dot = (rotation.X * baseline.Rotation.X) + (rotation.Y * baseline.Rotation.Y) + (rotation.Z * baseline.Rotation.Z) + (rotation.W * baseline.Rotation.W);
        if (MathF.Abs(dot) < 0.99999f)
        {
            return true;
        }

        Vector3 scale = transform.LocalScale;
        return MathF.Abs(scale.X - baseline.Scale.X) > epsilon || MathF.Abs(scale.Y - baseline.Scale.Y) > epsilon || MathF.Abs(scale.Z - baseline.Scale.Z) > epsilon;
    }

    private static void Store(Baseline baseline, Transform transform)
    {
        baseline.Position = transform.Position;
        baseline.Rotation = transform.Rotation;
        baseline.Scale = transform.LocalScale;
        baseline.FramesSinceSent = 0;
        baseline.Has = true;
    }

    private static Message BuildSpawn(NetworkObject networkObject)
    {
        Message message = Message.Create(ReplicationProtocol.Spawn);
        message.Write(networkObject.NetworkId);
        message.Write(networkObject.GameObject.Id.ToByteArray()); // Scene id: bind an object the client already has from the same scene.
        message.Write(networkObject.Prefab.Guid.ToByteArray());   // Prefab: instantiate when the client has no such scene object.
        Transform transform = networkObject.Transform;
        ReplicationProtocol.WriteState(
            message,
            TransformState.PositionBit | TransformState.RotationBit | TransformState.ScaleBit,
            transform.Position,
            transform.Rotation,
            transform.LocalScale);
        return message;
    }
}
