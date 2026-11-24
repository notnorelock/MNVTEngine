// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Net;

namespace MNVTEngine.Galaxy.Transport;

/// <summary>
/// The lowest layer: a non-blocking datagram socket. The client and server poll <see cref="TryReceive"/> each tick and
/// send with <see cref="Send"/>. Abstracted so the reliability and crypto layers can be tested over an in-memory
/// channel that drops, reorders and duplicates datagrams, without real sockets.
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>Binds to a local endpoint and returns the actual one (a port of 0 is assigned by the OS).</summary>
    IPEndPoint Bind(IPEndPoint local);

    /// <summary>Takes one pending datagram into <paramref name="buffer"/>. Returns false when none is waiting; never
    /// blocks. A datagram larger than the buffer is discarded (returns false for it).</summary>
    bool TryReceive(Span<byte> buffer, out int length, out IPEndPoint remote);

    /// <summary>Sends one datagram. Best effort: UDP gives no delivery guarantee.</summary>
    void Send(ReadOnlySpan<byte> data, IPEndPoint remote);
}
