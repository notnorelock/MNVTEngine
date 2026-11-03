using MNVTEngine.Mathematics;
using Silk.NET.OpenGL;

namespace MNVTEngine.Graphics.OpenGL;

internal sealed class GLShader : Shader
{
    private readonly GL _gl;
    private readonly Dictionary<string, int> _locations = new(StringComparer.Ordinal);

    public GLShader(GL gl, GraphicsResourceTracker tracker, ShaderSource source)
        : base(tracker, source.Name)
    {
        _gl = gl;
        uint vertex = 0, fragment = 0;
        try
        {
            vertex = Compile(ShaderType.VertexShader, source.VertexGlsl, "vertex");
            fragment = Compile(ShaderType.FragmentShader, source.FragmentGlsl, "fragment");

            Program = gl.CreateProgram();
            gl.AttachShader(Program, vertex);
            gl.AttachShader(Program, fragment);
            gl.LinkProgram(Program);
            gl.GetProgram(Program, ProgramPropertyARB.LinkStatus, out var linked);
            gl.DetachShader(Program, vertex);
            gl.DetachShader(Program, fragment);
            if (linked == 0)
            {
                throw new ShaderCompilationException(source.Name, "link", gl.GetProgramInfoLog(Program));
            }

            GLUtil.Label(gl, ObjectIdentifier.Program, Program, source.Name);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (vertex != 0)
            {
                gl.DeleteShader(vertex);
            }

            if (fragment != 0)
            {
                gl.DeleteShader(fragment);
            }
        }
    }

    public uint Program { get; }

    public override bool HasUniform(string name) => Location(name) >= 0;

    public int Location(string name)
    {
        if (!_locations.TryGetValue(name, out var location))
        {
            location = _gl.GetUniformLocation(Program, name);
            _locations[name] = location;
        }

        return location;
    }

    public unsafe void Set(int location, in Matrix4x4 value)
    {
        // Column-major storage matches GL, so no transpose.
        fixed (float* p = &value.M00)
        {
            _gl.ProgramUniformMatrix4(Program, location, 1, false, p);
        }
    }

    public void Set(int location, in UniformValue value)
    {
        var v = value.Vector;
        switch (value.Kind)
        {
            case UniformKind.Float:
                _gl.ProgramUniform1(Program, location, v.X);
                break;
            case UniformKind.Int:
                _gl.ProgramUniform1(Program, location, value.Integer);
                break;
            case UniformKind.Vector2:
                _gl.ProgramUniform2(Program, location, v.X, v.Y);
                break;
            case UniformKind.Vector4:
                _gl.ProgramUniform4(Program, location, v.X, v.Y, v.Z, v.W);
                break;
            case UniformKind.Matrix4x4:
                Set(location, value.Matrix);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Unknown uniform kind.");
        }
    }

    protected override void ReleaseCore()
    {
        if (Program != 0)
        {
            _gl.DeleteProgram(Program);
        }
    }

    private uint Compile(ShaderType type, string glsl, string stage)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, glsl);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0)
        {
            var log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new ShaderCompilationException(DebugName, stage, log);
        }

        return shader;
    }
}
