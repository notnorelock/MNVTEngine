// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Buffers.Binary;
using System.Net;
using Microsoft.Extensions.Logging;
using MNVTEngine.Galaxy.Reliability;
using MNVTEngine.Galaxy.Security;
using MNVTEngine.Galaxy.Transport;

namespace MNVTEngine.Galaxy;

/// <summary>
/// One established, encrypted peer link. Frames application messages into reliable or unreliable payloads, seals each
/// datagram with the session crypto, acknowledges and retransmits reliable messages, sends keep-alives when idle and
/// reports a timeout. Driven entirely from <see cref="Tick"/> and <see cref="Receive"/> on a single thread; it owns
/// no thread of its own.
/// </summary>
internal sealed partial class Connection : IDisposable
{
    private const int HeaderSize = 13; // type (1) + connection id (4) + counter (8)

    private readonly ITransport _transport;
    private readonly SessionCrypto _crypto;
    private readonly ReliableChannel _reliable = new();
    private readonly MessageReassembler _reassembler = new();
    private readonly List<byte[]> _delivered = [];
    private readonly ILogger _logger;

    private long _lastReceiveMs;
    private long _lastSendMs;
    private ushort _fragmentGroup;

    public Connection(uint connectionId, IPEndPoint remote, ITransport transport, SessionCrypto crypto, long nowMs, ILogger logger)
    {
        ConnectionId = connectionId;
        Remote = remote;
        _transport = transport;
        _crypto = crypto;
        _logger = logger;
        _lastReceiveMs = nowMs;
        _lastSendMs = nowMs;
    }

    public uint ConnectionId { get; }

    public IPEndPoint Remote { get; }

    /// <summary>Queues a message for the peer. A body over <see cref="GalaxyConstants.MaxMessageSize"/>, or a reliable
    /// send while the in-flight window is full, is dropped with a logged warning rather than throwing on the hot path.</summary>
    public void Send(Message message, DeliveryMethod method, long nowMs)
    {
        ReadOnlySpan<byte> body = message.WrittenSpan;
        if (body.Length > GalaxyConstants.MaxMessageSize)
        {
            LogMessageTooLarge(_logger, body.Length, GalaxyConstants.MaxMessageSize);
            return;
        }

        if ((uint)message.Id > ushort.MaxValue)
        {
            LogMessageIdOutOfRange(_logger, message.Id);
            return;
        }

        ushort id = (ushort)message.Id;
        if (body.Length <= GalaxyConstants.MaxFragmentPayload)
        {
            Span<byte> app = stackalloc byte[3 + body.Length];
            app[0] = (byte)AppKind.Whole;
            BinaryPrimitives.WriteUInt16LittleEndian(app.Slice(1, 2), id);
            body.CopyTo(app[3..]);
            DispatchApp(app, method, nowMs);
            return;
        }

        SendFragmented(id, body, method, nowMs);
    }

    private void SendFragmented(ushort id, ReadOnlySpan<byte> body, DeliveryMethod method, long nowMs)
    {
        int payload = GalaxyConstants.MaxFragmentPayload;
        int count = (body.Length + payload - 1) / payload;
        if (method == DeliveryMethod.Reliable && _reliable.InFlight + count > GalaxyConstants.MaxReliableInFlight)
        {
            LogReliableOverflow(_logger, GalaxyConstants.MaxReliableInFlight);
            return;
        }

        ushort group = _fragmentGroup++;
        Span<byte> envelope = stackalloc byte[9 + payload];
        envelope[0] = (byte)AppKind.Fragment;
        BinaryPrimitives.WriteUInt16LittleEndian(envelope.Slice(1, 2), id);
        BinaryPrimitives.WriteUInt16LittleEndian(envelope.Slice(3, 2), group);
        BinaryPrimitives.WriteUInt16LittleEndian(envelope.Slice(7, 2), (ushort)count);
        for (int i = 0; i < count; i++)
        {
            int offset = i * payload;
            int size = Math.Min(payload, body.Length - offset);
            BinaryPrimitives.WriteUInt16LittleEndian(envelope.Slice(5, 2), (ushort)i);
            body.Slice(offset, size).CopyTo(envelope[9..]);
            DispatchApp(envelope[..(9 + size)], method, nowMs);
        }
    }

    private void DispatchApp(ReadOnlySpan<byte> app, DeliveryMethod method, long nowMs)
    {
        if (method == DeliveryMethod.Reliable)
        {
            if (!_reliable.TryEnqueue(app, nowMs, out ushort sequence))
            {
                LogReliableOverflow(_logger, GalaxyConstants.MaxReliableInFlight);
                return;
            }

            SendReliableFrame(sequence, app, nowMs);
        }
        else
        {
            SendUnreliableFrame(app, nowMs);
        }
    }

    /// <summary>Sends a graceful disconnect to the peer (best effort).</summary>
    public void SendDisconnect(long nowMs)
    {
        Span<byte> inner = stackalloc byte[2];
        inner[0] = (byte)FrameKind.Control;
        inner[1] = (byte)ControlOpcode.Disconnect;
        SealAndSend(inner, nowMs);
    }

    /// <summary>Advances timers: retransmits, pending acknowledgements, keep-alive. Returns false when the peer has
    /// timed out and the connection must be dropped.</summary>
    public bool Tick(long nowMs)
    {
        foreach (ushort sequence in _reliable.CollectRetransmits(nowMs))
        {
            byte[]? payload = _reliable.PayloadOf(sequence);
            if (payload is not null)
            {
                SendReliableFrame(sequence, payload, nowMs);
            }
        }

        if (_reliable.TryTakeAck(out ushort ackLatest, out uint ackBits))
        {
            SendAckFrame(ackLatest, ackBits, nowMs);
        }

        if (nowMs - _lastSendMs >= GalaxyConstants.HeartbeatMilliseconds)
        {
            Span<byte> inner = stackalloc byte[2];
            inner[0] = (byte)FrameKind.Control;
            inner[1] = (byte)ControlOpcode.KeepAlive;
            SealAndSend(inner, nowMs);
        }

        return nowMs - _lastReceiveMs < GalaxyConstants.TimeoutMilliseconds;
    }

    /// <summary>Decrypts and processes one payload datagram. Application messages are appended to
    /// <paramref name="inbound"/>; <paramref name="remoteClosed"/> is set if the peer asked to disconnect. A datagram
    /// that fails authentication or replay is silently dropped.</summary>
    public void Receive(ReadOnlySpan<byte> datagram, long nowMs, List<Message> inbound, out bool remoteClosed)
    {
        remoteClosed = false;
        if (datagram.Length < HeaderSize + SessionCrypto.TagSize)
        {
            return;
        }

        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(datagram.Slice(5, 8));
        Span<byte> plaintext = stackalloc byte[GalaxyConstants.MaxDatagramSize];
        if (!_crypto.Open(counter, datagram[HeaderSize..], plaintext, datagram[..5], out int length))
        {
            return;
        }

        _lastReceiveMs = nowMs;
        ParseFrame(plaintext[..length], inbound, ref remoteClosed);
    }

    private void ParseFrame(ReadOnlySpan<byte> frame, List<Message> inbound, ref bool remoteClosed)
    {
        if (frame.IsEmpty)
        {
            return;
        }

        switch ((FrameKind)frame[0])
        {
            case FrameKind.Control:
                if (frame.Length >= 2 && (ControlOpcode)frame[1] == ControlOpcode.Disconnect)
                {
                    remoteClosed = true;
                }

                break;

            case FrameKind.Unreliable:
                AddApplicationMessage(frame[1..], inbound);
                break;

            case FrameKind.Reliable:
                if (frame.Length >= 3)
                {
                    ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(1, 2));
                    _reliable.Receive(sequence, frame[3..], _delivered);
                    foreach (byte[] payload in _delivered)
                    {
                        AddApplicationMessage(payload, inbound);
                    }

                    _delivered.Clear();
                }

                break;

            case FrameKind.Ack:
                if (frame.Length >= 7)
                {
                    ushort ackLatest = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(1, 2));
                    uint ackBits = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(3, 4));
                    _reliable.ProcessAck(ackLatest, ackBits);
                }

                break;

            default:
                break;
        }
    }

    private void AddApplicationMessage(ReadOnlySpan<byte> payload, List<Message> inbound)
    {
        if (payload.Length < 3)
        {
            return;
        }

        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(1, 2));
        if ((AppKind)payload[0] == AppKind.Whole)
        {
            Message message = Message.Create(id);
            message.LoadFrom(payload[3..]);
            inbound.Add(message);
            return;
        }

        if (payload.Length < 9)
        {
            return;
        }

        ushort group = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(3, 2));
        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(5, 2));
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(7, 2));
        if (_reassembler.Add(id, group, index, count, payload[9..]) is { } whole)
        {
            inbound.Add(whole);
        }
    }

    private void SendReliableFrame(ushort sequence, ReadOnlySpan<byte> app, long nowMs)
    {
        Span<byte> inner = stackalloc byte[3 + app.Length];
        inner[0] = (byte)FrameKind.Reliable;
        BinaryPrimitives.WriteUInt16LittleEndian(inner.Slice(1, 2), sequence);
        app.CopyTo(inner[3..]);
        SealAndSend(inner, nowMs);
    }

    private void SendUnreliableFrame(ReadOnlySpan<byte> app, long nowMs)
    {
        Span<byte> inner = stackalloc byte[1 + app.Length];
        inner[0] = (byte)FrameKind.Unreliable;
        app.CopyTo(inner[1..]);
        SealAndSend(inner, nowMs);
    }

    private void SendAckFrame(ushort ackLatest, uint ackBits, long nowMs)
    {
        Span<byte> inner = stackalloc byte[7];
        inner[0] = (byte)FrameKind.Ack;
        BinaryPrimitives.WriteUInt16LittleEndian(inner.Slice(1, 2), ackLatest);
        BinaryPrimitives.WriteUInt32LittleEndian(inner.Slice(3, 4), ackBits);
        SealAndSend(inner, nowMs);
    }

    private void SealAndSend(ReadOnlySpan<byte> inner, long nowMs)
    {
        Span<byte> datagram = stackalloc byte[HeaderSize + inner.Length + SessionCrypto.TagSize];
        datagram[0] = (byte)PacketType.Payload;
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.Slice(1, 4), ConnectionId);
        int sealedLength = _crypto.Seal(inner, datagram[HeaderSize..], datagram[..5], out ulong counter);
        BinaryPrimitives.WriteUInt64BigEndian(datagram.Slice(5, 8), counter);
        _transport.Send(datagram[..(HeaderSize + sealedLength)], Remote);
        _lastSendMs = nowMs;
    }

    public void Dispose() => _crypto.Dispose();

    [LoggerMessage(EventId = 9100, Level = LogLevel.Warning, Message = "Dropping a message of {Size} bytes: over the {Limit}-byte limit")]
    private static partial void LogMessageTooLarge(ILogger logger, int size, int limit);

    [LoggerMessage(EventId = 9101, Level = LogLevel.Warning, Message = "Dropping a message with id {Id}: a message id must be 0..65535")]
    private static partial void LogMessageIdOutOfRange(ILogger logger, int id);

    [LoggerMessage(EventId = 9102, Level = LogLevel.Warning, Message = "Dropping a reliable message: {Limit} are already in flight unacknowledged")]
    private static partial void LogReliableOverflow(ILogger logger, int limit);
}
