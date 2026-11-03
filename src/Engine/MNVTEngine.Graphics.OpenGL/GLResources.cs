using Silk.NET.OpenGL;

namespace MNVTEngine.Graphics.OpenGL;

internal interface IGLTexture
{
    uint Handle { get; }
}

internal sealed class GLTexture : Texture2D, IGLTexture
{
    private readonly GL _gl;

    public unsafe GLTexture(GL gl, GraphicsResourceTracker tracker, string name, int width, int height, ReadOnlySpan<byte> rgba, SamplerState sampler)
        : base(tracker, name, width, height, sampler)
    {
        if (rgba.Length != width * height * 4)
        {
            Dispose();
            throw new ArgumentException($"Expected {width * height * 4} bytes of RGBA8 data, got {rgba.Length}.", nameof(rgba));
        }

        _gl = gl;
        Handle = GLUtil.CreateTexture(gl, name, width, height, sampler);
        fixed (byte* pixels = rgba)
        {
            gl.TextureSubImage2D(Handle, 0, 0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        }

        if (sampler.GenerateMipmaps)
        {
            gl.GenerateTextureMipmap(Handle);
        }
    }

    public uint Handle { get; }

    protected override void ReleaseCore()
    {
        if (Handle != 0)
        {
            _gl.DeleteTexture(Handle);
        }
    }
}

internal sealed class GLRenderTarget : RenderTarget2D, IGLTexture
{
    private readonly GL _gl;

    public GLRenderTarget(GL gl, GraphicsResourceTracker tracker, string name, int width, int height, SamplerState sampler)
        : base(tracker, name, width, height, sampler with { GenerateMipmaps = false })
    {
        _gl = gl;
        Handle = GLUtil.CreateTexture(gl, name, width, height, Sampler);
        Framebuffer = gl.CreateFramebuffer();
        gl.NamedFramebufferTexture(Framebuffer, FramebufferAttachment.ColorAttachment0, Handle, 0);
        var status = (GLEnum)gl.CheckNamedFramebufferStatus(Framebuffer, FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            Dispose();
            throw new InvalidOperationException($"Render target '{name}' ({width}x{height}) is incomplete: {status}.");
        }

        GLUtil.Label(gl, ObjectIdentifier.Framebuffer, Framebuffer, name);
    }

    public uint Handle { get; }

    public uint Framebuffer { get; }

    protected override void ReleaseCore()
    {
        if (Framebuffer != 0)
        {
            _gl.DeleteFramebuffer(Framebuffer);
        }

        if (Handle != 0)
        {
            _gl.DeleteTexture(Handle);
        }
    }
}

internal sealed class GLVertexBuffer : VertexBuffer
{
    private readonly GL _gl;
    private readonly nuint _sizeInBytes;

    public unsafe GLVertexBuffer(GL gl, GraphicsResourceTracker tracker, string name, VertexLayout layout, int capacity)
        : base(tracker, name, layout, capacity)
    {
        _gl = gl;
        _sizeInBytes = (nuint)(layout.Stride * capacity);
        Buffer = gl.CreateBuffer();
        gl.NamedBufferData(Buffer, _sizeInBytes, null, BufferUsageARB.DynamicDraw);

        VertexArray = gl.CreateVertexArray();
        gl.VertexArrayVertexBuffer(VertexArray, 0, Buffer, 0, (uint)layout.Stride);
        foreach (var e in layout.Elements)
        {
            var location = (uint)e.Location;
            gl.EnableVertexArrayAttrib(VertexArray, location);
            var (type, normalized) = e.Type == VertexElementType.UNormByte
                ? (VertexAttribType.UnsignedByte, true)
                : (VertexAttribType.Float, false);
            gl.VertexArrayAttribFormat(VertexArray, location, e.Components, type, normalized, (uint)e.Offset);
            gl.VertexArrayAttribBinding(VertexArray, location, 0);
        }

        GLUtil.Label(gl, ObjectIdentifier.Buffer, Buffer, name);
        GLUtil.Label(gl, ObjectIdentifier.VertexArray, VertexArray, name);
    }

    public uint Buffer { get; }

    public uint VertexArray { get; }

    /// <summary>Last index buffer attached to the VAO, to skip redundant rebinds.</summary>
    public uint AttachedIndexBuffer { get; set; }

    protected override unsafe void SetDataCore(ReadOnlySpan<byte> data)
    {
        // Orphan the old storage so the driver need not wait for in-flight draws that read it.
        _gl.NamedBufferData(Buffer, _sizeInBytes, null, BufferUsageARB.DynamicDraw);
        fixed (byte* p = data)
        {
            _gl.NamedBufferSubData(Buffer, 0, (nuint)data.Length, p);
        }
    }

    protected override void ReleaseCore()
    {
        _gl.DeleteVertexArray(VertexArray);
        _gl.DeleteBuffer(Buffer);
    }
}

internal sealed class GLIndexBuffer : IndexBuffer
{
    private readonly GL _gl;

    public unsafe GLIndexBuffer(GL gl, GraphicsResourceTracker tracker, string name, ReadOnlySpan<ushort> indices)
        : base(tracker, name, indices.Length)
    {
        _gl = gl;
        Buffer = gl.CreateBuffer();
        fixed (ushort* p = indices)
        {
            gl.NamedBufferStorage(Buffer, (nuint)(indices.Length * sizeof(ushort)), p, BufferStorageMask.None);
        }

        GLUtil.Label(gl, ObjectIdentifier.Buffer, Buffer, name);
    }

    public uint Buffer { get; }

    protected override void ReleaseCore() => _gl.DeleteBuffer(Buffer);
}

internal static class GLUtil
{
    public static uint CreateTexture(GL gl, string name, int width, int height, SamplerState sampler)
    {
        var levels = sampler.GenerateMipmaps ? (uint)(1 + Math.Floor(Math.Log2(Math.Max(width, height)))) : 1u;
        var handle = gl.CreateTexture(TextureTarget.Texture2D);
        gl.TextureStorage2D(handle, levels, SizedInternalFormat.Rgba8, (uint)width, (uint)height);

        var (min, mag) = sampler.Filter switch
        {
            TextureFilter.Point => (sampler.GenerateMipmaps ? GLEnum.NearestMipmapNearest : GLEnum.Nearest, GLEnum.Nearest),
            _ => (sampler.GenerateMipmaps ? GLEnum.LinearMipmapLinear : GLEnum.Linear, GLEnum.Linear),
        };
        var wrap = sampler.Wrap switch
        {
            TextureWrap.Repeat => GLEnum.Repeat,
            TextureWrap.MirroredRepeat => GLEnum.MirroredRepeat,
            _ => GLEnum.ClampToEdge,
        };

        gl.TextureParameter(handle, TextureParameterName.TextureMinFilter, (int)min);
        gl.TextureParameter(handle, TextureParameterName.TextureMagFilter, (int)mag);
        gl.TextureParameter(handle, TextureParameterName.TextureWrapS, (int)wrap);
        gl.TextureParameter(handle, TextureParameterName.TextureWrapT, (int)wrap);
        Label(gl, ObjectIdentifier.Texture, handle, name);
        return handle;
    }

    /// <summary>Names the object for GPU debuggers such as RenderDoc.</summary>
    public static void Label(GL gl, ObjectIdentifier type, uint handle, string name) =>
        gl.ObjectLabel(type, handle, (uint)name.Length, name);
}
