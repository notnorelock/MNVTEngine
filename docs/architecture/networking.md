# Networking (MNVTEngine.Galaxy)

Galaxy is the engine's client/server networking: a from-scratch reliable-UDP transport with an encrypted, authenticated
wire protocol, a byte-oriented message API, a headless framework for dedicated servers, and scene state replication.
It is built for real-time games — low latency, a mix of reliable and unreliable traffic — with encryption, server
authentication and validation on by default.

Three projects (the transport and server are pure managed and headless — no graphics, platform or assets, so a
dedicated server never reaches for a file or device only a client has):

| Project | Role |
|---|---|
| [`MNVTEngine.Galaxy`](../../src/Engine/MNVTEngine.Galaxy) | The protocol: `GalaxyClient`, `GalaxyServer`, the `Message` reader/writer, the RUDP transport, the handshake and the session crypto. |
| [`MNVTEngine.Galaxy.Server`](../../src/Engine/MNVTEngine.Galaxy.Server) | `GameServer`: a dedicated-server framework — a fixed-rate loop with lifecycle and message hooks, each guarded so one bad message cannot crash the server. |
| [`MNVTEngine.Galaxy.Replication`](../../src/Engine/MNVTEngine.Galaxy.Replication) | Scene state replication: `NetworkObject`, `NetworkTransform`, and the `NetworkClient` / `NetworkServer` driver components. Depends on Scenes. |

It is the `networking` module (auto activation): a build that uses the Galaxy API ships it, one that does not leaves it
out. It depends only on `core`.

## Using it

A client pumps the socket once per frame; a server is usually driven by the dedicated-server loop.

```csharp
// Raw client (in the game loop)
var client = new GalaxyClient();
client.Connected += () => { var m = Message.Create(1); m.Write("hi"); client.Send(m); };
client.MessageReceived += m => Handle(m);   // the message is valid only inside the handler
client.Connect("127.0.0.1", 7777);          // optionally pass the server's pinned key
// each frame: client.Tick();

// Dedicated server
sealed class MyServer(GameServerOptions o) : GameServer(o)
{
    protected override void OnMessage(uint clientId, Message m) { /* ... */ }
    protected override void OnTick(float dt) { /* simulate */ }
}
new MyServer(new GameServerOptions { Port = 7777, TickRate = 30 }).Run(cancellationToken);
```

Messages carry a numeric **id** (0..65535), not a type name, so a handler is selected by id and obfuscation that renames
game types never breaks dispatch. A `Message` is pooled: create it with `Message.Create(id)`, and a received one is
recycled after its handler returns.

## The wire protocol

Everything after the handshake is one frame per datagram, each at most 1200 bytes (under a typical path MTU, so IP never
fragments it). A payload datagram is:

```
[type:1][connection id:4][counter:8][ AES-GCM( frame ) + tag:16 ]
```

The handshake (protocol v2) is a few small, unencrypted datagrams — there is nothing secret before the shared key exists:

1. **ConnectRequest** — client → server: protocol version, the client's ephemeral public key, and (after a challenge) a cookie.
2. **ConnectChallenge** — server → client: a stateless cookie the client must echo.
3. **ConnectRequest** (again) — client → server: the same, now echoing the cookie.
4. **ConnectAccept** — server → client: a connection id, the server's ephemeral public key, and — when the server has an identity — its identity public key and a signature over both ephemeral keys.

(A **ConnectReject** carries a reason: protocol mismatch, server full, malformed.) Both ends then derive the session
keys and all later datagrams are encrypted. Parsing is strict and bounded at every step: a malformed field fails the
parse, it never throws on the hot path, and a datagram that does not authenticate is dropped.

## Security

- **Key exchange.** A fresh elliptic-curve Diffie-Hellman key pair (NIST P-256) per connection; the shared secret runs
  through HKDF-SHA256 into two AES-GCM keys and two nonce salts, one per direction. A new key pair per connection gives
  each session its own keys (forward secrecy).
- **Encryption and integrity.** Every payload datagram is sealed with AES-GCM, with the clear header as associated data
  and the per-datagram counter in the nonce. Tampering with the ciphertext, header or counter fails the tag and the
  datagram is dropped.
- **Server authentication (anti man-in-the-middle).** A server with a [`GalaxyIdentity`](../../src/Engine/MNVTEngine.Galaxy/Security/GalaxyIdentity.cs)
  (ECDSA P-256) signs the accept over both ephemeral keys. A client that **pins** the server's public key verifies the
  signature and refuses a mismatch or an unsigned server, which a man-in-the-middle cannot forge without the private
  key. Pinning is opt-in; unsigned connections stay confidential and forward-secret but do not detect a MITM.
- **Stateless connection cookie.** The server answers a first request with an HMAC cookie bound to the source address
  and ephemeral key and keeps no state until the client echoes it, blocking spoofed-source floods and reflection.
- **Anti-replay.** A sliding window over the per-datagram counter rejects duplicates and anything older than the
  window, advancing only after a datagram authenticates.
- **Validation and limits.** Strict size and field checks everywhere, a fixed datagram ceiling, a per-second handshake
  cap, a per-source connection cap, and a connected client's payload accepted only from the source address its
  handshake came from.
- **No home-grown cryptography.** Every primitive is from the .NET base class library.

Cryptography is handled by the transport, below the message layer, so game code never sees it.

## Reliability and fragmentation

Inside each encrypted datagram is one frame: a control message (keep-alive, disconnect), an unreliable payload, a
reliable payload, or an acknowledgement.

- **Unreliable** — sent once, may be lost or reordered. For state replaced every tick (positions, inputs).
- **Reliable** — retransmitted until acknowledged and delivered in send order, using sequence numbers with selective
  acknowledgement (a latest-received sequence plus a 32-bit bitfield) and a retransmit timer. The reliable stream
  begins at sequence 0; a message that arrives before its predecessors waits in a reorder buffer, and the receiver
  never acknowledges a message it dropped for lack of buffer, so a dropped one is retransmitted.
- **Fragmentation.** A message body above one datagram (up to 64 KB) is split into fragments, each in its own frame,
  and reassembled by the receiver; a partial group is bounded so lost unreliable fragments cannot grow memory.

Keep-alives are sent when a connection is idle; a connection with no datagram for the timeout is dropped.

## The dedicated server

`GameServer` wraps `GalaxyServer` in a fixed-timestep loop. `Run` binds the socket, then each tick pumps the network
(firing the connection and message hooks) and calls `OnTick`, holding the configured tick rate. Every hook runs inside a
guard that logs and swallows exceptions (except out-of-memory): a malformed message or a bug in one handler is isolated
and the server keeps serving. Because the framework depends only on the networking layer, a dedicated server has no
graphics, window or asset files to go wrong. Set `GameServerOptions.Identity` to authenticate it to pinning clients.

### Standalone servers

Two ways to run a server as its own process, outside the editor:

- **Run the game's own scene headlessly:** `mnvt server <project> [--port N] [--tick-rate N]` loads the project's
  content and C# code, simulates its startup scene — physics, scripts, everything — with no window, graphics device or
  audio, and hosts a `NetworkServer` (adding one if the scene has none). This is the dedicated server for a game built
  on the engine ([`DedicatedServerHost`](../../src/Tooling/MNVTEngine.Build/DedicatedServerHost.cs)).
- **Write your own:** a server is a plain console app subclassing `GameServer`. See
  [`samples/GalaxyServer`](../../samples/GalaxyServer) — a standalone relay server that references only the networking
  layer and nothing else of the engine.

## State replication

`MNVTEngine.Galaxy.Replication` replicates scene state, server-authoritative, as engine components:

- **`NetworkObject`** marks a replicated prefab instance; the server assigns its `NetworkId`. **`NetworkTransform`**
  replicates its object's transform (position, rotation and/or scale).
- **`NetworkServer`** (add it to a scene to host) opens a `GalaxyServer`, assigns ids, tells clients to spawn and
  despawn objects (reliable), and broadcasts a transform snapshot at a fixed rate (unreliable). A newly connected client
  receives the full world first.
- **`NetworkClient`** (the client side, usable from the editor) opens a `GalaxyClient`, instantiates the prefabs the
  server spawns, destroys despawned ones, and drives remote `NetworkTransform`s from the snapshot stream with buffered
  **interpolation**, which hides jitter and the loss of the unreliable snapshots. Game messages that are not replication
  are raised through its `MessageReceived` event.

The server's transforms are the source of truth; clients mirror them (`NetworkRole.Remote`). Beyond that:

- **Authored scene objects** replicate without a prefab: the spawn carries the object's scene id, and a client binds the
  object it already has from the same scene by that id, instantiating the prefab only when it has no such object.
- **Owner authority:** `NetworkServer.AssignOwnership` gives a client authority over an object (e.g. its own avatar).
  The owner drives that object locally and sends it to the server at a fixed rate; the server accepts it only from the
  real owner and re-broadcasts it, and authority reverts to the server when the owner leaves.
- **Bandwidth:** snapshots are **delta** — an object is sent only when its transform changed, with a periodic refresh so
  a missed unreliable update is corrected. With `NetworkServer.RelevanceRadius > 0`, **interest management** sends a
  client only the objects near its owned object (plus any `NetworkObject.AlwaysRelevant` or owned by it).

The in-game debug overlay has a **Network** panel showing each `NetworkClient`'s connection state, replicated-object
count and message counters.

## Obfuscation

Message dispatch is by numeric id and the protocol uses no reflection on type or member names, so obfuscating a game
does not affect networking. The payload is encrypted on the wire regardless.

## The rename map / identifying a build

See [versioning](versioning.md) for how a build is identified and [obfuscation](obfuscation.md) for how a shipped
build is protected.

## Limits and future work

What the current milestone does not do yet, each a deliberate scope choice:

- **No client-side prediction or lag compensation.** An owner's object is driven locally (so its own input feels
  instant), but non-owned objects are shown interpolated in the past; there is no prediction/reconciliation of
  server-authoritative objects, and the server does not rewind time to validate hits.
- **Delta is per-object, not per-field.** A snapshot skips unchanged objects, but a changed object sends its whole
  transform rather than only the changed fields, and there is no per-client baseline/ack delta encoding.
- **Interest management is radius-only.** Relevance is distance from a client's owned object (or always-relevant);
  there are no zones, cells or custom relevance rules, and spawn/despawn is not itself culled (only snapshot updates).
- **No listen server.** A host either runs a dedicated server or is a client; running both in one process (a player
  hosting) is not wired up.

See [the architecture overview](overview.md) for how modules fit together.
