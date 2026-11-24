// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

namespace MNVTEngine.Galaxy.Tests;

public sealed class MessageTests
{
    [Fact]
    public void WritesAndReadsEveryType_InOrder()
    {
        Message message = Message.Create(42);
        message.Write((byte)7).Write(true).Write((short)-3).Write((ushort)9)
            .Write(-123456).Write(123456u).Write(-9_000_000_000L).Write(9_000_000_000UL)
            .Write(3.5f).Write(2.25d).Write("zażółć gęślą jaźń").Write(new byte[] { 1, 2, 3 });

        Assert.Equal(42, message.Id);
        Assert.Equal((byte)7, message.ReadByte());
        Assert.True(message.ReadBool());
        Assert.Equal((short)-3, message.ReadShort());
        Assert.Equal((ushort)9, message.ReadUShort());
        Assert.Equal(-123456, message.ReadInt());
        Assert.Equal(123456u, message.ReadUInt());
        Assert.Equal(-9_000_000_000L, message.ReadLong());
        Assert.Equal(9_000_000_000UL, message.ReadULong());
        Assert.Equal(3.5f, message.ReadFloat());
        Assert.Equal(2.25d, message.ReadDouble());
        Assert.Equal("zażółć gęślą jaźń", message.ReadString());
        Assert.Equal(new byte[] { 1, 2, 3 }, message.ReadBytes());
        Assert.False(message.Failed);
    }

    [Fact]
    public void ReadingPastTheEnd_SetsFailed_AndReturnsDefault()
    {
        Message message = Message.Create();
        message.Write(1);

        Assert.Equal(1, message.ReadInt());
        Assert.Equal(0, message.ReadInt()); // Nothing left.
        Assert.True(message.Failed);
    }

    [Fact]
    public void Release_RecyclesAndResets()
    {
        Message first = Message.Create(1);
        first.Write(5);
        first.Release();

        Message second = Message.Create(2);
        Assert.Equal(0, second.Length);
        Assert.Equal(2, second.Id);
        Assert.False(second.Failed);
    }
}
