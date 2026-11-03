using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MNVTEngine.Mathematics;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;

namespace MNVTEngine.Graphics.OpenGL;

/// <summary>OpenGL 4.5 core implementation of <see cref="IGraphicsDevice"/> using direct state access.</summary>
public sealed partial class GLGraphicsDevice : IGraphicsDevice
{
    private readonly GL _gl;
    private readonly ILogger _logger;
    private readonly DebugProc? _debugCallback;
    private RenderTarget2D? _currentTarget;
    private uint _currentProgram;
    private BlendMode? _currentBlend;
    private bool _disposed;

    private GLGraphicsDevice(GL gl, ILogger logger, int width, int height)
    {
        _gl = gl;
        _logger = logger;
        BackBufferWidth = width;
        BackBufferHeight = height;

        gl.GetInteger(GetPName.MajorVersion, out var major);
        gl.GetInteger(GetPName.MinorVersion, out var minor);
        if (major < 4 || (major == 4 && minor < 5))
        {
            throw new NotSupportedException($"OpenGL 4.5 is required; the context is {major}.{minor}.");
        }

        Description = $"OpenGL {major}.{minor} | {gl.GetStringS(StringName.Renderer)} | {gl.GetStringS(StringName.Vendor)}";

        gl.GetInteger(GetPName.ContextFlags, out var flags);
        if ((flags & (int)ContextFlagMask.DebugBit) != 0)
        {
            _debugCallback = OnDebugMessage;
            gl.Enable(EnableCap.DebugOutput);
            gl.Enable(EnableCap.DebugOutputSynchronous);
            unsafe
            {
                gl.DebugMessageCallback(_debugCallback, null);
            }
        }

        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        LogCreated(Description, _debugCallback is not null);
    }

    public GraphicsResourceTracker Resources { get; } = new();

    public string Description { get; }

    public int BackBufferWidth { get; private set; }

    public int BackBufferHeight { get; private set; }

    /// <summary>Loads GL entry points from the window's context, which must be current on this thread.</summary>
    public static GLGraphicsDevice Create(IGLContextSource contextSource, int backBufferWidth, int backBufferHeight, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(contextSource);
        ArgumentNullException.ThrowIfNull(logger);
        return new GLGraphicsDevice(GL.GetApi(contextSource), logger, backBufferWidth, backBufferHeight);
    }

    public void ResizeBackBuffer(int width, int height)
    {
        BackBufferWidth = width;
        BackBufferHeight = height;
        if (_currentTarget is null)
        {
            _gl.Viewport(0, 0, (uint)width, (uint)height);
        }
    }

    public Texture2D CreateTexture(int width, int height, ReadOnlySpan<byte> rgba, SamplerState sampler, string debugName = "Texture") =>
        new GLTexture(_gl, Resources, debugName, width, height, rgba, sampler);

    public RenderTarget2D CreateRenderTarget(int width, int height, SamplerState sampler, string debugName = "RenderTarget") =>
        new GLRenderTarget(_gl, Resources, debugName, width, height, sampler);

    public Shader CreateShader(ShaderSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new GLShader(_gl, Resources, source);
    }

    public VertexBuffer CreateVertexBuffer(VertexLayout layout, int capacity, string debugName = "VertexBuffer") =>
        new GLVertexBuffer(_gl, Resources, debugName, layout, capacity);

    public IndexBuffer CreateIndexBuffer(ReadOnlySpan<ushort> indices, string debugName = "IndexBuffer") =>
        new GLIndexBuffer(_gl, Resources, debugName, indices);

    public void SetRenderTarget(RenderTarget2D? target)
    {
        if (target is not null)
        {
            ObjectDisposedException.ThrowIf(target.IsDisposed, target);
        }

        var framebuffer = target is GLRenderTarget gl ? gl.Framebuffer
            : target is null ? 0u
            : throw new ArgumentException("Render target was not created by this device.", nameof(target));

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        _currentTarget = target;
        var (w, h) = target is null ? (BackBufferWidth, BackBufferHeight) : (target.Width, target.Height);
        _gl.Viewport(0, 0, (uint)w, (uint)h);
    }

    public void SetViewport(int x, int y, int width, int height) => _gl.Viewport(x, y, (uint)width, (uint)height);

    public void Clear(Color color)
    {
        _gl.ClearColor(color.R, color.G, color.B, color.A);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
    }

    public unsafe void Draw(in DrawCommand command)
    {
        var shader = command.Material.Shader as GLShader ?? throw new ArgumentException("Shader was not created by this device.", nameof(command));
        var texture = command.Texture as IGLTexture ?? throw new ArgumentException("Texture was not created by this device.", nameof(command));
        var vertices = command.Vertices as GLVertexBuffer ?? throw new ArgumentException("Vertex buffer was not created by this device.", nameof(command));
        var indices = command.Indices as GLIndexBuffer ?? throw new ArgumentException("Index buffer was not created by this device.", nameof(command));
        if (ReferenceEquals(command.Texture, _currentTarget))
        {
            throw new InvalidOperationException($"Cannot sample render target '{_currentTarget.DebugName}' while drawing into it.");
        }

        if (command.IndexOffset < 0 || command.IndexCount <= 0 || command.IndexOffset + command.IndexCount > indices.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(command), $"Index range {command.IndexOffset}+{command.IndexCount} exceeds buffer of {indices.Count}.");
        }

        if (_currentProgram != shader.Program)
        {
            _gl.UseProgram(shader.Program);
            _currentProgram = shader.Program;
        }

        var vpLocation = shader.Location("uViewProjection");
        if (vpLocation >= 0)
        {
            shader.Set(vpLocation, command.ViewProjection);
        }

        foreach (var (name, value) in command.Material.Uniforms)
        {
            var location = shader.Location(name);
            if (location >= 0)
            {
                shader.Set(location, value);
            }
        }

        ApplyBlend(command.Material.BlendMode);
        _gl.BindTextureUnit(0, texture.Handle);
        _gl.BindVertexArray(vertices.VertexArray);
        if (vertices.AttachedIndexBuffer != indices.Buffer)
        {
            _gl.VertexArrayElementBuffer(vertices.VertexArray, indices.Buffer);
            vertices.AttachedIndexBuffer = indices.Buffer;
        }

        var topology = command.Topology == PrimitiveTopology.Lines ? PrimitiveType.Lines : PrimitiveType.Triangles;
        _gl.DrawElements(topology, (uint)command.IndexCount, DrawElementsType.UnsignedShort, (void*)(command.IndexOffset * sizeof(ushort)));
    }

    public unsafe void ReadPixels(Span<byte> destination)
    {
        var (w, h) = _currentTarget is null ? (BackBufferWidth, BackBufferHeight) : (_currentTarget.Width, _currentTarget.Height);
        var rowBytes = w * 4;
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, rowBytes * h, nameof(destination));

        fixed (byte* p = destination)
        {
            _gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        // GL returns bottom row first; flip to top-down.
        var temp = new byte[rowBytes];
        for (var y = 0; y < h / 2; y++)
        {
            var top = destination.Slice(y * rowBytes, rowBytes);
            var bottom = destination.Slice((h - 1 - y) * rowBytes, rowBytes);
            top.CopyTo(temp);
            bottom.CopyTo(top);
            temp.CopyTo(bottom);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var leaked in Resources.DisposeAll())
        {
            LogLeak(leaked.ToString());
        }

        _gl.Dispose();
    }

    private void ApplyBlend(BlendMode mode)
    {
        if (_currentBlend == mode)
        {
            return;
        }

        _currentBlend = mode;
        if (mode == BlendMode.Opaque)
        {
            _gl.Disable(EnableCap.Blend);
            return;
        }

        _gl.Enable(EnableCap.Blend);
        _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
        switch (mode)
        {
            case BlendMode.Alpha:
                _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                break;
            case BlendMode.Premultiplied:
                _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                break;
            case BlendMode.Additive:
                _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.One, BlendingFactor.Zero, BlendingFactor.One);
                break;
            case BlendMode.Multiply:
                _gl.BlendFuncSeparate(BlendingFactor.DstColor, BlendingFactor.Zero, BlendingFactor.Zero, BlendingFactor.One);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown blend mode.");
        }
    }

    private void OnDebugMessage(GLEnum source, GLEnum type, int id, GLEnum severity, int length, nint message, nint userParam)
    {
        if (severity == GLEnum.DebugSeverityNotification)
        {
            return;
        }

        var text = Marshal.PtrToStringAnsi(message, length);
        var level = severity switch
        {
            GLEnum.DebugSeverityHigh => LogLevel.Error,
            GLEnum.DebugSeverityMedium => LogLevel.Warning,
            _ => LogLevel.Debug,
        };
        LogDebugMessage(level, type, id, text);
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Graphics device created: {Description} (debug output: {DebugOutput})")]
    private partial void LogCreated(string description, bool debugOutput);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "GPU resource leaked and released at device shutdown: {Resource}")]
    private partial void LogLeak(string resource);

    [LoggerMessage(EventId = 1002, Message = "OpenGL {Type} #{Id}: {Text}")]
    private partial void LogDebugMessage(LogLevel level, GLEnum type, int id, string text);
}
