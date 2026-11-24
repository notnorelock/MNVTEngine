// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using MNVTEngine.Galaxy;
using MNVTEngine.Galaxy.Replication;

namespace MNVTEngine;

/// <summary>
/// The client side of replication. Add it to a scene to join a server: it opens a <see cref="GalaxyClient"/>,
/// instantiates the prefabs the server spawns, destroys them when despawned, and drives their
/// <see cref="NetworkTransform"/>s from the snapshot stream (interpolated). Game messages that are not replication are
/// raised through <see cref="MessageReceived"/>, and <see cref="Send"/> sends to the server. Runs only while the scene
/// is playing — nothing connects in the editor's edit mode.
/// </summary>
[ComponentId("MNVTEngine.NetworkClient")]
public sealed class NetworkClient : Component, IDisposable
{
    private readonly Dictionary<uint, NetworkObject> _objects = [];
    private GalaxyClient? _client;

    /// <summary>Server host name or address.</summary>
    [SerializeField]
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Server UDP port.</summary>
    [SerializeField]
    public int Port { get; set; } = 7777;

    /// <summary>The server's identity public key (base64) to pin, defeating a man-in-the-middle. Empty connects
    /// without server authentication.</summary>
    [SerializeField]
    public string PinnedServerKey { get; set; } = "";

    /// <summary>Connect as soon as the scene starts playing.</summary>
    [SerializeField]
    public bool ConnectOnStart { get; set; } = true;

    /// <summary>Raised once connected.</summary>
    public event Action? Connected;

    /// <summary>Raised when the connection ends.</summary>
    public event Action<DisconnectReason>? Disconnected;

    /// <summary>Raised for a server message that is not replication (a game's own message).</summary>
    public event Action<Message>? MessageReceived;

    /// <summary>The connection state.</summary>
    public GalaxyClientState State => _client?.State ?? GalaxyClientState.Disconnected;

    /// <summary>Whether the client is connected.</summary>
    public bool IsConnected => State == GalaxyClientState.Connected;

    /// <summary>How many replicated objects are currently spawned on this client.</summary>
    public int SpawnedObjectCount => _objects.Count;

    /// <summary>Total messages received from the server.</summary>
    public long MessagesReceived { get; private set; }

    /// <summary>Total messages sent to the server.</summary>
    public long MessagesSent { get; private set; }

    public override void Start()
    {
        if (ConnectOnStart)
        {
            Connect();
        }
    }

    /// <summary>Begins connecting to the configured server.</summary>
    public void Connect()
    {
        if (_client is not null)
        {
            return;
        }

        _client = new GalaxyClient();
        _client.Connected += () => Connected?.Invoke();
        _client.Disconnected += reason =>
        {
            ClearObjects();
            Disconnected?.Invoke(reason);
        };
        _client.MessageReceived += OnMessage;
        _client.Connect(Host, Port, ParsePin(PinnedServerKey));
    }

    /// <summary>Disconnects from the server.</summary>
    public void Disconnect() => _client?.Disconnect();

    /// <summary>Sends a game message to the server.</summary>
    public void Send(Message message, DeliveryMethod method = DeliveryMethod.Reliable)
    {
        _client?.Send(message, method);
        MessagesSent++;
    }

    public override void Update() => _client?.Tick();

    public override void OnDestroy() => Dispose();

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        ClearObjects();
        GC.SuppressFinalize(this);
    }

    private static byte[] ParsePin(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private void OnMessage(Message message)
    {
        MessagesReceived++;
        switch (message.Id)
        {
            case ReplicationProtocol.Spawn:
                HandleSpawn(message);
                break;

            case ReplicationProtocol.Despawn:
                HandleDespawn(message);
                break;

            case ReplicationProtocol.Snapshot:
                HandleSnapshot(message);
                break;

            default:
                MessageReceived?.Invoke(message);
                break;
        }
    }

    private void HandleSpawn(Message message)
    {
        uint networkId = message.ReadUInt();
        byte[] guidBytes = message.ReadBytes();
        TransformState state = ReplicationProtocol.ReadState(message);
        if (message.Failed || guidBytes.Length != 16 || _objects.ContainsKey(networkId))
        {
            return;
        }

        var prefab = new AssetRef(new Guid(guidBytes));
        GameObject instance;
        try
        {
            instance = GameObject.Scene.Instantiate(prefab);
        }
        catch (InvalidOperationException ex)
        {
            Log.Error("Could not spawn a replicated prefab; is its asset present and a prefab?", ex);
            return;
        }

        NetworkObject networkObject = instance.GetComponent<NetworkObject>() ?? instance.AddComponent<NetworkObject>();
        networkObject.NetworkId = networkId;
        networkObject.Prefab = prefab;
        networkObject.Role = NetworkRole.Remote;
        instance.Transform.Position = state.Position;
        instance.Transform.Rotation = state.Rotation;
        instance.Transform.LocalScale = state.Scale;
        _objects[networkId] = networkObject;
    }

    private void HandleDespawn(Message message)
    {
        uint networkId = message.ReadUInt();
        if (!message.Failed && _objects.Remove(networkId, out NetworkObject? networkObject) && !networkObject.IsDestroyed)
        {
            networkObject.GameObject.Destroy();
        }
    }

    private void HandleSnapshot(Message message)
    {
        ushort count = message.ReadUShort();
        float now = Time.TimeSinceStart;
        for (int i = 0; i < count; i++)
        {
            uint networkId = message.ReadUInt();
            TransformState state = ReplicationProtocol.ReadState(message);
            if (message.Failed)
            {
                return;
            }

            if (_objects.TryGetValue(networkId, out NetworkObject? networkObject))
            {
                networkObject.GetComponent<NetworkTransform>()?.ReceiveState(now, state);
            }
        }
    }

    private void ClearObjects()
    {
        foreach (NetworkObject networkObject in _objects.Values)
        {
            if (!networkObject.IsDestroyed)
            {
                networkObject.GameObject.Destroy();
            }
        }

        _objects.Clear();
    }
}
