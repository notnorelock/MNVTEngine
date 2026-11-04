using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Text;

/// <summary>Where a cached glyph or image lives in the atlas. <see cref="Offset"/> is from the pen position on the baseline (y down).</summary>
public readonly record struct AtlasRegion(Texture2D Page, Rect Source, Vector2 Offset, Vector2 Size)
{
    public bool IsEmpty => Size.X <= 0 || Size.Y <= 0;
}

/// <summary>
/// Dynamic texture atlas for glyphs and small generated images (UI corner pieces). Pages are 1024x1024 RGBA8 textures
/// filled shelf by shelf; each glyph is rasterized once per (font, glyph, pixel size) and kept. New content is written to
/// a CPU copy of the page and uploaded by <see cref="Flush"/> as one rectangle per page and frame: no texture per
/// glyph, no recreation, no GPU wait. Page 0 starts with a white block, so solid fills batch with text. When
/// <see cref="MaxPages"/> pages are full, the atlas starts over (<see cref="Generation"/> increments, and cached
/// geometry that points into the atlas must be rebuilt).
/// </summary>
public sealed class GlyphAtlas : IDisposable
{
    public const int PageSize = 1024;
    private const int Padding = 1;
    private readonly IGraphicsDevice _device;
    private readonly List<Page> _pages = [];
    private readonly Dictionary<(int Font, int Glyph, int Size), AtlasRegion> _glyphs = [];
    private readonly Dictionary<long, AtlasRegion> _images = [];

    public GlyphAtlas(IGraphicsDevice device, int maxPages = 8)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);
        _device = device;
        MaxPages = maxPages;
        Reset();
    }

    public int MaxPages { get; }

    public int PageCount => _pages.Count;

    public int GlyphCount => _glyphs.Count;

    public int ImageCount => _images.Count;

    /// <summary>Increments whenever the atlas was cleared; regions from older generations are invalid.</summary>
    public int Generation { get; private set; }

    /// <summary>Glyphs rasterized since creation (cache misses).</summary>
    public long GlyphsRasterized { get; private set; }

    /// <summary>The white block in page 0, for solid rectangles drawn with the atlas texture.</summary>
    public AtlasRegion White { get; private set; }

    public IReadOnlyList<Texture2D> Pages => [.. _pages.Select(p => p.Texture)];

    /// <summary>Glyph sizes are cached in quarter pixels.</summary>
    public static int QuantizeSize(float pixelSize) => Math.Max(1, (int)MathF.Round(pixelSize * 4f));

    /// <summary>The atlas region of a glyph at a pixel size, rasterizing it on first use.</summary>
    public AtlasRegion GetGlyph(Font font, int glyph, float pixelSize)
    {
        ArgumentNullException.ThrowIfNull(font);
        var key = (font.Id, glyph, QuantizeSize(pixelSize));
        if (_glyphs.TryGetValue(key, out var region))
        {
            return region;
        }

        var bitmap = font.Rasterize(glyph, key.Item3 / 4f);
        GlyphsRasterized++;
        region = bitmap.Width == 0
            ? new AtlasRegion(_pages[0].Texture, default, default, default)
            : Place(bitmap.Width, bitmap.Height, new Vector2(bitmap.OffsetX, bitmap.OffsetY), (pixels, stride) => Coverage(bitmap, pixels, stride));
        _glyphs[key] = region;
        return region;
    }

    /// <summary>A generated image (e.g. a rounded-corner piece) identified by <paramref name="key"/>, drawn by <paramref name="draw"/> on first use.</summary>
    /// <param name="draw">Fills RGBA8 pixels: the span starts at the image's top-left; rows are <c>stride</c> bytes apart.</param>
    public AtlasRegion GetImage(long key, int width, int height, ImageWriter draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (_images.TryGetValue(key, out var region))
        {
            return region;
        }

        region = Place(width, height, Vector2.Zero, draw);
        _images[key] = region;
        return region;
    }

    /// <summary>Uploads everything added since the last flush (one rectangle per changed page). Call once per frame before drawing.</summary>
    public void Flush()
    {
        foreach (var page in _pages)
        {
            if (page.DirtyBottom <= page.DirtyTop)
            {
                continue;
            }

            int top = page.DirtyTop, height = page.DirtyBottom - page.DirtyTop;
            _device.UpdateTexture(page.Texture, 0, top, PageSize, height, page.Pixels.AsSpan(top * PageSize * 4, height * PageSize * 4));
            (page.DirtyTop, page.DirtyBottom) = (PageSize, 0);
        }
    }

    public void Dispose()
    {
        foreach (var page in _pages)
        {
            page.Texture.Dispose();
        }

        _pages.Clear();
    }

    private static void Coverage(GlyphBitmap bitmap, Span<byte> pixels, int stride)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var o = (y * stride) + (x * 4);
                pixels[o] = pixels[o + 1] = pixels[o + 2] = 255; // white, coverage in alpha: tinted by the vertex color
                pixels[o + 3] = bitmap.Coverage[(y * bitmap.Width) + x];
            }
        }
    }

    private AtlasRegion Place(int width, int height, Vector2 offset, ImageWriter draw)
    {
        if (width + (2 * Padding) > PageSize || height + (2 * Padding) > PageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"A {width}x{height} image does not fit a {PageSize}x{PageSize} atlas page.");
        }

        if (!TryAllocate(_pages[^1], width, height, out var x, out var y))
        {
            if (_pages.Count == MaxPages)
            {
                Reset(); // every page is full: start over; callers rebuild from Generation
            }
            else
            {
                _pages.Add(NewPage());
            }

            TryAllocate(_pages[^1], width, height, out x, out y);
        }

        var page = _pages[^1];
        draw(page.Pixels.AsSpan(((y * PageSize) + x) * 4), PageSize * 4);
        page.DirtyTop = Math.Min(page.DirtyTop, y);
        page.DirtyBottom = Math.Max(page.DirtyBottom, y + height);
        return new AtlasRegion(page.Texture, new Rect(x, y, width, height), offset, new Vector2(width, height));
    }

    private static bool TryAllocate(Page page, int width, int height, out int x, out int y)
    {
        var w = width + Padding;
        var h = height + Padding;
        foreach (var shelf in page.Shelves)
        {
            // Reuse a shelf that is tall enough but not wastefully so.
            if (shelf.Height >= h && shelf.Height <= h + (h / 2) + 2 && shelf.Used + w <= PageSize)
            {
                (x, y) = (shelf.Used, shelf.Top);
                shelf.Used += w;
                return true;
            }
        }

        if (page.NextTop + h <= PageSize)
        {
            var shelf = new Shelf { Top = page.NextTop, Height = h, Used = w };
            page.Shelves.Add(shelf);
            page.NextTop += h;
            (x, y) = (0, shelf.Top);
            return true;
        }

        (x, y) = (0, 0);
        return false;
    }

    private void Reset()
    {
        Dispose();
        _glyphs.Clear();
        _images.Clear();
        Generation++;
        _pages.Add(NewPage());
        White = Place(4, 4, Vector2.Zero, static (pixels, stride) =>
        {
            for (var y = 0; y < 4; y++)
            {
                pixels.Slice(y * stride, 16).Fill(255);
            }
        });
        White = White with { Source = new Rect(White.Source.X + 1, White.Source.Y + 1, 2, 2) }; // inner texels: no bleeding when filtered
    }

    private Page NewPage() => new(_device.CreateTexture(PageSize, PageSize, new byte[PageSize * PageSize * 4], SamplerState.LinearClamp, $"GlyphAtlas[{_pages.Count}]"));

    private sealed class Shelf
    {
        public int Top { get; init; }

        public int Height { get; init; }

        public int Used { get; set; }
    }

    private sealed class Page(Texture2D texture)
    {
        public Texture2D Texture { get; } = texture;

        public byte[] Pixels { get; } = new byte[PageSize * PageSize * 4];

        public List<Shelf> Shelves { get; } = [];

        public int NextTop { get; set; }

        public int DirtyTop { get; set; } = PageSize;

        public int DirtyBottom { get; set; }
    }
}

/// <summary>Writes RGBA8 pixels into an atlas slot.</summary>
public delegate void ImageWriter(Span<byte> pixels, int stride);
