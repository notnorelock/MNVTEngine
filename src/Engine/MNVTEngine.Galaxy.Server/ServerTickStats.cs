// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.Galaxy.Server;

/// <summary>How long a <see cref="GameServer"/>'s ticks took (<see cref="GameServer.Timings"/>), in milliseconds.</summary>
/// <param name="Ticks">Ticks measured.</param>
/// <param name="LastMs">The latest tick.</param>
/// <param name="AverageMs">The mean of the measured ticks.</param>
/// <param name="MaxMs">The longest tick.</param>
/// <param name="OverBudget">Ticks longer than <paramref name="BudgetMs"/>: the loop fell behind its tick rate.</param>
/// <param name="BudgetMs">One tick at the configured tick rate.</param>
public sealed record ServerTickStats(long Ticks, double LastMs, double AverageMs, double MaxMs, long OverBudget, double BudgetMs)
{
    /// <summary>No ticks measured yet.</summary>
    public static ServerTickStats Empty(double budgetMs) => new(0, 0, 0, 0, 0, budgetMs);
}
