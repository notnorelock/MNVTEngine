// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

namespace MNVTEngine.Galaxy.Reliability;

/// <summary>
/// Reliable, in-order delivery over an unreliable datagram transport, using sequence numbers with selective
/// acknowledgement (a latest-received sequence plus a 32-bit bitfield of the ones before it) and timed
/// retransmission — the classic game-networking reliability scheme. The sender keeps unacknowledged messages and
/// resends them; the receiver delivers in send order and never acknowledges a message it had to drop for lack of
/// buffer, so a dropped message is simply retransmitted later. Sequence numbers are compared with signed 16-bit
/// wraparound arithmetic.
/// </summary>
internal sealed class ReliableChannel
{
    private readonly struct Outstanding(byte[] data, long lastSentMs)
    {
        public byte[] Data { get; } = data;
        public long LastSentMs { get; } = lastSentMs;
    }

    // Sender.
    private readonly Dictionary<ushort, Outstanding> _outstanding = [];
    private ushort _nextSequence;

    // Receiver: ordered delivery. The reliable stream always begins at sequence 0, so delivery starts there; a
    // sequence received before its predecessors waits in the reorder buffer rather than advancing the stream.
    private readonly Dictionary<ushort, byte[]> _reorder = [];
    private ushort _deliverExpected;

    // Receiver: acknowledgement state.
    private ushort _ackLatest;
    private uint _ackBits;
    private bool _haveAck;
    private bool _ackDirty;

    public int InFlight => _outstanding.Count;

    private static bool Newer(ushort a, ushort b) => (short)(a - b) > 0;

    /// <summary>Assigns the next sequence number and records the message for retransmission. Returns false (without
    /// enqueuing) when too many messages are already unacknowledged.</summary>
    public bool TryEnqueue(ReadOnlySpan<byte> payload, long nowMs, out ushort sequence)
    {
        sequence = 0;
        if (_outstanding.Count >= GalaxyConstants.MaxReliableInFlight)
        {
            return false;
        }

        sequence = _nextSequence++;
        _outstanding[sequence] = new Outstanding(payload.ToArray(), nowMs);
        return true;
    }

    /// <summary>Returns the sequences whose retransmit timer has elapsed, resetting their timers. The caller resends
    /// each with <see cref="PayloadOf"/>.</summary>
    public List<ushort> CollectRetransmits(long nowMs)
    {
        List<ushort> due = [];
        foreach ((ushort sequence, Outstanding entry) in _outstanding)
        {
            if (nowMs - entry.LastSentMs >= GalaxyConstants.RetransmitMilliseconds)
            {
                due.Add(sequence);
            }
        }

        foreach (ushort sequence in due)
        {
            _outstanding[sequence] = new Outstanding(_outstanding[sequence].Data, nowMs);
        }

        return due;
    }

    public byte[]? PayloadOf(ushort sequence) => _outstanding.TryGetValue(sequence, out var entry) ? entry.Data : null;

    /// <summary>Clears every message the peer acknowledged.</summary>
    public void ProcessAck(ushort ackLatest, uint ackBits)
    {
        _outstanding.Remove(ackLatest);
        for (int i = 0; i < 32; i++)
        {
            if ((ackBits & (1u << i)) != 0)
            {
                _outstanding.Remove((ushort)(ackLatest - 1 - i));
            }
        }
    }

    /// <summary>Takes a received reliable message. Appends the messages now deliverable, in send order, to
    /// <paramref name="delivered"/>. A far-future message that would overflow the reorder buffer is ignored and not
    /// acknowledged, so the sender retransmits it later.</summary>
    public void Receive(ushort sequence, ReadOnlySpan<byte> payload, List<byte[]> delivered)
    {
        if (!Newer(sequence, (ushort)(_deliverExpected - 1)))
        {
            RecordAck(sequence); // Already delivered; acknowledge again in case an earlier ack was lost.
            return;
        }

        if (sequence == _deliverExpected)
        {
            delivered.Add(payload.ToArray());
            _deliverExpected++;
            while (_reorder.Remove(_deliverExpected, out byte[]? buffered))
            {
                delivered.Add(buffered);
                _deliverExpected++;
            }

            RecordAck(sequence);
            return;
        }

        BufferOrDrop(sequence, payload);
    }

    private void BufferOrDrop(ushort sequence, ReadOnlySpan<byte> payload)
    {
        if (_reorder.ContainsKey(sequence))
        {
            RecordAck(sequence);
            return;
        }

        if (_reorder.Count >= GalaxyConstants.MaxReliableInFlight)
        {
            return; // Buffer full: do not acknowledge, so this is retransmitted and accepted once the gap closes.
        }

        _reorder[sequence] = payload.ToArray();
        RecordAck(sequence);
    }

    private void RecordAck(ushort sequence)
    {
        _ackDirty = true;
        if (!_haveAck)
        {
            _haveAck = true;
            _ackLatest = sequence;
            _ackBits = 0;
            return;
        }

        int diff = (short)(sequence - _ackLatest);
        if (diff > 0)
        {
            _ackBits = diff >= 32 ? 0u : (_ackBits << diff) | (1u << (diff - 1));
            _ackLatest = sequence;
        }
        else
        {
            int back = -diff;
            if (back is >= 1 and <= 32)
            {
                _ackBits |= 1u << (back - 1);
            }
        }
    }

    /// <summary>Reports whether an acknowledgement is pending since the last call and, if so, its contents.</summary>
    public bool TryTakeAck(out ushort ackLatest, out uint ackBits)
    {
        ackLatest = _ackLatest;
        ackBits = _ackBits;
        bool pending = _ackDirty;
        _ackDirty = false;
        return pending;
    }
}
