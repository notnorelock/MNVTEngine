// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using System.Globalization;
using MNVTEngine.Galaxy;
using MNVTEngine.Galaxy.Server;

// A minimal standalone dedicated server: it relays every client's message to all connected clients. It runs as its own
// process, with no engine, editor, window or graphics — just the networking layer. Build your game's client with
// MNVTEngine.Galaxy (or the NetworkClient component) and point it at this server's host and port.
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true; // Let the loop shut down cleanly instead of killing the process.
    stopping.Cancel();
};

int port = args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 7777;
using var server = new RelayServer(new GameServerOptions { Port = port, TickRate = 30, Name = "Galaxy Relay" });
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Galaxy relay server on UDP {port}. Press Ctrl+C to stop."));
server.Run(stopping.Token);
Console.WriteLine("Stopped.");

/// <summary>Relays each client's message to every connected client, tagged with the sender's id.</summary>
internal sealed class RelayServer(GameServerOptions options) : GameServer(options)
{
    private const int ChatMessageId = 1;

    protected override void OnClientConnected(uint clientId) =>
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Client {clientId} connected ({Clients.Count} online)."));

    protected override void OnClientDisconnected(uint clientId, DisconnectReason reason) =>
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Client {clientId} disconnected: {reason}."));

    protected override void OnMessage(uint clientId, Message message)
    {
        string text = message.ReadString();
        Message relay = Message.Create(ChatMessageId);
        relay.Write(clientId).Write(text);
        SendToAll(relay);
        relay.Release();
    }
}
