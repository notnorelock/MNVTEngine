// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI;

/// <summary>Loading: skeleton placeholders and the region that swaps them for content.</summary>
public static partial class V
{
    /// <summary>
    /// Shows <paramref name="skeleton"/> while <paramref name="state"/> is loading, then <paramref name="content"/>,
    /// <paramref name="empty"/> or <paramref name="failed"/> (defaults: a plain empty state, a "could not load" one).
    /// See <see cref="LoadingRegion"/>.
    /// </summary>
    public static LoadingRegion Loading(Func<LoadState> state, Func<UIElement> skeleton, Func<UIElement> content, Func<UIElement>? empty = null, Func<UIElement>? failed = null) =>
        new(state, skeleton, content, empty, failed);

    /// <summary>A skeleton box (cards, images, buttons) of a size.</summary>
    public static Skeleton Skeleton(UIUnit width, UIUnit height) => new Skeleton().Size(width, height);

    /// <summary>A round skeleton: an avatar or an icon.</summary>
    public static Skeleton SkeletonCircle(float size) => new Skeleton { Shape = SkeletonShape.Circle }.Size(size, size);

    /// <summary>A line of text that is coming, <paramref name="widthPercent"/> of the row wide, as tall as body text's x-height band.</summary>
    public static Skeleton SkeletonText(float widthPercent = 100f, float height = 10f) =>
        new Skeleton { Shape = SkeletonShape.Text }.Size(UIUnit.Percent(widthPercent), height);

    /// <summary>
    /// A list item that is coming: an avatar circle (unless <paramref name="avatar"/> is 0) and
    /// <paramref name="lines"/> text lines, the later ones shorter, like a name over a status.
    /// </summary>
    public static UIElement SkeletonRow(float avatar = 32f, int lines = 2)
    {
        var text = Column([.. Enumerable.Range(0, Math.Max(1, lines)).Select(i => SkeletonText(i == 0 ? 55f : 35f - (i * 5f)))]).Gap(6f).Grow();
        return Row(avatar > 0f ? SkeletonCircle(avatar) : null, text).Gap(Design.Current.Peek().Space3).FillWidth();
    }

    /// <summary><paramref name="count"/> rows of <see cref="SkeletonRow"/>: a list that is coming.</summary>
    public static UIElement SkeletonList(int count = 5, float avatar = 32f, int lines = 2) =>
        Column([.. Enumerable.Range(0, Math.Max(1, count)).Select(_ => SkeletonRow(avatar, lines))]).Gap(Design.Current.Peek().Space3).FillWidth();
}
