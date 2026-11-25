// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

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

    private const float HistorySeconds = 1f;

    private readonly Dictionary<uint, NetworkObject> _objects = [];
    private readonly Dictionary<uint, Baseline> _baselines = [];
    private readonly Dictionary<uint, HashSet<uint>> _known = []; // clientId -> netIds it currently tracks (interest culling)
    private readonly Dictionary<uint, List<(float Time, Vector3 Position)>> _history = []; // for lag compensation
    private GalaxyServer? _server;
    private uint _nextId = 1;
    private float _accumulator;
    private float _serverTime;

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

    /// <summary>A custom relevance rule for interest management (zones, cells, teams): given a client and an object,
    /// return whether that client should receive the object. When set it replaces the radius test (an object that is
    /// <see cref="NetworkObject.AlwaysRelevant"/> or owned by the client is always relevant). Not serialized.</summary>
    public Func<uint, NetworkObject, bool>? RelevanceFilter { get; set; }

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

        _serverTime += Time.DeltaTime;
        RecordHistory();

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
            _history.Remove(networkObject.NetworkId);
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
            _history.Remove(id);
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

        uint networkId = message.ReadVarUInt();
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
        message.WriteVarUInt(networkId);
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
        message.WriteVarUInt(networkId);
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

        // Delta: send only the fields that changed since last sent, or all of an object's fields on a periodic refresh
        // (so a missed unreliable update is corrected). A newly connected client gets full state in the spawn.
        var sendFlags = new Dictionary<uint, byte>(replicated.Count);
        foreach (NetworkObject networkObject in replicated)
        {
            Baseline baseline = _baselines.TryGetValue(networkObject.NetworkId, out Baseline? existing) ? existing : (_baselines[networkObject.NetworkId] = new Baseline());
            byte synced = networkObject.GetComponent<NetworkTransform>()!.Flags;
            bool refresh = !baseline.Has || baseline.FramesSinceSent >= RefreshFrames;
            sendFlags[networkObject.NetworkId] = refresh ? synced : (byte)(ChangedFields(baseline, networkObject.Transform) & synced);
        }

        if (RelevanceRadius <= 0f && RelevanceFilter is null)
        {
            List<NetworkObject> included = replicated.Where(o => sendFlags[o.NetworkId] != 0).ToList();
            if (included.Count > 0)
            {
                Message message = Message.Create(ReplicationProtocol.Snapshot);
                message.Write((ushort)included.Count);
                foreach (NetworkObject networkObject in included)
                {
                    WriteObject(message, networkObject, sendFlags[networkObject.NetworkId]);
                }

                _server!.SendToAll(message, DeliveryMethod.Unreliable);
                message.Release();
            }
        }
        else
        {
            SendCulledSnapshots(replicated, sendFlags);
        }

        foreach (NetworkObject networkObject in replicated)
        {
            Baseline baseline = _baselines[networkObject.NetworkId];
            if (sendFlags[networkObject.NetworkId] != 0)
            {
                Store(baseline, networkObject.Transform);
            }
            else
            {
                baseline.FramesSinceSent++;
            }
        }
    }

    private void SendCulledSnapshots(List<NetworkObject> replicated, Dictionary<uint, byte> sendFlags)
    {
        float radiusSquared = RelevanceRadius * RelevanceRadius;
        foreach (uint clientId in _server!.Clients)
        {
            Vector3? view = ViewPosition(clientId);
            HashSet<uint> known = _known.TryGetValue(clientId, out HashSet<uint>? set) ? set : (_known[clientId] = []);
            var next = new HashSet<uint>();
            List<(NetworkObject Object, byte Flags)> included = [];
            foreach (NetworkObject networkObject in replicated)
            {
                if (!IsRelevant(networkObject, clientId, view, radiusSquared))
                {
                    continue;
                }

                next.Add(networkObject.NetworkId);

                // A newly relevant object is sent in full; one the client already tracks sends only changed fields.
                byte flags = known.Contains(networkObject.NetworkId) ? sendFlags[networkObject.NetworkId] : networkObject.GetComponent<NetworkTransform>()!.Flags;
                if (flags != 0)
                {
                    included.Add((networkObject, flags));
                }
            }

            _known[clientId] = next;
            if (included.Count > 0)
            {
                Message message = Message.Create(ReplicationProtocol.Snapshot);
                message.Write((ushort)included.Count);
                foreach ((NetworkObject networkObject, byte flags) in included)
                {
                    WriteObject(message, networkObject, flags);
                }

                _server.Send(clientId, message, DeliveryMethod.Unreliable);
                message.Release();
            }
        }
    }

    private bool IsRelevant(NetworkObject networkObject, uint clientId, Vector3? view, float radiusSquared)
    {
        if (networkObject.AlwaysRelevant || networkObject.Owner == clientId)
        {
            return true;
        }

        if (RelevanceFilter is { } filter)
        {
            return filter(clientId, networkObject);
        }

        if (view is not { } center)
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

    /// <summary>
    /// Lag compensation: the position a networked object had <paramref name="secondsAgo"/> ago, interpolated from the
    /// server's recent history (about a second is kept). When validating a client's action against what that client
    /// actually saw — a hit-scan shot, for example — rewind by the client's view latency (roughly its round-trip time
    /// plus the client's interpolation delay) and test against these past positions. Returns false if the object has
    /// no history yet.
    /// </summary>
    public bool TryGetPastPosition(uint networkId, float secondsAgo, out Vector3 position)
    {
        position = default;
        if (!_history.TryGetValue(networkId, out List<(float Time, Vector3 Position)>? history) || history.Count == 0)
        {
            return false;
        }

        float target = _serverTime - Math.Max(0f, secondsAgo);
        if (target <= history[0].Time)
        {
            position = history[0].Position;
            return true;
        }

        for (int i = 0; i < history.Count - 1; i++)
        {
            (float aTime, Vector3 aPos) = history[i];
            (float bTime, Vector3 bPos) = history[i + 1];
            if (target >= aTime && target <= bTime)
            {
                float t = bTime > aTime ? (target - aTime) / (bTime - aTime) : 0f;
                position = Vector3.Lerp(aPos, bPos, t);
                return true;
            }
        }

        position = history[^1].Position;
        return true;
    }

    private void RecordHistory()
    {
        float cutoff = _serverTime - HistorySeconds;
        foreach (NetworkObject networkObject in _objects.Values)
        {
            if (networkObject.IsDestroyed || networkObject.GetComponent<NetworkTransform>() is null)
            {
                continue;
            }

            List<(float Time, Vector3 Position)> history = _history.TryGetValue(networkObject.NetworkId, out List<(float, Vector3)>? existing) ? existing : (_history[networkObject.NetworkId] = []);
            history.Add((_serverTime, networkObject.Transform.Position));

            int drop = 0;
            while (drop < history.Count && history[drop].Time < cutoff)
            {
                drop++;
            }

            if (drop > 0)
            {
                history.RemoveRange(0, drop);
            }
        }
    }

    private static void WriteObject(Message message, NetworkObject networkObject, byte flags)
    {
        Transform current = networkObject.Transform;
        message.WriteVarUInt(networkObject.NetworkId);
        ReplicationProtocol.WriteState(message, flags, current.Position, current.Rotation, current.LocalScale);
    }

    private static byte ChangedFields(Baseline baseline, Transform transform)
    {
        const float epsilon = 1e-4f;
        byte flags = 0;
        Vector3 position = transform.Position;
        if (MathF.Abs(position.X - baseline.Position.X) > epsilon || MathF.Abs(position.Y - baseline.Position.Y) > epsilon || MathF.Abs(position.Z - baseline.Position.Z) > epsilon)
        {
            flags |= TransformState.PositionBit;
        }

        Quaternion rotation = transform.Rotation;
        float dot = (rotation.X * baseline.Rotation.X) + (rotation.Y * baseline.Rotation.Y) + (rotation.Z * baseline.Rotation.Z) + (rotation.W * baseline.Rotation.W);
        if (MathF.Abs(dot) < 0.99999f)
        {
            flags |= TransformState.RotationBit;
        }

        Vector3 scale = transform.LocalScale;
        if (MathF.Abs(scale.X - baseline.Scale.X) > epsilon || MathF.Abs(scale.Y - baseline.Scale.Y) > epsilon || MathF.Abs(scale.Z - baseline.Scale.Z) > epsilon)
        {
            flags |= TransformState.ScaleBit;
        }

        return flags;
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
        message.WriteVarUInt(networkObject.NetworkId);
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
