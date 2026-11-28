// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI;

public static partial class V
{
    /// <summary>A line that slides when it does not fit (<see cref="Widgets.Marquee"/>); style the label as usual.</summary>
    public static Marquee Marquee(Label label) => new(label);

    /// <summary>A sliding body-text line following <paramref name="text"/>.</summary>
    public static Marquee Marquee(Func<string> text) => new(Text(text));
}
