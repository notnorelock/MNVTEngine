// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

namespace MNVTEngine.Galaxy;

/// <summary>Protocol-wide limits and timings shared by the client and the server.</summary>
public static class GalaxyConstants
{
    /// <summary>Bumped whenever the wire format changes; a client and server must match to connect.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Largest UDP payload the protocol will send or accept, chosen to stay under a typical path MTU so a
    /// datagram is not fragmented by IP. Anything larger is dropped as malformed.</summary>
    public const int MaxDatagramSize = 1200;

    /// <summary>Largest application message body. One message travels in one datagram in this version
    /// (shortcut: no fragmentation yet; add it when a use case needs messages above one MTU).</summary>
    public const int MaxMessageSize = 1024;

    /// <summary>A connection with no datagram received for this long is considered lost.</summary>
    public const int TimeoutMilliseconds = 5000;

    /// <summary>When otherwise idle, a keep-alive is sent this often so the peer's timeout does not trip.</summary>
    public const int HeartbeatMilliseconds = 1000;

    /// <summary>A reliable message with no acknowledgement after this long is retransmitted.</summary>
    public const int RetransmitMilliseconds = 100;

    /// <summary>Upper bound on unacknowledged reliable messages per connection; further reliable sends are refused
    /// until acks free space, so a stalled peer cannot grow memory without bound.</summary>
    public const int MaxReliableInFlight = 512;

    /// <summary>Anti-replay window (in datagrams) for the encrypted transport; reordering within it is accepted,
    /// duplicates and anything older are dropped.</summary>
    public const int ReplayWindowSize = 1024;
}
