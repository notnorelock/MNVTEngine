// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

namespace MNVTEngine.Galaxy.Server;

/// <summary>Configuration for a <see cref="GameServer"/>.</summary>
public sealed class GameServerOptions
{
    /// <summary>UDP port to listen on. 0 lets the OS choose (useful in tests).</summary>
    public int Port { get; set; } = 7777;

    /// <summary>Maximum number of connected clients. Further connection attempts are refused as server-full.</summary>
    public int MaxClients { get; set; } = 64;

    /// <summary>Simulation ticks per second. The loop keeps this rate as closely as the clock allows.</summary>
    public int TickRate { get; set; } = 30;

    /// <summary>A human-readable name for logs.</summary>
    public string Name { get; set; } = "MNVTEngine Server";

    internal void Validate()
    {
        if (Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port must be between 0 and 65535.");
        }

        if (MaxClients < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxClients), MaxClients, "MaxClients must be at least 1.");
        }

        if (TickRate < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(TickRate), TickRate, "TickRate must be at least 1.");
        }
    }
}
