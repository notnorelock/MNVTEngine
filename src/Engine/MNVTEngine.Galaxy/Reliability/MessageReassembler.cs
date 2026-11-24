// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

namespace MNVTEngine.Galaxy.Reliability;

/// <summary>
/// Reassembles messages that were split into fragments. Each fragment names its group, index and total count; when
/// every fragment of a group has arrived, the whole message is rebuilt. Sent reliably, all fragments arrive in order
/// and a group completes at once; sent unreliably, a lost fragment leaves a group incomplete, so the number of
/// partial groups is capped and the oldest is dropped to bound memory. Inconsistent fragments (a mismatched count or
/// message id, an out-of-range index) are ignored.
/// </summary>
internal sealed class MessageReassembler
{
    private sealed class Partial(ushort messageId, int count)
    {
        public ushort MessageId { get; } = messageId;
        public int Count { get; } = count;
        public byte[]?[] Parts { get; } = new byte[count][];
        public int Received { get; set; }
        public int TotalBytes { get; set; }
    }

    private readonly Dictionary<ushort, Partial> _groups = [];

    public Message? Add(ushort messageId, ushort group, ushort index, ushort count, ReadOnlySpan<byte> chunk)
    {
        if (count == 0 || count > GalaxyConstants.MaxFragmentsPerMessage || index >= count)
        {
            return null;
        }

        if (!_groups.TryGetValue(group, out Partial? partial))
        {
            if (_groups.Count >= GalaxyConstants.MaxFragmentGroups)
            {
                DropOldest();
            }

            partial = new Partial(messageId, count);
            _groups[group] = partial;
        }

        if (partial.Count != count || partial.MessageId != messageId)
        {
            return null; // A group id reused with inconsistent framing: ignore.
        }

        if (partial.Parts[index] is null)
        {
            partial.Parts[index] = chunk.ToArray();
            partial.Received++;
            partial.TotalBytes += chunk.Length;
            if (partial.TotalBytes > GalaxyConstants.MaxMessageSize)
            {
                _groups.Remove(group);
                return null;
            }
        }

        if (partial.Received != partial.Count)
        {
            return null;
        }

        _groups.Remove(group);
        return Build(partial);
    }

    private static Message Build(Partial partial)
    {
        byte[] body = new byte[partial.TotalBytes];
        int offset = 0;
        foreach (byte[]? part in partial.Parts)
        {
            part!.CopyTo(body, offset);
            offset += part.Length;
        }

        Message message = Message.Create(partial.MessageId);
        message.LoadFrom(body);
        return message;
    }

    private void DropOldest()
    {
        // Dictionary enumeration order is insertion order here (no removals interleaved before the cap is hit for a
        // given group), so the first entry is the oldest still-incomplete group.
        foreach (ushort key in _groups.Keys)
        {
            _groups.Remove(key);
            return;
        }
    }
}
