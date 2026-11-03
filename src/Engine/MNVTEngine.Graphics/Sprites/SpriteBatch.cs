using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics;

/// <summary>
/// Collects textured quads between <see cref="Begin"/> and <see cref="End"/>, sorts them, and submits
/// as few draw calls as the sort mode allows. Consecutive sprites sharing a texture become one draw.
/// Vertex data is uploaded once per chunk of <see cref="MaxSpritesPerChunk"/> sprites.
/// </summary>
public sealed class SpriteBatch : IDisposable
{
    public const int MaxSpritesPerChunk = 2048;

    private readonly IGraphicsDevice _device;
    private readonly Material _defaultMaterial;
    private readonly VertexBuffer _vertexBuffer;
    private readonly IndexBuffer _indexBuffer;
    private readonly SpriteVertex[] _chunkVertices = new SpriteVertex[MaxSpritesPerChunk * 4];
    private readonly Dictionary<Texture2D, int> _textureOrder = new(ReferenceEqualityComparer.Instance);

    private SpriteItem[] _items = new SpriteItem[256];
    private int[] _order = new int[256];
    private int _count;
    private bool _active;
    private SpriteSortMode _sortMode;
    private Material _material;
    private Matrix4x4 _viewProjection;

    public SpriteBatch(IGraphicsDevice device, Material defaultMaterial)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(defaultMaterial);
        _device = device;
        _defaultMaterial = defaultMaterial;
        _material = defaultMaterial;
        _vertexBuffer = device.CreateVertexBuffer(SpriteVertex.Layout, MaxSpritesPerChunk * 4, "SpriteBatch.Vertices");
        _indexBuffer = device.CreateIndexBuffer(CreateQuadIndices(MaxSpritesPerChunk), "SpriteBatch.Indices");
        WhitePixel = device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.PointClamp, "SpriteBatch.WhitePixel");
    }

    /// <summary>1x1 white texture for untextured shapes and debug primitives.</summary>
    public Texture2D WhitePixel { get; }

    public bool IsActive => _active;

    /// <summary>Totals since the last <see cref="ResetStats"/>.</summary>
    public int SpriteCount { get; private set; }

    public int DrawCallCount { get; private set; }

    public void ResetStats()
    {
        SpriteCount = 0;
        DrawCallCount = 0;
    }

    public void Begin(Matrix4x4 viewProjection, SpriteSortMode sortMode = SpriteSortMode.Deferred, Material? material = null)
    {
        if (_active)
        {
            throw new InvalidOperationException("Begin called twice without End.");
        }

        _active = true;
        _viewProjection = viewProjection;
        _sortMode = sortMode;
        _material = material ?? _defaultMaterial;
        _count = 0;
    }

    /// <param name="position">World position of the pivot.</param>
    /// <param name="size">World size of the quad.</param>
    /// <param name="rotationDegrees">Counter-clockwise rotation around the pivot.</param>
    /// <param name="pivot">Normalized pivot, (0, 0) = bottom-left, (0.5, 0.5) = center (default).</param>
    /// <param name="source">Source rectangle in texels, origin top-left of the image. Null = whole texture.</param>
    /// <param name="depth">Sort key for <see cref="SpriteSortMode.BackToFront"/>/<see cref="SpriteSortMode.FrontToBack"/>; higher is farther.</param>
    public void Draw(
        Texture2D texture,
        Vector2 position,
        Vector2 size,
        Color color,
        float rotationDegrees = 0f,
        Vector2? pivot = null,
        Rect? source = null,
        SpriteFlip flip = SpriteFlip.None,
        float depth = 0f)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ObjectDisposedException.ThrowIf(texture.IsDisposed, texture);
        if (!_active)
        {
            throw new InvalidOperationException("Draw must be called between Begin and End.");
        }

        var p = pivot ?? new Vector2(0.5f, 0.5f);
        float x0 = -p.X * size.X, y0 = -p.Y * size.Y;
        float x1 = x0 + size.X, y1 = y0 + size.Y;

        // Texture coordinates: v = 0 is image row 0 (top) for uploaded images, bottom for render targets.
        var src = source ?? new Rect(0f, 0f, texture.Width, texture.Height);
        float u0 = src.X / texture.Width, u1 = (src.X + src.Width) / texture.Width;
        float vTop = src.Y / texture.Height, vBottom = (src.Y + src.Height) / texture.Height;
        if (texture.IsFlippedVertically)
        {
            (vTop, vBottom) = (1f - vTop, 1f - vBottom);
        }

        if ((flip & SpriteFlip.Horizontal) != 0)
        {
            (u0, u1) = (u1, u0);
        }

        if ((flip & SpriteFlip.Vertical) != 0)
        {
            (vTop, vBottom) = (vBottom, vTop);
        }

        var (sin, cos) = rotationDegrees == 0f ? (0f, 1f) : MathF.SinCos(rotationDegrees * Mathf.Deg2Rad);
        Vector2 Corner(float x, float y) => new(position.X + (x * cos) - (y * sin), position.Y + (x * sin) + (y * cos));

        var packed = color.ToRgba32();
        EnsureCapacity(_count + 1);
        _items[_count] = new SpriteItem(
            texture,
            depth,
            new SpriteVertex(Corner(x0, y0), new Vector2(u0, vBottom), packed),
            new SpriteVertex(Corner(x1, y0), new Vector2(u1, vBottom), packed),
            new SpriteVertex(Corner(x1, y1), new Vector2(u1, vTop), packed),
            new SpriteVertex(Corner(x0, y1), new Vector2(u0, vTop), packed));
        _count++;
    }

    public void End()
    {
        if (!_active)
        {
            throw new InvalidOperationException("End called without Begin.");
        }

        _active = false;
        if (_count == 0)
        {
            return;
        }

        Sort();
        for (var chunkStart = 0; chunkStart < _count; chunkStart += MaxSpritesPerChunk)
        {
            FlushChunk(chunkStart, Math.Min(MaxSpritesPerChunk, _count - chunkStart));
        }

        SpriteCount += _count;
        _count = 0;
        _textureOrder.Clear();

        // Drop texture references so the batch never keeps disposed resources reachable.
        Array.Clear(_items);
    }

    public void Dispose()
    {
        _vertexBuffer.Dispose();
        _indexBuffer.Dispose();
        WhitePixel.Dispose();
    }

    private void Sort()
    {
        for (var i = 0; i < _count; i++)
        {
            _order[i] = i;
        }

        var order = _order.AsSpan(0, _count);
        var items = _items;
        switch (_sortMode)
        {
            case SpriteSortMode.Texture:
                for (var i = 0; i < _count; i++)
                {
                    _textureOrder.TryAdd(items[i].Texture, _textureOrder.Count);
                }

                var textureOrder = _textureOrder;
                order.Sort((a, b) => Compare(textureOrder[items[a].Texture], textureOrder[items[b].Texture], a, b));
                break;
            case SpriteSortMode.BackToFront:
                order.Sort((a, b) => Compare(items[b].Depth, items[a].Depth, a, b));
                break;
            case SpriteSortMode.FrontToBack:
                order.Sort((a, b) => Compare(items[a].Depth, items[b].Depth, a, b));
                break;
            default:
                break;
        }

        // Ties fall back to submission index, which makes the unstable span sort stable.
        static int Compare<T>(T x, T y, int a, int b)
            where T : IComparable<T>
        {
            var c = x.CompareTo(y);
            return c != 0 ? c : a.CompareTo(b);
        }
    }

    private void FlushChunk(int start, int count)
    {
        for (var i = 0; i < count; i++)
        {
            ref readonly var item = ref _items[_order[start + i]];
            var v = i * 4;
            _chunkVertices[v] = item.V0;
            _chunkVertices[v + 1] = item.V1;
            _chunkVertices[v + 2] = item.V2;
            _chunkVertices[v + 3] = item.V3;
        }

        _vertexBuffer.SetData<SpriteVertex>(_chunkVertices.AsSpan(0, count * 4));

        var runStart = 0;
        for (var i = 1; i <= count; i++)
        {
            var runTexture = _items[_order[start + runStart]].Texture;
            if (i < count && ReferenceEquals(_items[_order[start + i]].Texture, runTexture))
            {
                continue;
            }

            var command = new DrawCommand(_material, runTexture, _vertexBuffer, _indexBuffer, runStart * 6, (i - runStart) * 6, _viewProjection);
            _device.Draw(in command);
            DrawCallCount++;
            runStart = i;
        }
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _items.Length)
        {
            return;
        }

        var size = Math.Max(required, _items.Length * 2);
        Array.Resize(ref _items, size);
        Array.Resize(ref _order, size);
    }

    private static ushort[] CreateQuadIndices(int quads)
    {
        var indices = new ushort[quads * 6];
        for (var q = 0; q < quads; q++)
        {
            var v = (ushort)(q * 4);
            var i = q * 6;
            indices[i] = v;
            indices[i + 1] = (ushort)(v + 1);
            indices[i + 2] = (ushort)(v + 2);
            indices[i + 3] = (ushort)(v + 2);
            indices[i + 4] = (ushort)(v + 3);
            indices[i + 5] = v;
        }

        return indices;
    }

    private readonly record struct SpriteItem(Texture2D Texture, float Depth, SpriteVertex V0, SpriteVertex V1, SpriteVertex V2, SpriteVertex V3);
}
