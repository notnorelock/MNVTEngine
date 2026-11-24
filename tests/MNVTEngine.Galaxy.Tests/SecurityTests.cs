// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using MNVTEngine.Galaxy.Security;

namespace MNVTEngine.Galaxy.Tests;

public sealed class ReplayWindowTests
{
    [Fact]
    public void AcceptsNew_RejectsDuplicate_AcceptsReorderWithinWindow_RejectsTooOld()
    {
        var window = new ReplayWindow(64);

        Assert.True(window.CanAccept(10));
        window.Commit(10);

        Assert.False(window.CanAccept(10));      // Duplicate.
        Assert.True(window.CanAccept(9));         // Older but within the window (reordered).
        window.Commit(9);
        Assert.False(window.CanAccept(9));        // Now a duplicate too.

        window.Commit(200);                        // Jump far ahead.
        Assert.False(window.CanAccept(10));       // Older than the window now.
        Assert.True(window.CanAccept(201));
    }
}

public sealed class SessionCryptoTests
{
    private static readonly byte[] Aad = [4, 0, 1, 2, 3];

    [Fact]
    public void SealAndOpen_RoundTripsAcrossDirections()
    {
        (SessionCrypto client, SessionCrypto server) = Handshakes.Pair();
        byte[] plaintext = "secret payload"u8.ToArray();

        byte[] sealed1 = new byte[plaintext.Length + SessionCrypto.TagSize];
        int length = client.Seal(plaintext, sealed1, Aad, out ulong counter);

        byte[] opened = new byte[plaintext.Length];
        Assert.True(server.Open(counter, sealed1.AsSpan(0, length), opened, Aad, out int openedLength));
        Assert.Equal(plaintext, opened.AsSpan(0, openedLength).ToArray());
    }

    [Fact]
    public void Open_RejectsATamperedTag()
    {
        (SessionCrypto client, SessionCrypto server) = Handshakes.Pair();
        byte[] plaintext = [1, 2, 3, 4];
        byte[] blob = new byte[plaintext.Length + SessionCrypto.TagSize];
        int length = client.Seal(plaintext, blob, Aad, out ulong counter);
        blob[length - 1] ^= 0xFF; // Flip a tag bit.

        byte[] opened = new byte[plaintext.Length];
        Assert.False(server.Open(counter, blob.AsSpan(0, length), opened, Aad, out _));
    }

    [Fact]
    public void Open_RejectsAReplayedDatagram()
    {
        (SessionCrypto client, SessionCrypto server) = Handshakes.Pair();
        byte[] plaintext = [9, 9, 9];
        byte[] blob = new byte[plaintext.Length + SessionCrypto.TagSize];
        int length = client.Seal(plaintext, blob, Aad, out ulong counter);
        byte[] opened = new byte[plaintext.Length];

        Assert.True(server.Open(counter, blob.AsSpan(0, length), opened, Aad, out _));
        Assert.False(server.Open(counter, blob.AsSpan(0, length), opened, Aad, out _)); // Same counter again.
    }

    [Fact]
    public void Open_RejectsWrongAssociatedData()
    {
        (SessionCrypto client, SessionCrypto server) = Handshakes.Pair();
        byte[] plaintext = [5, 6];
        byte[] blob = new byte[plaintext.Length + SessionCrypto.TagSize];
        int length = client.Seal(plaintext, blob, Aad, out ulong counter);

        byte[] opened = new byte[plaintext.Length];
        Assert.False(server.Open(counter, blob.AsSpan(0, length), opened, [4, 0, 9, 9, 9], out _));
    }
}
