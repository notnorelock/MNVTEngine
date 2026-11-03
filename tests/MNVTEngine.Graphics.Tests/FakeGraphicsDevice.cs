using System.Runtime.InteropServices;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Tests;

/// <summary>Records draw commands and buffer uploads instead of talking to a GPU.</summary>
internal sealed class FakeGraphicsDevice : IGraphicsDevice
{
    public GraphicsResourceTracker Resources { get; } = new();

    public string Description => "Fake";

    public int BackBufferWidth { get; private set; } = 800;

    public int BackBufferHeight { get; private set; } = 600;

    public List<DrawCommand> Draws { get; } = [];

    /// <summary>Vertex data snapshot taken at each draw (the buffer is overwritten per chunk).</summary>
    public List<SpriteVertex[]> VerticesAtDraw { get; } = [];

    public RenderTarget2D? CurrentTarget { get; private set; }

    public void ResizeBackBuffer(int width, int height) => (BackBufferWidth, BackBufferHeight) = (width, height);

    public Texture2D CreateTexture(int width, int height, ReadOnlySpan<byte> rgba, SamplerState sampler, string debugName = "Texture") =>
        new FakeTexture(Resources, debugName, width, height, sampler);

    public RenderTarget2D CreateRenderTarget(int width, int height, SamplerState sampler, string debugName = "RenderTarget") =>
        new FakeRenderTarget(Resources, debugName, width, height, sampler);

    public Shader CreateShader(ShaderSource source) => new FakeShader(Resources, source.Name);

    public VertexBuffer CreateVertexBuffer(VertexLayout layout, int capacity, string debugName = "VertexBuffer") =>
        new FakeVertexBuffer(Resources, debugName, layout, capacity);

    public IndexBuffer CreateIndexBuffer(ReadOnlySpan<ushort> indices, string debugName = "IndexBuffer") =>
        new FakeIndexBuffer(Resources, debugName, indices.Length);

    public IndexBuffer CreateDynamicIndexBuffer(int capacity, string debugName = "DynamicIndexBuffer") =>
        new FakeIndexBuffer(Resources, debugName, capacity);

    public void SetRenderTarget(RenderTarget2D? target) => CurrentTarget = target;

    public void SetViewport(int x, int y, int width, int height)
    {
    }

    public void Clear(Color color)
    {
    }

    public void Draw(in DrawCommand command)
    {
        Draws.Add(command);
        VerticesAtDraw.Add(((FakeVertexBuffer)command.Vertices).Data);
    }

    public void ReadPixels(Span<byte> destination) => destination.Clear();

    public void Dispose() => Resources.DisposeAll();

    public sealed class FakeTexture(GraphicsResourceTracker t, string n, int w, int h, SamplerState s) : Texture2D(t, n, w, h, s)
    {
        protected override void ReleaseCore()
        {
        }
    }

    public sealed class FakeRenderTarget(GraphicsResourceTracker t, string n, int w, int h, SamplerState s) : RenderTarget2D(t, n, w, h, s)
    {
        protected override void ReleaseCore()
        {
        }
    }

    public sealed class FakeShader(GraphicsResourceTracker t, string n) : Shader(t, n)
    {
        public override bool HasUniform(string name) => true;

        protected override void ReleaseCore()
        {
        }
    }

    public sealed class FakeVertexBuffer(GraphicsResourceTracker t, string n, VertexLayout l, int c) : VertexBuffer(t, n, l, c)
    {
        public SpriteVertex[] Data { get; private set; } = [];

        protected override void SetDataCore(ReadOnlySpan<byte> data) => Data = MemoryMarshal.Cast<byte, SpriteVertex>(data).ToArray();

        protected override void ReleaseCore()
        {
        }
    }

    public sealed class FakeIndexBuffer(GraphicsResourceTracker t, string n, int c) : IndexBuffer(t, n, c)
    {
        protected override void ReleaseCore()
        {
        }
    }
}
