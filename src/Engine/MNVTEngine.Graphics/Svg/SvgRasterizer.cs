using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Svg;

/// <summary>What <see cref="SvgRasterizer.Render"/> produced, with the features it could not draw.</summary>
public sealed record SvgImage(Image Image, IReadOnlyList<string> Warnings);

/// <summary>
/// Turns an SVG document into pixels, in pure C# (no native library), for SVG textures and icons:
/// <list type="bullet">
/// <item>elements <c>svg</c>, <c>g</c>, <c>path</c>, <c>rect</c> (with rounded corners), <c>circle</c>, <c>ellipse</c>,
/// <c>line</c>, <c>polyline</c> and <c>polygon</c>;</item>
/// <item>every path command (lines, cubic and quadratic curves, arcs, relative and absolute), fill rules, fills and
/// strokes with widths, opacity (group, fill and stroke), colors (hex, <c>rgb()</c>, names), <c>transform</c> lists and
/// <c>viewBox</c>, as attributes or in <c>style</c>;</item>
/// <item>anti-aliasing (4 sub-scanlines with exact horizontal coverage).</item>
/// </list>
/// Gradients, patterns, text, images, filters, masks, clip paths and <c>use</c> are not drawn: each is named once in
/// <see cref="SvgImage.Warnings"/> (gradient fills use their first stop's color). DTDs are refused (untrusted input).
/// </summary>
public static class SvgRasterizer
{
    /// <summary>The largest side of a rendered image.</summary>
    public const int MaxSize = 8192;

    private const float CurveTolerance = 0.2f; // pixels: how far a flattened curve may stray from the real one

    /// <summary>True when the bytes look like an SVG document (text starting with an XML declaration or <c>&lt;svg</c>).</summary>
    public static bool IsSvg(ReadOnlySpan<byte> data)
    {
        var text = System.Text.Encoding.UTF8.GetString(data[..Math.Min(data.Length, 1024)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return text.StartsWith("<svg", StringComparison.Ordinal) || (text.StartsWith("<?xml", StringComparison.Ordinal) && text.Contains("<svg", StringComparison.Ordinal))
            || (text.StartsWith("<!--", StringComparison.Ordinal) && text.Contains("<svg", StringComparison.Ordinal));
    }

    /// <summary>Renders at the document's size (its <c>width</c>/<c>height</c>, else its <c>viewBox</c>) times <paramref name="scale"/>.</summary>
    /// <param name="tint">Multiplies every color (white artwork drawn in another color); null leaves colors as they are.</param>
    /// <exception cref="InvalidDataException">Not a valid SVG document, or a size of zero or above <see cref="MaxSize"/>.</exception>
    public static SvgImage Render(byte[] data, string sourceName, float scale = 1f, Color? tint = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!(scale > 0f) || !float.IsFinite(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale must be positive.");
        }

        XElement root;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            root = XDocument.Load(reader).Root ?? throw new InvalidDataException($"'{sourceName}' is empty.");
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"'{sourceName}' is not a valid SVG document: {ex.Message}", ex);
        }

        if (root.Name.LocalName != "svg")
        {
            throw new InvalidDataException($"'{sourceName}' is not an SVG document (its root is <{root.Name.LocalName}>).");
        }

        var viewBox = Numbers(root.Attribute("viewBox")?.Value);
        var width = Length(root.Attribute("width")?.Value) ?? (viewBox.Count == 4 ? viewBox[2] : 0f);
        var height = Length(root.Attribute("height")?.Value) ?? (viewBox.Count == 4 ? viewBox[3] : 0f);
        var pixelWidth = (int)MathF.Ceiling(width * scale);
        var pixelHeight = (int)MathF.Ceiling(height * scale);
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new InvalidDataException($"'{sourceName}' has no size: give the <svg> a width and height or a viewBox.");
        }

        if (pixelWidth > MaxSize || pixelHeight > MaxSize)
        {
            throw new InvalidDataException($"'{sourceName}' would be {pixelWidth} x {pixelHeight} pixels; at most {MaxSize} per side (lower the scale).");
        }

        // viewBox maps onto the viewport (preserveAspectRatio xMidYMid meet), then the scale to pixels.
        var view = Affine.Scale(scale, scale);
        if (viewBox.Count == 4 && viewBox[2] > 0f && viewBox[3] > 0f)
        {
            var fit = MathF.Min(width / viewBox[2], height / viewBox[3]);
            var offsetX = (width - (viewBox[2] * fit)) / 2f;
            var offsetY = (height - (viewBox[3] * fit)) / 2f;
            view = Affine.Translate(-viewBox[0], -viewBox[1]).Then(Affine.Scale(fit, fit)).Then(Affine.Translate(offsetX, offsetY)).Then(view);
        }

        var canvas = new Canvas(pixelWidth, pixelHeight);
        var context = new RenderContext(canvas, tint, IndexById(root), []);
        context.DrawChildren(root, Style.Default.Inherit(root, context), view);
        return new SvgImage(canvas.ToImage(), [.. context.Warnings]);
    }

    private static Dictionary<string, XElement> IndexById(XElement root) =>
        root.DescendantsAndSelf().Where(e => e.Attribute("id") is not null).GroupBy(e => e.Attribute("id")!.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    // ------------------------------------------------------------------ drawing

    private sealed class RenderContext(Canvas canvas, Color? tint, Dictionary<string, XElement> ids, SortedSet<string> warnings)
    {
        public SortedSet<string> Warnings { get; } = warnings;

        public Dictionary<string, XElement> Ids { get; } = ids;

        public void Warn(string message) => Warnings.Add(message);

        public void DrawChildren(XElement parent, Style style, Affine transform)
        {
            foreach (var element in parent.Elements())
            {
                Draw(element, style, transform);
            }
        }

        private void Draw(XElement element, Style parentStyle, Affine parentTransform)
        {
            var name = element.Name.LocalName;
            if (name is "defs" or "title" or "desc" or "metadata" or "style" or "linearGradient" or "radialGradient" or "symbol")
            {
                if (name == "style")
                {
                    Warn("<style> sheets are not applied; use attributes or style=\"...\" on the elements.");
                }

                return;
            }

            var style = parentStyle.Inherit(element, this);
            if (style.Display == "none" || style.Visibility is "hidden" or "collapse")
            {
                return;
            }

            var transform = Transform(element.Attribute("transform")?.Value).Then(parentTransform);
            switch (name)
            {
                case "g" or "svg" or "a":
                    if (style.Opacity < 1f)
                    {
                        // Group opacity applies to the group as a whole: draw it on its own layer, then blend.
                        var layer = canvas.PushLayer();
                        DrawChildren(element, style with { Opacity = 1f }, transform);
                        canvas.PopLayer(layer, style.Opacity);
                    }
                    else
                    {
                        DrawChildren(element, style, transform);
                    }

                    return;
                case "path":
                    Shape(PathData(element.Attribute("d")?.Value ?? ""), style, transform);
                    return;
                case "rect":
                    Shape(RectPath(element), style, transform);
                    return;
                case "circle":
                    {
                        var r = Attr(element, "r");
                        Shape(EllipsePath(Attr(element, "cx"), Attr(element, "cy"), r, r), style, transform);
                        return;
                    }

                case "ellipse":
                    Shape(EllipsePath(Attr(element, "cx"), Attr(element, "cy"), Attr(element, "rx"), Attr(element, "ry")), style, transform);
                    return;
                case "line":
                    Shape([new PathCommand('M', [Attr(element, "x1"), Attr(element, "y1")]), new PathCommand('L', [Attr(element, "x2"), Attr(element, "y2")])], style with { Fill = null }, transform);
                    return;
                case "polyline" or "polygon":
                    {
                        var points = Numbers(element.Attribute("points")?.Value);
                        var commands = new List<PathCommand>();
                        for (var i = 0; i + 1 < points.Count; i += 2)
                        {
                            commands.Add(new PathCommand(i == 0 ? 'M' : 'L', [points[i], points[i + 1]]));
                        }

                        if (name == "polygon" && commands.Count > 0)
                        {
                            commands.Add(new PathCommand('Z', []));
                        }

                        Shape(commands, style, transform);
                        return;
                    }

                default:
                    Warn($"<{name}> is not drawn.");
                    return;
            }
        }

        private void Shape(List<PathCommand> commands, Style style, Affine transform)
        {
            if (commands.Count == 0)
            {
                return;
            }

            var scale = MathF.Sqrt(MathF.Abs((transform.A * transform.D) - (transform.B * transform.C)));
            var contours = Flatten(commands, transform, CurveTolerance / MathF.Max(scale, 1e-3f));
            if (style.Fill is { } fill && style.FillOpacity > 0f)
            {
                canvas.Fill(contours.Where(c => c.Points.Count >= 3).Select(c => c.Points).ToList(), style.EvenOdd, Tinted(fill, style.FillOpacity * style.Opacity));
            }

            if (style.Stroke is { } stroke && style.StrokeWidth > 0f && style.StrokeOpacity > 0f)
            {
                var polygons = new List<List<Vector2>>();
                var halfWidth = style.StrokeWidth * scale / 2f;
                foreach (var contour in contours)
                {
                    StrokePolygons(contour.Points, contour.Closed, halfWidth, polygons);
                }

                canvas.Fill(polygons, evenOdd: false, Tinted(stroke, style.StrokeOpacity * style.Opacity));
            }
        }

        private Color Tinted(Color color, float opacity) =>
            tint is { } t ? new Color(color.R * t.R, color.G * t.G, color.B * t.B, color.A * t.A * opacity) : color with { A = color.A * opacity };
    }

    /// <summary>
    /// A stroke as polygons filled together (nonzero, all counter-clockwise, so overlaps add up): a quad per segment, a
    /// disc at every joint and end (round joins and caps).
    /// </summary>
    private static void StrokePolygons(List<Vector2> points, bool closed, float halfWidth, List<List<Vector2>> into)
    {
        var count = points.Count;
        if (count == 0)
        {
            return;
        }

        var segments = closed ? count : count - 1;
        for (var i = 0; i < segments; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % count];
            var direction = b - a;
            var length = direction.Magnitude;
            if (length < 1e-6f)
            {
                continue;
            }

            var normal = new Vector2(-direction.Y, direction.X) / length * halfWidth;
            into.Add(CounterClockwise([a + normal, b + normal, b - normal, a - normal]));
        }

        var discSegments = Math.Clamp((int)(halfWidth * 2f), 8, 48);
        foreach (var p in points)
        {
            var disc = new List<Vector2>(discSegments);
            for (var s = 0; s < discSegments; s++)
            {
                var angle = s * MathF.Tau / discSegments;
                disc.Add(p + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * halfWidth);
            }

            into.Add(CounterClockwise(disc));
        }
    }

    private static List<Vector2> CounterClockwise(List<Vector2> polygon)
    {
        var area = 0f;
        for (var i = 0; i < polygon.Count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        if (area < 0f)
        {
            polygon.Reverse();
        }

        return polygon;
    }

    // ------------------------------------------------------------------ the canvas: anti-aliased polygon fill

    private sealed class Canvas(int width, int height)
    {
        private const int SubScanlines = 4;
        private readonly Stack<float[]> _layers = [];
        private float[] _pixels = new float[width * height * 4]; // premultiplied RGBA

        public float[] PushLayer()
        {
            _layers.Push(_pixels);
            _pixels = new float[width * height * 4];
            return _pixels;
        }

        public void PopLayer(float[] layer, float opacity)
        {
            _pixels = _layers.Pop();
            for (var i = 0; i < _pixels.Length; i += 4)
            {
                var a = layer[i + 3] * opacity;
                var keep = 1f - a;
                for (var c = 0; c < 3; c++)
                {
                    _pixels[i + c] = (layer[i + c] * opacity) + (_pixels[i + c] * keep);
                }

                _pixels[i + 3] = a + (_pixels[i + 3] * keep);
            }
        }

        /// <summary>Fills the polygons together with the fill rule, blending <paramref name="color"/> by coverage (source over).</summary>
        public void Fill(List<List<Vector2>> polygons, bool evenOdd, Color color)
        {
            if (polygons.Count == 0 || color.A <= 0f)
            {
                return;
            }

            var edges = new List<(float X0, float Y0, float X1, float Y1, int Winding)>();
            var minY = float.MaxValue;
            var maxY = float.MinValue;
            foreach (var polygon in polygons)
            {
                for (var i = 0; i < polygon.Count; i++)
                {
                    var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
                    if (a.Y == b.Y)
                    {
                        continue;
                    }

                    edges.Add(a.Y < b.Y ? (a.X, a.Y, b.X, b.Y, 1) : (b.X, b.Y, a.X, a.Y, -1));
                    (minY, maxY) = (MathF.Min(minY, MathF.Min(a.Y, b.Y)), MathF.Max(maxY, MathF.Max(a.Y, b.Y)));
                }
            }

            var rowStart = Math.Max(0, (int)MathF.Floor(minY));
            var rowEnd = Math.Min(height - 1, (int)MathF.Ceiling(maxY));
            var coverage = new float[width + 1];
            var crossings = new List<(float X, int Winding)>();
            for (var row = rowStart; row <= rowEnd; row++)
            {
                Array.Clear(coverage);
                var touched = false;
                for (var sub = 0; sub < SubScanlines; sub++)
                {
                    var y = row + ((sub + 0.5f) / SubScanlines);
                    crossings.Clear();
                    foreach (var e in edges)
                    {
                        if (y >= e.Y0 && y < e.Y1)
                        {
                            crossings.Add((e.X0 + ((y - e.Y0) / (e.Y1 - e.Y0) * (e.X1 - e.X0)), e.Winding));
                        }
                    }

                    if (crossings.Count < 2)
                    {
                        continue;
                    }

                    crossings.Sort((p, q) => p.X.CompareTo(q.X));
                    var winding = 0;
                    for (var c = 0; c < crossings.Count - 1; c++)
                    {
                        winding += evenOdd ? 1 : crossings[c].Winding;
                        var inside = evenOdd ? (winding & 1) == 1 : winding != 0;
                        if (inside)
                        {
                            AddSpan(coverage, crossings[c].X, crossings[c + 1].X, 1f / SubScanlines);
                            touched = true;
                        }
                    }
                }

                if (!touched)
                {
                    continue;
                }

                for (var x = 0; x < width; x++)
                {
                    var amount = Math.Min(1f, coverage[x]);
                    if (amount <= 0f)
                    {
                        continue;
                    }

                    var i = ((row * width) + x) * 4;
                    var a = color.A * amount;
                    var keep = 1f - a;
                    _pixels[i] = (color.R * a) + (_pixels[i] * keep);
                    _pixels[i + 1] = (color.G * a) + (_pixels[i + 1] * keep);
                    _pixels[i + 2] = (color.B * a) + (_pixels[i + 2] * keep);
                    _pixels[i + 3] = a + (_pixels[i + 3] * keep);
                }
            }
        }

        /// <summary>Adds the horizontal span [x0, x1) to the pixel coverage of one sub-scanline, with exact partial pixels at the ends.</summary>
        private void AddSpan(float[] coverage, float x0, float x1, float weight)
        {
            x0 = Math.Clamp(x0, 0f, width);
            x1 = Math.Clamp(x1, 0f, width);
            if (x1 <= x0)
            {
                return;
            }

            var first = (int)x0;
            var last = (int)x1;
            if (first == last)
            {
                coverage[first] += (x1 - x0) * weight;
                return;
            }

            coverage[first] += (first + 1 - x0) * weight;
            for (var x = first + 1; x < last; x++)
            {
                coverage[x] += weight;
            }

            if (last < width)
            {
                coverage[last] += (x1 - last) * weight;
            }
        }

        public Image ToImage()
        {
            var bytes = new byte[width * height * 4];
            for (var i = 0; i < bytes.Length; i += 4)
            {
                var a = _pixels[i + 3];
                if (a <= 0f)
                {
                    continue;
                }

                // Back to straight alpha, sRGB as authored (blending happened on the authored values, as browsers do).
                for (var c = 0; c < 3; c++)
                {
                    bytes[i + c] = (byte)Math.Clamp(MathF.Round(_pixels[i + c] / a * 255f), 0f, 255f);
                }

                bytes[i + 3] = (byte)Math.Clamp(MathF.Round(a * 255f), 0f, 255f);
            }

            return new Image(width, height, bytes);
        }
    }

    // ------------------------------------------------------------------ geometry

    private readonly record struct PathCommand(char Command, float[] Args);

    private sealed record Contour(List<Vector2> Points, bool Closed);

    /// <summary>A 2D affine transform: x' = A x + C y + E, y' = B x + D y + F (SVG's matrix(a b c d e f)).</summary>
    private readonly record struct Affine(float A, float B, float C, float D, float E, float F)
    {
        public static Affine Identity => new(1f, 0f, 0f, 1f, 0f, 0f);

        public static Affine Translate(float x, float y) => new(1f, 0f, 0f, 1f, x, y);

        public static Affine Scale(float x, float y) => new(x, 0f, 0f, y, 0f, 0f);

        public static Affine Rotate(float degrees)
        {
            var (s, c) = MathF.SinCos(degrees * Mathf.Deg2Rad);
            return new Affine(c, s, -s, c, 0f, 0f);
        }

        /// <summary>This transform, then <paramref name="next"/>.</summary>
        public Affine Then(Affine next) => new(
            (next.A * A) + (next.C * B), (next.B * A) + (next.D * B),
            (next.A * C) + (next.C * D), (next.B * C) + (next.D * D),
            (next.A * E) + (next.C * F) + next.E, (next.B * E) + (next.D * F) + next.F);

        public Vector2 Apply(Vector2 p) => new((A * p.X) + (C * p.Y) + E, (B * p.X) + (D * p.Y) + F);
    }

    private static Affine Transform(string? text)
    {
        var result = Affine.Identity;
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf('(', index);
            var close = open < 0 ? -1 : text.IndexOf(')', open);
            if (open < 0 || close < 0)
            {
                break;
            }

            var name = text[index..open].Trim().TrimStart(',').Trim();
            var v = Numbers(text[(open + 1)..close]);
            float Arg(int i, float fallback) => i < v.Count ? v[i] : fallback;
            var step = name switch
            {
                "matrix" when v.Count == 6 => new Affine(v[0], v[1], v[2], v[3], v[4], v[5]),
                "translate" => Affine.Translate(Arg(0, 0f), Arg(1, 0f)),
                "scale" => Affine.Scale(Arg(0, 1f), Arg(1, Arg(0, 1f))),
                "rotate" when v.Count >= 3 => Affine.Translate(-v[1], -v[2]).Then(Affine.Rotate(v[0])).Then(Affine.Translate(v[1], v[2])),
                "rotate" => Affine.Rotate(Arg(0, 0f)),
                "skewX" => new Affine(1f, 0f, MathF.Tan(Arg(0, 0f) * Mathf.Deg2Rad), 1f, 0f, 0f),
                "skewY" => new Affine(1f, MathF.Tan(Arg(0, 0f) * Mathf.Deg2Rad), 0f, 1f, 0f, 0f),
                _ => Affine.Identity,
            };
            result = step.Then(result); // the list applies right to left: the last transform acts first
            index = close + 1;
        }

        return result;
    }

    private static List<PathCommand> RectPath(XElement element)
    {
        var (x, y, w, h) = (Attr(element, "x"), Attr(element, "y"), Attr(element, "width"), Attr(element, "height"));
        if (w <= 0f || h <= 0f)
        {
            return [];
        }

        var rx = Length(element.Attribute("rx")?.Value);
        var ry = Length(element.Attribute("ry")?.Value);
        var (radiusX, radiusY) = (Math.Min(rx ?? ry ?? 0f, w / 2f), Math.Min(ry ?? rx ?? 0f, h / 2f));
        if (radiusX <= 0f || radiusY <= 0f)
        {
            return [new('M', [x, y]), new('L', [x + w, y]), new('L', [x + w, y + h]), new('L', [x, y + h]), new('Z', [])];
        }

        return
        [
            new('M', [x + radiusX, y]), new('L', [x + w - radiusX, y]), new('A', [radiusX, radiusY, 0, 0, 1, x + w, y + radiusY]),
            new('L', [x + w, y + h - radiusY]), new('A', [radiusX, radiusY, 0, 0, 1, x + w - radiusX, y + h]),
            new('L', [x + radiusX, y + h]), new('A', [radiusX, radiusY, 0, 0, 1, x, y + h - radiusY]),
            new('L', [x, y + radiusY]), new('A', [radiusX, radiusY, 0, 0, 1, x + radiusX, y]), new('Z', []),
        ];
    }

    private static List<PathCommand> EllipsePath(float cx, float cy, float rx, float ry) =>
        rx <= 0f || ry <= 0f ? [] :
        [
            new('M', [cx + rx, cy]), new('A', [rx, ry, 0, 0, 1, cx - rx, cy]), new('A', [rx, ry, 0, 0, 1, cx + rx, cy]), new('Z', []),
        ];

    /// <summary>Parses path data into absolute commands (relative ones resolved, H/V/S/T expanded) for <see cref="Flatten"/>.</summary>
    private static List<PathCommand> PathData(string d)
    {
        var commands = new List<PathCommand>();
        var tokens = new PathTokens(d);
        var current = Vector2.Zero;
        var start = Vector2.Zero;
        var lastControl = Vector2.Zero;
        var lastCommand = ' ';
        char? command = null;
        while (tokens.TryCommand(out var next) || (command is not null && tokens.HasNumber))
        {
            if (next != '\0')
            {
                command = next;
            }

            var c = command!.Value;
            var relative = char.IsLower(c);
            var upper = char.ToUpperInvariant(c);
            Vector2 Point(Vector2 p) => relative ? current + p : p;
            switch (upper)
            {
                case 'M':
                    current = start = Point(tokens.Pair());
                    commands.Add(new('M', [current.X, current.Y]));
                    command = relative ? 'l' : 'L'; // further pairs are lines
                    break;
                case 'L':
                    current = Point(tokens.Pair());
                    commands.Add(new('L', [current.X, current.Y]));
                    break;
                case 'H':
                    current = new Vector2(relative ? current.X + tokens.Number() : tokens.Number(), current.Y);
                    commands.Add(new('L', [current.X, current.Y]));
                    break;
                case 'V':
                    current = new Vector2(current.X, relative ? current.Y + tokens.Number() : tokens.Number());
                    commands.Add(new('L', [current.X, current.Y]));
                    break;
                case 'C':
                    {
                        var (c1, c2, end) = (Point(tokens.Pair()), Point(tokens.Pair()), Point(tokens.Pair()));
                        commands.Add(new('C', [c1.X, c1.Y, c2.X, c2.Y, end.X, end.Y]));
                        (lastControl, current) = (c2, end);
                        break;
                    }

                case 'S':
                    {
                        var c1 = lastCommand is 'C' or 'S' ? (current * 2f) - lastControl : current;
                        var (c2, end) = (Point(tokens.Pair()), Point(tokens.Pair()));
                        commands.Add(new('C', [c1.X, c1.Y, c2.X, c2.Y, end.X, end.Y]));
                        (lastControl, current) = (c2, end);
                        break;
                    }

                case 'Q':
                    {
                        var (control, end) = (Point(tokens.Pair()), Point(tokens.Pair()));
                        commands.Add(new('Q', [control.X, control.Y, end.X, end.Y]));
                        (lastControl, current) = (control, end);
                        break;
                    }

                case 'T':
                    {
                        var control = lastCommand is 'Q' or 'T' ? (current * 2f) - lastControl : current;
                        var end = Point(tokens.Pair());
                        commands.Add(new('Q', [control.X, control.Y, end.X, end.Y]));
                        (lastControl, current) = (control, end);
                        break;
                    }

                case 'A':
                    {
                        var (rx, ry, rotation) = (tokens.Number(), tokens.Number(), tokens.Number());
                        var (large, sweep) = (tokens.Flag(), tokens.Flag());
                        var end = Point(tokens.Pair());
                        commands.Add(new('A', [rx, ry, rotation, large, sweep, end.X, end.Y]));
                        current = end;
                        break;
                    }

                case 'Z':
                    commands.Add(new('Z', []));
                    current = start;
                    command = null; // a number after Z needs a new command
                    break;
                default:
                    return commands; // unknown command: draw what came before, as browsers do
            }

            lastCommand = upper;
            if (tokens.Failed)
            {
                break;
            }
        }

        return commands;
    }

    /// <summary>Absolute commands to polylines in pixels; curves and arcs are subdivided until within <paramref name="tolerance"/> (user units).</summary>
    private static List<Contour> Flatten(List<PathCommand> commands, Affine transform, float tolerance)
    {
        var contours = new List<Contour>();
        List<Vector2>? points = null;
        var current = Vector2.Zero;
        var start = Vector2.Zero;
        void Add(Vector2 p) => points!.Add(transform.Apply(p));
        foreach (var (command, a) in commands)
        {
            switch (command)
            {
                case 'M':
                    if (points is { Count: > 0 })
                    {
                        contours.Add(new Contour(points, false));
                    }

                    points = [];
                    current = start = new Vector2(a[0], a[1]);
                    Add(current);
                    break;
                case 'L':
                    points ??= [transform.Apply(current)];
                    current = new Vector2(a[0], a[1]);
                    Add(current);
                    break;
                case 'C':
                    {
                        points ??= [transform.Apply(current)];
                        var (p1, p2, p3) = (new Vector2(a[0], a[1]), new Vector2(a[2], a[3]), new Vector2(a[4], a[5]));
                        var steps = CurveSteps(current, p1, p2, p3, tolerance);
                        for (var s = 1; s <= steps; s++)
                        {
                            var t = s / (float)steps;
                            var u = 1f - t;
                            Add((current * (u * u * u)) + (p1 * (3f * u * u * t)) + (p2 * (3f * u * t * t)) + (p3 * (t * t * t)));
                        }

                        current = p3;
                        break;
                    }

                case 'Q':
                    {
                        points ??= [transform.Apply(current)];
                        var (p1, p2) = (new Vector2(a[0], a[1]), new Vector2(a[2], a[3]));
                        var steps = CurveSteps(current, p1, p1, p2, tolerance);
                        for (var s = 1; s <= steps; s++)
                        {
                            var t = s / (float)steps;
                            var u = 1f - t;
                            Add((current * (u * u)) + (p1 * (2f * u * t)) + (p2 * (t * t)));
                        }

                        current = p2;
                        break;
                    }

                case 'A':
                    points ??= [transform.Apply(current)];
                    foreach (var p in Arc(current, a[0], a[1], a[2], a[3] != 0f, a[4] != 0f, new Vector2(a[5], a[6]), tolerance))
                    {
                        Add(p);
                    }

                    current = new Vector2(a[5], a[6]);
                    break;
                case 'Z':
                    if (points is { Count: > 0 })
                    {
                        contours.Add(new Contour(points, true));
                    }

                    points = null;
                    current = start;
                    break;
            }
        }

        if (points is { Count: > 0 })
        {
            contours.Add(new Contour(points, false));
        }

        return contours;
    }

    /// <summary>Enough straight steps for a cubic curve to stay within the tolerance (from its control polygon's length).</summary>
    private static int CurveSteps(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float tolerance)
    {
        var length = (p1 - p0).Magnitude + (p2 - p1).Magnitude + (p3 - p2).Magnitude;
        return Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(length / MathF.Max(tolerance, 1e-4f)) * 0.7f), 1, 512);
    }

    /// <summary>An SVG elliptical arc (endpoint form) as points after the start, per the SVG implementation notes (F.6.5).</summary>
    private static IEnumerable<Vector2> Arc(Vector2 from, float rx, float ry, float rotationDegrees, bool large, bool sweep, Vector2 to, float tolerance)
    {
        rx = MathF.Abs(rx);
        ry = MathF.Abs(ry);
        if (rx < 1e-6f || ry < 1e-6f || from == to)
        {
            yield return to;
            yield break;
        }

        var (sin, cos) = MathF.SinCos(rotationDegrees * Mathf.Deg2Rad);
        var half = (from - to) / 2f;
        var x1 = (cos * half.X) + (sin * half.Y);
        var y1 = (-sin * half.X) + (cos * half.Y);
        var lambda = ((x1 * x1) / (rx * rx)) + ((y1 * y1) / (ry * ry));
        if (lambda > 1f)
        {
            (rx, ry) = (rx * MathF.Sqrt(lambda), ry * MathF.Sqrt(lambda));
        }

        var numerator = (rx * rx * ry * ry) - (rx * rx * y1 * y1) - (ry * ry * x1 * x1);
        var denominator = (rx * rx * y1 * y1) + (ry * ry * x1 * x1);
        var factor = MathF.Sqrt(MathF.Max(0f, numerator / denominator)) * (large == sweep ? -1f : 1f);
        var cx1 = factor * rx * y1 / ry;
        var cy1 = factor * -ry * x1 / rx;
        var center = new Vector2((cos * cx1) - (sin * cy1), (sin * cx1) + (cos * cy1)) + ((from + to) / 2f);
        static float Angle(Vector2 u, Vector2 v) => MathF.Atan2((u.X * v.Y) - (u.Y * v.X), (u.X * v.X) + (u.Y * v.Y));
        var startAngle = Angle(Vector2.UnitX, new Vector2((x1 - cx1) / rx, (y1 - cy1) / ry));
        var delta = Angle(new Vector2((x1 - cx1) / rx, (y1 - cy1) / ry), new Vector2((-x1 - cx1) / rx, (-y1 - cy1) / ry));
        if (!sweep && delta > 0f)
        {
            delta -= MathF.Tau;
        }
        else if (sweep && delta < 0f)
        {
            delta += MathF.Tau;
        }

        var steps = Math.Clamp((int)MathF.Ceiling(MathF.Abs(delta) * MathF.Sqrt(MathF.Max(rx, ry) / MathF.Max(tolerance, 1e-4f)) * 0.5f), 2, 512);
        for (var s = 1; s <= steps; s++)
        {
            var angle = startAngle + (delta * s / steps);
            var (ps, pc) = MathF.SinCos(angle);
            yield return s == steps ? to : center + new Vector2((cos * rx * pc) - (sin * ry * ps), (sin * rx * pc) + (cos * ry * ps));
        }
    }

    /// <summary>Path data tokens: command letters, numbers (with exponents and the compact "1.5.5" and "-1-2" forms) and arc flags.</summary>
    private sealed class PathTokens(string text)
    {
        private int _index;

        public bool Failed { get; private set; }

        public bool HasNumber
        {
            get
            {
                Skip();
                return _index < text.Length && (char.IsDigit(text[_index]) || text[_index] is '-' or '+' or '.');
            }
        }

        public bool TryCommand(out char command)
        {
            Skip();
            if (_index < text.Length && char.IsLetter(text[_index]) && text[_index] is not ('e' or 'E'))
            {
                command = text[_index++];
                return true;
            }

            command = '\0';
            return false;
        }

        public Vector2 Pair() => new(Number(), Number());

        public float Flag()
        {
            Skip();
            if (_index < text.Length && text[_index] is '0' or '1')
            {
                return text[_index++] - '0';
            }

            Failed = true;
            return 0f;
        }

        public float Number()
        {
            Skip();
            var begin = _index;
            if (_index < text.Length && text[_index] is '-' or '+')
            {
                _index++;
            }

            var dot = false;
            while (_index < text.Length && (char.IsDigit(text[_index]) || (text[_index] == '.' && !dot)))
            {
                dot |= text[_index] == '.';
                _index++;
            }

            if (_index < text.Length && text[_index] is 'e' or 'E' && _index + 1 < text.Length && (char.IsDigit(text[_index + 1]) || text[_index + 1] is '-' or '+'))
            {
                _index += 2;
                while (_index < text.Length && char.IsDigit(text[_index]))
                {
                    _index++;
                }
            }

            if (begin == _index || !float.TryParse(text.AsSpan(begin, _index - begin), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                Failed = true;
                _index = Math.Max(_index, begin + 1);
                return 0f;
            }

            return value;
        }

        private void Skip()
        {
            while (_index < text.Length && (char.IsWhiteSpace(text[_index]) || text[_index] == ','))
            {
                _index++;
            }
        }
    }

    // ------------------------------------------------------------------ style

    /// <summary>The inherited presentation properties (fill and stroke are null for "none").</summary>
    private sealed record Style(Color? Fill, Color? Stroke, float StrokeWidth, float Opacity, float FillOpacity, float StrokeOpacity, bool EvenOdd, string? Display, string? Visibility)
    {
        public static Style Default { get; } = new(Color.Black, null, 1f, 1f, 1f, 1f, false, null, null);

        /// <summary>This style with the element's attributes and <c>style="..."</c> declarations on top (opacity does not inherit).</summary>
        public Style Inherit(XElement element, RenderContext context)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var attribute in element.Attributes())
            {
                values[attribute.Name.LocalName] = attribute.Value.Trim();
            }

            foreach (var declaration in (element.Attribute("style")?.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = declaration.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    values[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
                }
            }

            var style = this with { Opacity = 1f, Display = null };
            if (values.TryGetValue("fill", out var fill))
            {
                style = style with { Fill = Paint(fill, context) };
            }

            if (values.TryGetValue("stroke", out var stroke))
            {
                style = style with { Stroke = Paint(stroke, context) };
            }

            if (values.TryGetValue("stroke-width", out var strokeWidth) && Length(strokeWidth) is { } w)
            {
                style = style with { StrokeWidth = MathF.Max(0f, w) };
            }

            float Unit(string key, float fallback) => values.TryGetValue(key, out var v) && float.TryParse(v.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                ? Math.Clamp(v.EndsWith('%') ? f / 100f : f, 0f, 1f)
                : fallback;
            style = style with
            {
                Opacity = Unit("opacity", 1f),
                FillOpacity = Unit("fill-opacity", FillOpacity),
                StrokeOpacity = Unit("stroke-opacity", StrokeOpacity),
                EvenOdd = values.TryGetValue("fill-rule", out var rule) ? rule == "evenodd" : EvenOdd,
                Display = values.GetValueOrDefault("display"),
                Visibility = values.GetValueOrDefault("visibility") ?? Visibility,
            };
            foreach (var unsupported in new[] { "filter", "mask", "clip-path", "stroke-dasharray", "marker-start", "marker-mid", "marker-end" })
            {
                if (values.TryGetValue(unsupported, out var value) && value != "none")
                {
                    context.Warn($"'{unsupported}' is not applied.");
                }
            }

            return style;
        }

        private static Color? Paint(string value, RenderContext context)
        {
            if (value == "none" || value.Length == 0)
            {
                return null;
            }

            if (value.StartsWith("url(", StringComparison.Ordinal))
            {
                var id = value[4..value.IndexOf(')', StringComparison.Ordinal)].Trim().TrimStart('#').Trim('\'', '"');
                context.Warn("Gradients and patterns are drawn with their first stop's color.");
                var stop = context.Ids.GetValueOrDefault(id)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "stop");
                var color = stop?.Attribute("stop-color")?.Value ?? StopColorFromStyle(stop);
                return color is null ? null : ParseColor(color);
            }

            return ParseColor(value);
        }

        private static string? StopColorFromStyle(XElement? stop) =>
            (stop?.Attribute("style")?.Value ?? "").Split(';').Select(d => d.Split(':')).Where(p => p.Length == 2 && p[0].Trim() == "stop-color").Select(p => p[1].Trim()).FirstOrDefault();
    }

    private static readonly Dictionary<string, uint> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = 0x000000, ["white"] = 0xFFFFFF, ["red"] = 0xFF0000, ["lime"] = 0x00FF00, ["green"] = 0x008000, ["blue"] = 0x0000FF,
        ["yellow"] = 0xFFFF00, ["cyan"] = 0x00FFFF, ["aqua"] = 0x00FFFF, ["magenta"] = 0xFF00FF, ["fuchsia"] = 0xFF00FF, ["gray"] = 0x808080,
        ["grey"] = 0x808080, ["silver"] = 0xC0C0C0, ["maroon"] = 0x800000, ["olive"] = 0x808000, ["purple"] = 0x800080, ["teal"] = 0x008080,
        ["navy"] = 0x000080, ["orange"] = 0xFFA500, ["pink"] = 0xFFC0CB, ["brown"] = 0xA52A2A, ["gold"] = 0xFFD700, ["transparent"] = 0x000000,
    };

    /// <summary>#rgb, #rgba, #rrggbb, #rrggbbaa, rgb()/rgba() and the common color names; unknown values are black (as browsers draw an invalid fill).</summary>
    private static Color ParseColor(string value)
    {
        value = value.Trim();
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length is 3 or 4)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }

            if (hex.Length is 6 or 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            {
                return hex.Length == 6
                    ? new Color(((packed >> 16) & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f, (packed & 0xFF) / 255f, 1f)
                    : new Color(((packed >> 24) & 0xFF) / 255f, ((packed >> 16) & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f, (packed & 0xFF) / 255f);
            }
        }
        else if (value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) && value.IndexOf('(', StringComparison.Ordinal) is var open and > 0)
        {
            var parts = value[(open + 1)..value.TrimEnd().TrimEnd(')').Length].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            float Channel(int i) => i < parts.Length && float.TryParse(parts[i].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                ? Math.Clamp(parts[i].EndsWith('%') ? f / 100f : i == 3 ? f : f / 255f, 0f, 1f)
                : i == 3 ? 1f : 0f;
            return new Color(Channel(0), Channel(1), Channel(2), Channel(3));
        }
        else if (value == "currentColor")
        {
            return Color.Black;
        }
        else if (NamedColors.TryGetValue(value, out var named))
        {
            return new Color(((named >> 16) & 0xFF) / 255f, ((named >> 8) & 0xFF) / 255f, (named & 0xFF) / 255f, value.Equals("transparent", StringComparison.OrdinalIgnoreCase) ? 0f : 1f);
        }

        return Color.Black;
    }

    // ------------------------------------------------------------------ numbers

    private static float Attr(XElement element, string name) => Length(element.Attribute(name)?.Value) ?? 0f;

    /// <summary>A length in user units: plain numbers and px (pt, mm, cm and in converted at 96 DPI); null for none or percentages.</summary>
    private static float? Length(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        var (number, factor) = text.EndsWith("px", StringComparison.Ordinal) ? (text[..^2], 1f)
            : text.EndsWith("pt", StringComparison.Ordinal) ? (text[..^2], 96f / 72f)
            : text.EndsWith("mm", StringComparison.Ordinal) ? (text[..^2], 96f / 25.4f)
            : text.EndsWith("cm", StringComparison.Ordinal) ? (text[..^2], 96f / 2.54f)
            : text.EndsWith("in", StringComparison.Ordinal) ? (text[..^2], 96f)
            : (text, 1f);
        return !text.EndsWith('%') && float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value * factor : null;
    }

    private static List<float> Numbers(string? text)
    {
        var result = new List<float>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var tokens = new PathTokens(text);
        while (tokens.HasNumber)
        {
            var value = tokens.Number();
            if (tokens.Failed)
            {
                break;
            }

            result.Add(value);
        }

        return result;
    }
}
