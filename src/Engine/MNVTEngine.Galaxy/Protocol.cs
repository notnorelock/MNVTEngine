// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

namespace MNVTEngine.Galaxy;

/// <summary>How a message is delivered. The transport always protects confidentiality and integrity; this only
/// chooses the reliability guarantee.</summary>
public enum DeliveryMethod : byte
{
    /// <summary>Sent once, not acknowledged, may be lost or arrive out of order. For data that is superseded every
    /// tick (positions, inputs).</summary>
    Unreliable = 0,

    /// <summary>Retransmitted until acknowledged and delivered to the handler in send order. For data that must
    /// arrive exactly once (chat, spawns, commands).</summary>
    Reliable = 1,
}

/// <summary>The kind of a datagram, in its first clear byte. Handshake datagrams are unencrypted by necessity; every
/// <see cref="Payload"/> datagram is encrypted and authenticated.</summary>
internal enum PacketType : byte
{
    ConnectRequest = 1,
    ConnectAccept = 2,
    ConnectReject = 3,
    Payload = 4,
}

/// <summary>Why a connection attempt was refused, carried in a <see cref="PacketType.ConnectReject"/>.</summary>
public enum RejectReason : byte
{
    None = 0,
    ProtocolMismatch = 1,
    ServerFull = 2,
    Malformed = 3,
}

/// <summary>Why a connection ended, reported to the application.</summary>
public enum DisconnectReason : byte
{
    /// <summary>The local side asked to disconnect.</summary>
    LocalRequest = 0,

    /// <summary>The peer sent a disconnect.</summary>
    RemoteRequest = 1,

    /// <summary>No datagram arrived within the timeout.</summary>
    Timeout = 2,

    /// <summary>The server refused the connection attempt (see <see cref="RejectReason"/>).</summary>
    Rejected = 3,

    /// <summary>The handshake or a later datagram failed validation or authentication.</summary>
    ProtocolError = 4,
}

/// <summary>The first byte of a decrypted payload frame, selecting how the rest is interpreted.</summary>
internal enum FrameKind : byte
{
    Control = 0,
    Unreliable = 1,
    Reliable = 2,
    Ack = 3,
}

/// <summary>Opcodes of a <see cref="FrameKind.Control"/> frame.</summary>
internal enum ControlOpcode : byte
{
    KeepAlive = 0,
    Disconnect = 1,
}

/// <summary>The first byte of an application chunk inside a reliable or unreliable frame: a whole small message, or
/// one fragment of a larger one.</summary>
internal enum AppKind : byte
{
    Whole = 0,
    Fragment = 1,
}
