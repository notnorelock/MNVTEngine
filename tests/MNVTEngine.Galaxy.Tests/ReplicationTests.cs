// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Diagnostics;
using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;

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
