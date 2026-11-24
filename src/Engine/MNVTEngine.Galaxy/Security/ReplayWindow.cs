// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

namespace MNVTEngine.Galaxy.Security;

/// <summary>
/// Sliding-window anti-replay over the monotonically increasing per-datagram counter, as in IPsec/DTLS. Reordering
/// within the window is accepted; a counter already seen, or older than the window, is rejected. Caller checks with
/// <see cref="CanAccept"/> before authenticating and commits with <see cref="Commit"/> only after the datagram's tag
/// verifies, so forged datagrams cannot advance the window.
/// </summary>
internal sealed class ReplayWindow(int size = GalaxyConstants.ReplayWindowSize)
{
    private readonly bool[] _seen = new bool[size];
    private ulong _highest;
    private bool _any;

    public bool CanAccept(ulong counter)
    {
        if (!_any || counter > _highest)
        {
            return true;
        }

        if (counter + (ulong)_seen.Length <= _highest)
        {
            return false; // Older than the window.
        }

        return !_seen[counter % (ulong)_seen.Length];
    }

    public void Commit(ulong counter)
    {
        int window = _seen.Length;
        if (!_any)
        {
            _any = true;
            _highest = counter;
            _seen[counter % (ulong)window] = true;
            return;
        }

        if (counter > _highest)
        {
            if (counter - _highest >= (ulong)window)
            {
                Array.Clear(_seen);
            }
            else
            {
                for (ulong i = _highest + 1; i < counter; i++)
                {
                    _seen[i % (ulong)window] = false;
                }
            }

            _highest = counter;
        }

        _seen[counter % (ulong)window] = true;
    }
}
