using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>
/// Two-step layout, run by <see cref="UIContext"/> only when something changed:
/// <list type="number">
/// <item><b>Measure.</b> The size an element wants: fixed or percent sizes resolved against the parent, auto sizes from
/// content. Clamped to min/max and cached for the pass.</item>
/// <item><b>Arrange.</b> Each container places its children in its content box (bounds minus border and padding).</item>
/// </list>
/// Container modes:
/// <list type="bullet">
/// <item><b>Absolute.</b> Anchor, pivot and offset.</item>
/// <item><b>Row and Column.</b> Gaps, flex-grow, justify, and cross-axis align with stretch.</item>
/// <item><b>Grid.</b> Equal columns; rows as tall as their tallest cell.</item>
/// </list>
/// </summary>
public static class LayoutEngine
{
    [ThreadStatic]
    private static Dictionary<(UIElement, float, float), Vector2>? _measureCache;

    /// <summary>Lays out a whole tree in <paramref name="viewport"/> (the root fills it unless sized otherwise).</summary>
    public static void Layout(UIElement root, Rect viewport)
    {
        ArgumentNullException.ThrowIfNull(root);
        _measureCache ??= [];
        _measureCache.Clear();
        try
        {
            var size = Size(root, viewport.Width, viewport.Height, stretchWidth: root.Width.IsAuto, stretchHeight: root.Height.IsAuto);
            Arrange(root, new Rect(viewport.X + root.Margin.Left, viewport.Y + root.Margin.Top, size.X, size.Y), viewport);
        }
        finally
        {
            _measureCache.Clear();
        }
    }

    /// <summary>Outer size (margin box) an element wants in the given parent content size.</summary>
    public static Vector2 MeasureOuter(UIElement element, float parentWidth, float parentHeight)
    {
        var size = Size(element, parentWidth, parentHeight, false, false);
        return new Vector2(size.X + element.Margin.Horizontal, size.Y + element.Margin.Vertical);
    }

    /// <summary>Content size of a container from its children and layout mode (the default <see cref="UIElement.MeasureContent"/>).</summary>
    public static Vector2 MeasureChildren(UIElement element, float availableWidth, float availableHeight)
    {
        var children = element.Children.Where(c => c.Visible).ToList();
        if (children.Count == 0)
        {
            return Vector2.Zero;
        }

        var gaps = element.Gap * (children.Count - 1);
        switch (element.Layout)
        {
            case LayoutMode.Row:
            {
                var sizes = children.Select(c => MeasureOuter(c, availableWidth, availableHeight)).ToList();
                return new Vector2(sizes.Sum(s => s.X) + gaps, sizes.Max(s => s.Y));
            }

            case LayoutMode.Column:
            {
                var sizes = children.Select(c => MeasureOuter(c, availableWidth, availableHeight)).ToList();
                return new Vector2(sizes.Max(s => s.X), sizes.Sum(s => s.Y) + gaps);
            }

            case LayoutMode.Grid:
            {
                var columns = element.GridColumns;
                var cell = float.IsFinite(availableWidth) ? (availableWidth - (element.Gap * (columns - 1))) / columns : float.PositiveInfinity;
                var sizes = children.Select(c => MeasureOuter(c, cell, availableHeight)).ToList();
                var rows = (sizes.Count + columns - 1) / columns;
                var height = Enumerable.Range(0, rows).Sum(r => sizes.Skip(r * columns).Take(columns).Max(s => s.Y)) + (element.Gap * (rows - 1));
                return new Vector2((sizes.Max(s => s.X) * columns) + (element.Gap * (columns - 1)), height);
            }

            default:
            {
                var extent = Vector2.Zero;
                foreach (var child in children)
                {
                    var outer = MeasureOuter(child, availableWidth, availableHeight);
                    var offset = new Vector2(child.Left.Resolve(availableWidth) ?? 0f, child.Top.Resolve(availableHeight) ?? 0f);
                    extent = Vector2.Max(extent, outer + Vector2.Max(offset, Vector2.Zero));
                }

                return extent;
            }
        }
    }

    /// <summary>The default <see cref="UIElement.ArrangeChildren"/>: places children by the element's layout mode.</summary>
    public static void ArrangeChildren(UIElement element)
    {
        var content = element.ContentBounds;
        var children = element.Children.Where(c => c.Visible).ToList();
        switch (element.Layout)
        {
            case LayoutMode.Row:
            case LayoutMode.Column:
                ArrangeFlex(element, content, children, element.Layout == LayoutMode.Row);
                break;
            case LayoutMode.Grid:
                ArrangeGrid(element, content, children);
                break;
            default:
                foreach (var child in children)
                {
                    var size = Size(child, content.Width, content.Height, false, false);
                    var anchor = new Vector2(content.X + (content.Width * child.Anchor.X), content.Y + (content.Height * child.Anchor.Y));
                    var offset = new Vector2(child.Left.Resolve(content.Width) ?? 0f, child.Top.Resolve(content.Height) ?? 0f);
                    var position = anchor + offset - new Vector2(size.X * child.Pivot.X, size.Y * child.Pivot.Y) + new Vector2(child.Margin.Left, child.Margin.Top);
                    Arrange(child, new Rect(position.X, position.Y, size.X, size.Y), element.ChildClip);
                }

                break;
        }
    }

    /// <summary>Border-box size: resolved width/height, or content + padding + border when auto; stretch fills the cell.</summary>
    internal static Vector2 Size(UIElement element, float parentWidth, float parentHeight, bool stretchWidth, bool stretchHeight)
    {
        var key = (element, stretchWidth ? -parentWidth - 1f : parentWidth, stretchHeight ? -parentHeight - 1f : parentHeight);
        var cache = _measureCache ??= [];
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var style = element.ResolveStyle();
        var chrome = new Vector2(style.Padding.Horizontal + (2 * style.BorderWidth), style.Padding.Vertical + (2 * style.BorderWidth));
        var width = element.Width.Resolve(parentWidth) ?? (stretchWidth && float.IsFinite(parentWidth) ? parentWidth - element.Margin.Horizontal : (float?)null);
        var height = element.Height.Resolve(parentHeight) ?? (stretchHeight && float.IsFinite(parentHeight) ? parentHeight - element.Margin.Vertical : (float?)null);
        if (width is null || height is null)
        {
            var availableWidth = (width ?? (float.IsFinite(parentWidth) ? parentWidth - element.Margin.Horizontal : float.PositiveInfinity)) - chrome.X;
            var availableHeight = (height ?? (float.IsFinite(parentHeight) ? parentHeight - element.Margin.Vertical : float.PositiveInfinity)) - chrome.Y;
            var content = element.MeasureContent(Math.Max(0f, availableWidth), Math.Max(0f, availableHeight));
            width ??= content.X + chrome.X;
            height ??= content.Y + chrome.Y;
        }

        var size = new Vector2(
            Math.Clamp(width.Value, element.MinWidth, Math.Max(element.MinWidth, element.MaxWidth)),
            Math.Clamp(height.Value, element.MinHeight, Math.Max(element.MinHeight, element.MaxHeight)));
        cache[key] = size;
        return size;
    }

    /// <summary>Gives an element its border box, computes content and clip boxes, then arranges its children.</summary>
    internal static void Arrange(UIElement element, Rect bounds, Rect parentClip)
    {
        var style = element.ResolveStyle();
        element.Bounds = bounds;
        var inset = style.BorderWidth;
        element.ContentBounds = new Rect(
            bounds.X + inset + style.Padding.Left,
            bounds.Y + inset + style.Padding.Top,
            Math.Max(0f, bounds.Width - (2 * inset) - style.Padding.Horizontal),
            Math.Max(0f, bounds.Height - (2 * inset) - style.Padding.Vertical));
        element.PaintClip = parentClip;
        element.ClipBounds = Intersect(parentClip, bounds);
        element.ChildClip = element.Overflow == Overflow.Clip ? element.ClipBounds : parentClip;
        element.ArrangeChildren();
    }

    internal static Rect Intersect(Rect a, Rect b)
    {
        var x0 = MathF.Max(a.X, b.X);
        var y0 = MathF.Max(a.Y, b.Y);
        var x1 = MathF.Min(a.X + a.Width, b.X + b.Width);
        var y1 = MathF.Min(a.Y + a.Height, b.Y + b.Height);
        return new Rect(x0, y0, MathF.Max(0f, x1 - x0), MathF.Max(0f, y1 - y0));
    }

    private static void ArrangeFlex(UIElement element, Rect content, List<UIElement> children, bool row)
    {
        float Main(Vector2 v) => row ? v.X : v.Y;
        float Cross(Vector2 v) => row ? v.Y : v.X;
        var mainSize = row ? content.Width : content.Height;
        var crossSize = row ? content.Height : content.Width;

        // Main sizes: fixed, percent or measured; free space goes to flex-grow children.
        var stretch = children.Select(c => (c.AlignSelf ?? element.AlignItems) == Align.Stretch && (row ? c.Height : c.Width).IsAuto).ToList();
        var sizes = children.Select((c, i) => Size(c, content.Width, content.Height, !row && stretch[i], row && stretch[i])).ToList();
        var margins = children.Select(c => row ? c.Margin.Horizontal : c.Margin.Vertical).ToList();
        var used = sizes.Select(Main).Sum() + margins.Sum() + (element.Gap * Math.Max(0, children.Count - 1));
        var free = mainSize - used;
        var grow = children.Sum(c => c.FlexGrow);
        if (free > 0 && grow > 0)
        {
            for (var i = 0; i < children.Count; i++)
            {
                if (children[i].FlexGrow > 0)
                {
                    var extra = free * children[i].FlexGrow / grow;
                    var main = Math.Clamp(Main(sizes[i]) + extra, row ? children[i].MinWidth : children[i].MinHeight, row ? children[i].MaxWidth : children[i].MaxHeight);
                    sizes[i] = row ? new Vector2(main, sizes[i].Y) : new Vector2(sizes[i].X, main);
                }
            }

            free = 0;
        }

        var position = element.Justify switch
        {
            Justify.Center => Math.Max(0, free) / 2f,
            Justify.End => Math.Max(0, free),
            _ => 0f,
        };
        var spacing = element.Justify == Justify.SpaceBetween && children.Count > 1 ? Math.Max(0, free) / (children.Count - 1) : 0f;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var margin = child.Margin;
            var align = child.AlignSelf ?? element.AlignItems;
            var cross = Cross(sizes[i]);
            var crossMargin = row ? margin.Vertical : margin.Horizontal;
            var crossOffset = align switch
            {
                Align.Center => (crossSize - cross - crossMargin) / 2f,
                Align.End => crossSize - cross - crossMargin,
                _ => 0f,
            };
            position += row ? margin.Left : margin.Top;
            var rect = row
                ? new Rect(content.X + position, content.Y + margin.Top + crossOffset, sizes[i].X, sizes[i].Y)
                : new Rect(content.X + margin.Left + crossOffset, content.Y + position, sizes[i].X, sizes[i].Y);
            Arrange(child, rect, element.ChildClip);
            position += Main(sizes[i]) + (row ? margin.Right : margin.Bottom) + element.Gap + spacing;
        }
    }

    private static void ArrangeGrid(UIElement element, Rect content, List<UIElement> children)
    {
        var columns = element.GridColumns;
        var cellWidth = (content.Width - (element.Gap * (columns - 1))) / columns;
        var y = content.Y;
        for (var start = 0; start < children.Count; start += columns)
        {
            var rowChildren = children.Skip(start).Take(columns).ToList();
            var sizes = rowChildren.Select(c => Size(c, cellWidth, content.Height, c.Width.IsAuto, false)).ToList();
            var rowHeight = sizes.Select((s, i) => s.Y + rowChildren[i].Margin.Vertical).Max();
            for (var i = 0; i < rowChildren.Count; i++)
            {
                var child = rowChildren[i];
                var x = content.X + (i * (cellWidth + element.Gap)) + child.Margin.Left;
                Arrange(child, new Rect(x, y + child.Margin.Top, sizes[i].X, sizes[i].Y), element.ChildClip);
            }

            y += rowHeight + element.Gap;
        }
    }
}
