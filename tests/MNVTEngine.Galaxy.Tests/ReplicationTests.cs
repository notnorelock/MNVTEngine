// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using System.Diagnostics;
using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;

namespace MNVTEngine.Galaxy.Tests;

/// <summary>Two engine scenes over a real UDP loopback: the server spawns and moves a networked object, the client
/// instantiates it from its prefab and follows the server's transform through interpolation.</summary>
public sealed class ReplicationTests
{
    private sealed class FakePrefabs : IPrefabProvider
    {
        public GameObject Instantiate(Scene scene, AssetRef prefab, Transform? parent)
        {
            GameObject gameObject = scene.CreateGameObject("Replicated", parent);
            gameObject.AddComponent<NetworkObject>();
            gameObject.AddComponent<NetworkTransform>();
            return gameObject;
        }
    }

    private sealed class Stepper(Scene scene)
    {
        private long _frame;
        private double _total;

        public void Step(float deltaSeconds)
        {
            _total += deltaSeconds;
            scene.Update(new FrameTime(deltaSeconds, deltaSeconds, _total, _total, _frame++));
        }
    }

    [Fact]
    public void AClient_SpawnsTheServersObject_AndFollowsItsMovement()
    {
        var serverScene = new Scene();
        var serverStep = new Stepper(serverScene);
        NetworkServer server = serverScene.CreateGameObject("Net").AddComponent<NetworkServer>();
        server.Port = 0;
        server.SnapshotRate = 60;

        GameObject player = serverScene.CreateGameObject("Player");
        NetworkObject playerObject = player.AddComponent<NetworkObject>();
        playerObject.Prefab = new AssetRef(Guid.NewGuid());
        player.AddComponent<NetworkTransform>();
        player.Transform.Position = Vector3.Zero;

        serverScene.Start();
        serverStep.Step(0f); // Runs the component Start: opens the socket and binds the port.
        Assert.True(server.BoundPort != 0);

        var clientScene = new Scene { Prefabs = new FakePrefabs() };
        var clientStep = new Stepper(clientScene);
        NetworkClient client = clientScene.CreateGameObject("Net").AddComponent<NetworkClient>();
        client.Host = "127.0.0.1";
        client.Port = server.BoundPort;
        clientScene.Start();

        const float dt = 1f / 60f;
        Pump(serverStep, clientStep, dt, () => client.IsConnected && client.SpawnedObjectCount == 1, 5000);
        Assert.True(client.IsConnected);
        Assert.Equal(1, client.SpawnedObjectCount);

        player.Transform.Position = new Vector3(5f, 0f, 0f);
        Pump(serverStep, clientStep, dt, () => RemotePosition(clientScene).X > 4.5f, 3000);

        Assert.InRange(RemotePosition(clientScene).X, 4.5f, 5.5f);

        ((IDisposable)server).Dispose();
        ((IDisposable)client).Dispose();
    }

    [Fact]
    public void AnAuthoredSceneObject_BindsBySceneId_WithoutAPrefab_AndFollowsTheServer()
    {
        ComponentRegistry registry = ComponentRegistry.CreateDefault().Register<NetworkObject>().Register<NetworkTransform>().Register<NetworkServer>().Register<NetworkClient>();
        var serializer = new SceneSerializer(registry);

        var authored = new Scene();
        GameObject mover = authored.CreateGameObject("Mover");
        mover.AddComponent<NetworkObject>(); // No prefab: it exists in the scene on both sides.
        mover.AddComponent<NetworkTransform>();
        string json = serializer.Serialize(authored);

        Scene serverScene = serializer.Deserialize(json).Scene;
        var serverStep = new Stepper(serverScene);
        NetworkServer server = serverScene.CreateGameObject("Net").AddComponent<NetworkServer>();
        server.Port = 0;
        server.SnapshotRate = 60;
        serverScene.Start();
        serverStep.Step(0f);

        Scene clientScene = serializer.Deserialize(json).Scene;
        var clientStep = new Stepper(clientScene);
        NetworkClient client = clientScene.CreateGameObject("Net").AddComponent<NetworkClient>();
        client.Host = "127.0.0.1";
        client.Port = server.BoundPort;
        clientScene.Start();

        NetworkObject clientMover = clientScene.Find("Mover")!.GetComponent<NetworkObject>()!;
        const float dt = 1f / 60f;
        Pump(serverStep, clientStep, dt, () => clientMover.Role == NetworkRole.Remote && clientMover.IsSpawned, 5000);
        Assert.True(clientMover.IsSpawned);
        Assert.Equal(NetworkRole.Remote, clientMover.Role);

        serverScene.Find("Mover")!.Transform.Position = new Vector3(3f, 0f, 0f);
        Pump(serverStep, clientStep, dt, () => clientMover.Transform.Position.X > 2.5f, 3000);
        Assert.InRange(clientMover.Transform.Position.X, 2.5f, 3.5f);

        ((IDisposable)server).Dispose();
        ((IDisposable)client).Dispose();
    }

    [Fact]
    public void AnOwningClient_DrivesItsObject_AndTheServerFollows()
    {
        ComponentRegistry registry = ComponentRegistry.CreateDefault().Register<NetworkObject>().Register<NetworkTransform>().Register<NetworkServer>().Register<NetworkClient>();
        var serializer = new SceneSerializer(registry);

        var authored = new Scene();
        GameObject avatar = authored.CreateGameObject("Avatar");
        avatar.AddComponent<NetworkObject>();
        avatar.AddComponent<NetworkTransform>();
        string json = serializer.Serialize(authored);

        Scene serverScene = serializer.Deserialize(json).Scene;
        var serverStep = new Stepper(serverScene);
        NetworkServer server = serverScene.CreateGameObject("Net").AddComponent<NetworkServer>();
        server.Port = 0;
        server.SnapshotRate = 60;
        uint? joined = null;
        server.ClientConnected += id => joined = id;
        serverScene.Start();
        serverStep.Step(0f);

        Scene clientScene = serializer.Deserialize(json).Scene;
        var clientStep = new Stepper(clientScene);
        NetworkClient client = clientScene.CreateGameObject("Net").AddComponent<NetworkClient>();
        client.Host = "127.0.0.1";
        client.Port = server.BoundPort;
        clientScene.Start();

        NetworkObject clientAvatar = clientScene.Find("Avatar")!.GetComponent<NetworkObject>()!;
        NetworkObject serverAvatar = serverScene.Find("Avatar")!.GetComponent<NetworkObject>()!;
        const float dt = 1f / 60f;
        Pump(serverStep, clientStep, dt, () => joined is not null && clientAvatar.IsSpawned, 5000);
        Assert.NotNull(joined);

        server.AssignOwnership(serverAvatar, joined!.Value);
        Pump(serverStep, clientStep, dt, () => clientAvatar.Role == NetworkRole.Authority, 3000);
        Assert.Equal(NetworkRole.Authority, clientAvatar.Role);

        // The owning client moves its object; the server must accept the owner's transform.
        clientAvatar.Transform.Position = new Vector3(-6f, 0f, 0f);
        Pump(serverStep, clientStep, dt, () => serverAvatar.Transform.Position.X < -5.5f, 3000);
        Assert.InRange(serverAvatar.Transform.Position.X, -6.5f, -5.5f);

        ((IDisposable)server).Dispose();
        ((IDisposable)client).Dispose();
    }

    [Fact]
    public void InterestManagement_SendsNearObjects_ButCullsFarOnes()
    {
        ComponentRegistry registry = ComponentRegistry.CreateDefault().Register<NetworkObject>().Register<NetworkTransform>().Register<NetworkServer>().Register<NetworkClient>();
        var serializer = new SceneSerializer(registry);

        var authored = new Scene();
        foreach ((string name, Vector3 position) in new[] { ("Me", Vector3.Zero), ("Near", new Vector3(2f, 0f, 0f)), ("Far", new Vector3(100f, 0f, 0f)) })
        {
            GameObject go = authored.CreateGameObject(name);
            go.AddComponent<NetworkObject>();
            go.AddComponent<NetworkTransform>();
            go.Transform.Position = position;
        }

        string json = serializer.Serialize(authored);

        Scene serverScene = serializer.Deserialize(json).Scene;
        var serverStep = new Stepper(serverScene);
        NetworkServer server = serverScene.CreateGameObject("Net").AddComponent<NetworkServer>();
        server.Port = 0;
        server.SnapshotRate = 60;
        server.RelevanceRadius = 5f;
        uint? joined = null;
        server.ClientConnected += id => joined = id;
        serverScene.Start();
        serverStep.Step(0f);

        Scene clientScene = serializer.Deserialize(json).Scene;
        var clientStep = new Stepper(clientScene);
        NetworkClient client = clientScene.CreateGameObject("Net").AddComponent<NetworkClient>();
        client.Host = "127.0.0.1";
        client.Port = server.BoundPort;
        clientScene.Start();

        NetworkObject clientMe = clientScene.Find("Me")!.GetComponent<NetworkObject>()!;
        const float dt = 1f / 60f;
        Pump(serverStep, clientStep, dt, () => joined is not null && clientMe.IsSpawned, 5000);
        server.AssignOwnership(serverScene.Find("Me")!.GetComponent<NetworkObject>()!, joined!.Value);
        Pump(serverStep, clientStep, dt, () => clientMe.Role == NetworkRole.Authority, 3000);

        serverScene.Find("Near")!.Transform.Position = new Vector3(2f, 3f, 0f);  // within the radius
        serverScene.Find("Far")!.Transform.Position = new Vector3(100f, 9f, 0f); // far outside it
        Pump(serverStep, clientStep, dt, () => clientScene.Find("Near")!.Transform.Position.Y > 2.5f, 3000);

        Assert.InRange(clientScene.Find("Near")!.Transform.Position.Y, 2.5f, 3.5f); // near object updated
        Assert.InRange(clientScene.Find("Far")!.Transform.Position.Y, -0.5f, 0.5f);  // far object culled: still at its spawn state

        ((IDisposable)server).Dispose();
        ((IDisposable)client).Dispose();
    }

    [Fact]
    public void ACustomRelevanceFilter_DecidesWhichObjectsAClientGets()
    {
        ComponentRegistry registry = ComponentRegistry.CreateDefault().Register<NetworkObject>().Register<NetworkTransform>().Register<NetworkServer>().Register<NetworkClient>();
        var serializer = new SceneSerializer(registry);

        var authored = new Scene();
        foreach (string name in new[] { "Visible", "Blocked" })
        {
            GameObject go = authored.CreateGameObject(name);
            go.AddComponent<NetworkObject>();
            go.AddComponent<NetworkTransform>();
        }

        string json = serializer.Serialize(authored);

        Scene serverScene = serializer.Deserialize(json).Scene;
        var serverStep = new Stepper(serverScene);
        NetworkServer server = serverScene.CreateGameObject("Net").AddComponent<NetworkServer>();
        server.Port = 0;
        server.SnapshotRate = 60;
        server.RelevanceFilter = (_, networkObject) => networkObject.GameObject.Name != "Blocked";
        serverScene.Start();
        serverStep.Step(0f);

        Scene clientScene = serializer.Deserialize(json).Scene;
        var clientStep = new Stepper(clientScene);
        NetworkClient client = clientScene.CreateGameObject("Net").AddComponent<NetworkClient>();
        client.Host = "127.0.0.1";
        client.Port = server.BoundPort;
        clientScene.Start();

        const float dt = 1f / 60f;
        Pump(serverStep, clientStep, dt, () => clientScene.Find("Visible")!.GetComponent<NetworkObject>()!.IsSpawned, 5000);

        serverScene.Find("Visible")!.Transform.Position = new Vector3(0f, 4f, 0f);
        serverScene.Find("Blocked")!.Transform.Position = new Vector3(0f, 7f, 0f);
        Pump(serverStep, clientStep, dt, () => clientScene.Find("Visible")!.Transform.Position.Y > 3.5f, 3000);

        Assert.InRange(clientScene.Find("Visible")!.Transform.Position.Y, 3.5f, 4.5f);
        Assert.InRange(clientScene.Find("Blocked")!.Transform.Position.Y, -0.5f, 0.5f); // filtered out: never updated

        ((IDisposable)server).Dispose();
        ((IDisposable)client).Dispose();
    }

    [Fact]
    public void LagCompensation_ReturnsAPastPosition()
    {
        var scene = new Scene();
        var step = new Stepper(scene);
        NetworkServer server = scene.CreateGameObject("Net").AddComponent<NetworkServer>();
        server.Port = 0;
        GameObject target = scene.CreateGameObject("Target");
        target.AddComponent<NetworkObject>();
        target.AddComponent<NetworkTransform>();
        scene.Start();
        step.Step(0f); // assigns the network id

        uint id = target.GetComponent<NetworkObject>()!.NetworkId;
        Assert.True(id != 0);

        const float dt = 1f / 60f;
        for (int i = 1; i <= 60; i++)
        {
            target.Transform.Position = new Vector3(i, 0f, 0f);
            step.Step(dt);
        }

        Assert.True(server.TryGetPastPosition(id, secondsAgo: 0f, out Vector3 now));
        Assert.InRange(now.X, 59f, 60f);
        Assert.True(server.TryGetPastPosition(id, secondsAgo: 0.5f, out Vector3 past));
        Assert.InRange(past.X, 28f, 32f); // about half a second ago

        ((IDisposable)server).Dispose();
    }

    private static Vector3 RemotePosition(Scene scene)
    {
        foreach (NetworkObject networkObject in scene.FindObjectsOfType<NetworkObject>())
        {
            if (networkObject.Role == NetworkRole.Remote)
            {
                return networkObject.Transform.Position;
            }
        }

        return Vector3.Zero;
    }

    private static void Pump(Stepper server, Stepper client, float dt, Func<bool> done, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < timeoutMs)
        {
            server.Step(dt);
            client.Step(dt);
            Thread.Sleep(4);
        }
    }
}
