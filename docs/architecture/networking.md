# Networking (MNVTEngine.Galaxy)

Galaxy is the engine's client/server networking: a from-scratch reliable-UDP transport with an encrypted, authenticated
wire protocol and a byte-oriented message API, plus a headless framework for dedicated game servers. It is built for
real-time games — low latency, a mix of reliable and unreliable traffic — in the spirit of lightweight game-networking
libraries, with encryption and validation on by default.

Two projects, both pure managed and headless (they reference neither graphics, platform nor assets, so a dedicated
server can never reach for a file or device that only a client has):

| Project | Role |
|---|---|
| [`MNVTEngine.Galaxy`](../../src/Engine/MNVTEngine.Galaxy) | The protocol: `GalaxyClient`, `GalaxyServer`, the `Message` reader/writer, the RUDP transport, the handshake and the session crypto. |
| [`MNVTEngine.Galaxy.Server`](../../src/Engine/MNVTEngine.Galaxy.Server) | `GameServer`: a dedicated-server framework — a fixed-rate loop with lifecycle and message hooks, each guarded so one bad message cannot crash the server. |

It is the `networking` module (auto activation): a build that uses the Galaxy API ships it, one that does not leaves it
out. It depends only on `core`.

## Using it

A client pumps the socket once per frame; a server is usually driven by the dedicated-server loop.

```csharp
// Client (in the game loop)
var client = new GalaxyClient();
client.Connected += () => { var m = Message.Create(1); m.Write("hi"); client.Send(m); };
client.MessageReceived += m => Handle(m);   // the message is valid only inside the handler
client.Connect("127.0.0.1", 7777);
// each frame:
client.Tick();

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

Everything after the handshake is one datagram per frame, each at most 1200 bytes (under a typical path MTU, so IP never
fragments it). A payload datagram is:

```
[type:1][connection id:4][counter:8][ AES-GCM( frame ) + tag:16 ]
```

The handshake is three small, unencrypted datagrams (there is nothing secret before the shared key exists):

- **ConnectRequest** — client → server: protocol version and the client's ephemeral public key.
- **ConnectAccept** — server → client: an assigned connection id and the server's ephemeral public key.
- **ConnectReject** — server → client: a reason (protocol mismatch, server full, malformed).

Both ends then derive the session keys and all later datagrams are encrypted. Parsing is strict and bounded at every
step: a malformed field fails the parse, it never throws on the hot path, and a datagram that does not authenticate is
dropped.

## Security

- **Key exchange.** A fresh elliptic-curve Diffie-Hellman key pair (NIST P-256) per connection; the shared secret runs
  through HKDF-SHA256 into two AES-GCM keys and two nonce salts, one per direction, so the two directions never share a
  (key, nonce) pair. A new key pair per connection gives each session its own keys.
- **Encryption and integrity.** Every payload datagram is sealed with AES-GCM: encrypted and authenticated, with the
  clear header (type and connection id) as associated data and the per-datagram counter in the nonce. Tampering with the
  ciphertext, the header or the counter fails the tag and the datagram is dropped.
- **Anti-replay.** A sliding window over the per-datagram counter (DTLS/IPsec style) accepts reordering within the
  window and rejects duplicates and anything older. The window advances only after a datagram authenticates, so forged
  datagrams cannot move it.
- **Validation and flood resistance.** Strict size and field checks everywhere; a fixed datagram ceiling; a per-second
  cap on brand-new handshakes; a connected client's payload is accepted only from the source address its handshake came
  from, so a spoofed source is dropped and the server answers nothing it did not have to.
- **No home-grown cryptography.** Every primitive is from the .NET base class library (`ECDiffieHellman`, `HKDF`,
  `AesGcm`).

All cryptography is handled by the transport, below the message layer, so game code never sees it.

## Reliability

Inside each encrypted datagram is one frame: a control message (keep-alive, disconnect), an unreliable payload, a
reliable payload, or an acknowledgement.

- **Unreliable** — sent once, may be lost or reordered. For state that is replaced every tick (positions, inputs).
- **Reliable** — retransmitted until acknowledged and delivered to the handler in send order. Sequence numbers with
  selective acknowledgement (a latest-received sequence plus a 32-bit bitfield) and a retransmit timer — the classic
  game-networking reliability scheme. The reliable stream begins at sequence 0; a message that arrives before its
  predecessors waits in a reorder buffer, and the receiver never acknowledges a message it had to drop for lack of
  buffer, so a dropped one is simply retransmitted. A cap on unacknowledged messages bounds memory.

Keep-alives are sent when a connection is otherwise idle; a connection with no datagram for the timeout is dropped.

## The dedicated server

`GameServer` wraps `GalaxyServer` in a fixed-timestep loop. `Run` binds the socket, then each tick pumps the network
(firing the connection and message hooks) and calls `OnTick`, holding the configured tick rate. Every hook runs inside a
guard that logs and swallows exceptions (except out-of-memory): a malformed message or a bug in one handler is isolated
and the server keeps serving. Because the framework depends only on the networking layer, a dedicated server has no
graphics, no window and no asset files to go wrong — the file and device errors a headless server has no business
hitting are absent by construction. Embed it in an external loop instead with `Start` + `TickOnce`.

## Obfuscation

Message dispatch is by numeric id, and the protocol uses no reflection on type or member names, so obfuscating a game
(see [obfuscation](obfuscation.md)) does not affect networking: the renamed assembly talks to an unrenamed one over the
same wire format. The payload is encrypted on the wire regardless of whether the assembly is obfuscated.

## Limits and future work

This is the first milestone — the transport, the message API, the security layer and the dedicated-server framework.
Known limits, each a deliberate scope choice:

- **One datagram per message.** A message body is capped at one MTU; there is no fragmentation yet. Add it when a use
  case needs larger reliable messages (initial world state).
- **No server identity.** The handshake gives confidentiality and forward secrecy but does not authenticate the server
  to the client, so it does not stop a man-in-the-middle that can intercept the handshake. Pin a server public key to
  close this.
- **Basic flood resistance.** A per-second handshake cap and the source-address check, but no connection cookies or
  per-IP limits yet.
- **No state replication.** Galaxy moves messages; snapshotting and interpolation of game state sit above it, for a
  later milestone.

See [the architecture overview](overview.md) for how modules fit together and [versioning](versioning.md) for how a
build is identified.
