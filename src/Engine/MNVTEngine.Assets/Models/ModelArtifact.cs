using System.Text;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Assets.Models;

/// <summary>
/// The binary artifact of an imported model (<c>Library/Artifacts/&lt;guid&gt;/model</c>, and in exported games' archives):
/// magic "MNVTMODL", a version, then nodes, meshes (streams, indices, submeshes, skinning), materials, textures
/// (embedded bytes or a project asset), skins and animations. Reading validates every count and index, so a damaged
/// or foreign file is rejected instead of crashing the renderer.
/// </summary>
public static class ModelArtifact
{
    public const string Name = "model";
    public const int Version = 1;
    private static readonly byte[] Magic = "MNVTMODL"u8.ToArray();
    private const int MaxCount = 1 << 24;

    public static byte[] Write(ModelData model)
    {
        ArgumentNullException.ThrowIfNull(model);
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8);
        w.Write(Magic);
        w.Write(Version);

        w.Write(model.Nodes.Count);
        foreach (var n in model.Nodes)
        {
            w.Write(n.Name);
            w.Write(n.Parent);
            WriteVector(w, n.Position);
            w.Write(n.Rotation.X);
            w.Write(n.Rotation.Y);
            w.Write(n.Rotation.Z);
            w.Write(n.Rotation.W);
            WriteVector(w, n.Scale);
            w.Write(n.Mesh);
            w.Write(n.Skin);
        }

        w.Write(model.Meshes.Count);
        foreach (var m in model.Meshes)
        {
            WriteMesh(w, m);
        }

        w.Write(model.Materials.Count);
        foreach (var m in model.Materials)
        {
            w.Write(m.Name);
            w.Write(m.SourceModel);
            WriteColor(w, m.BaseColor);
            w.Write(m.Metallic);
            w.Write(m.Roughness);
            WriteColor(w, m.Emissive);
            w.Write(m.NormalScale);
            w.Write(m.OcclusionStrength);
            w.Write((int)m.AlphaMode);
            w.Write(m.AlphaCutoff);
            w.Write(m.DoubleSided);
            w.Write(m.Textures.Count);
            foreach (var (role, index) in m.Textures.OrderBy(t => t.Key))
            {
                w.Write((int)role);
                w.Write(index);
            }
        }

        w.Write(model.Textures.Count);
        foreach (var t in model.Textures)
        {
            w.Write(t.Name);
            w.Write(t.SourcePath ?? "");
            w.Write(t.ProjectAsset?.ToString("D") ?? "");
            w.Write(t.Embedded?.Length ?? -1);
            if (t.Embedded is { } bytes)
            {
                w.Write(bytes);
            }
        }

        w.Write(model.Skins.Count);
        foreach (var s in model.Skins)
        {
            w.Write(s.Name);
            w.Write(s.Joints.Count);
            for (var i = 0; i < s.Joints.Count; i++)
            {
                w.Write(s.Joints[i]);
                var span = Matrix4x4.AsSpan(s.InverseBindMatrices[i]);
                foreach (var f in span)
                {
                    w.Write(f);
                }
            }
        }

        w.Write(model.Animations.Count);
        foreach (var a in model.Animations)
        {
            w.Write(a.Name);
            w.Write(a.Channels.Count);
            foreach (var c in a.Channels)
            {
                w.Write(c.Node);
                w.Write((int)c.Path);
                w.Write((int)c.Interpolation);
                WriteFloats(w, c.Times);
                WriteFloats(w, c.Values);
            }
        }

        w.Write(model.Warnings.Count);
        foreach (var warning in model.Warnings)
        {
            w.Write(warning);
        }

        w.Flush();
        return stream.ToArray();
    }

    /// <exception cref="InvalidDataException">Not a model artifact, another version, or inconsistent data.</exception>
    public static ModelData Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var r = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8);
            if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            {
                throw new InvalidDataException("Not a model artifact.");
            }

            var version = r.ReadInt32();
            if (version != Version)
            {
                throw new InvalidDataException($"Model artifact version {version} is not supported (expected {Version}); reimport the model.");
            }

            var model = new ModelData();
            for (int i = 0, n = Count(r); i < n; i++)
            {
                model.Nodes.Add(new ModelNode(r.ReadString(), r.ReadInt32(), ReadVector(r), new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), ReadVector(r), r.ReadInt32(), r.ReadInt32()));
            }

            for (int i = 0, n = Count(r); i < n; i++)
            {
                model.Meshes.Add(ReadMesh(r));
            }

            for (int i = 0, n = Count(r); i < n; i++)
            {
                var m = new ModelMaterial(r.ReadString())
                {
                    SourceModel = r.ReadString(),
                    BaseColor = ReadColor(r),
                    Metallic = r.ReadSingle(),
                    Roughness = r.ReadSingle(),
                    Emissive = ReadColor(r),
                    NormalScale = r.ReadSingle(),
                    OcclusionStrength = r.ReadSingle(),
                    AlphaMode = Enum<ModelAlphaMode>(r.ReadInt32()),
                    AlphaCutoff = r.ReadSingle(),
                    DoubleSided = r.ReadBoolean(),
                };
                for (int t = 0, tn = Count(r); t < tn; t++)
                {
                    m.Textures[Enum<TextureRole>(r.ReadInt32())] = r.ReadInt32();
                }

                model.Materials.Add(m);
            }

            for (int i = 0, n = Count(r); i < n; i++)
            {
                var t = new ModelTexture(r.ReadString());
                var source = r.ReadString();
                var asset = r.ReadString();
                (t.SourcePath, t.ProjectAsset) = (source.Length == 0 ? null : source, asset.Length == 0 ? null : Guid.Parse(asset));
                var length = r.ReadInt32();
                if (length >= 0)
                {
                    t.Embedded = r.ReadBytes(Check(length));
                }

                model.Textures.Add(t);
            }

            for (int i = 0, n = Count(r); i < n; i++)
            {
                var s = new ModelSkin(r.ReadString());
                for (int j = 0, jn = Count(r); j < jn; j++)
                {
                    s.Joints.Add(r.ReadInt32());
                    var m = new Matrix4x4();
                    var span = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref m.M00, 16);
                    for (var k = 0; k < 16; k++)
                    {
                        span[k] = r.ReadSingle();
                    }

                    s.InverseBindMatrices.Add(m);
                }

                model.Skins.Add(s);
            }

            for (int i = 0, n = Count(r); i < n; i++)
            {
                var a = new ModelAnimation(r.ReadString());
                for (int c = 0, cn = Count(r); c < cn; c++)
                {
                    a.Channels.Add(new AnimationChannel(r.ReadInt32(), Enum<AnimationPath>(r.ReadInt32()), Enum<AnimationInterpolation>(r.ReadInt32()), ReadFloats(r), ReadFloats(r)));
                }

                model.Animations.Add(a);
            }

            for (int i = 0, n = Count(r); i < n; i++)
            {
                model.Warnings.Add(r.ReadString());
            }

            Validate(model);
            return model;
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The model artifact is truncated.", ex);
        }
    }

    private static void WriteMesh(BinaryWriter w, ModelMesh model)
    {
        var mesh = model.Mesh;
        w.Write(model.Name);
        WriteVectors(w, mesh.Positions);
        WriteOptional(w, mesh.Normals, (bw, v) => WriteVector(bw, v));
        WriteOptional(w, mesh.Tangents, (bw, v) => { bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z); bw.Write(v.W); });
        WriteOptional(w, mesh.UV0, (bw, v) => { bw.Write(v.X); bw.Write(v.Y); });
        WriteOptional(w, mesh.UV1, (bw, v) => { bw.Write(v.X); bw.Write(v.Y); });
        WriteOptional(w, mesh.Colors, WriteColor);
        w.Write(mesh.IndexSpan.Length);
        foreach (var index in mesh.IndexSpan)
        {
            w.Write(index);
        }

        w.Write(mesh.SubMeshes.Count);
        for (var i = 0; i < mesh.SubMeshes.Count; i++)
        {
            w.Write(mesh.SubMeshes[i].IndexStart);
            w.Write(mesh.SubMeshes[i].IndexCount);
            w.Write(i < model.SubMeshMaterials.Count ? model.SubMeshMaterials[i] : -1);
        }

        w.Write(model.Joints is not null);
        if (model.Joints is not null)
        {
            foreach (var j in model.Joints)
            {
                w.Write(j);
            }

            WriteFloats(w, model.Weights!);
        }

        w.Write(model.MorphTargetCount);
    }

    private static ModelMesh ReadMesh(BinaryReader r)
    {
        var name = r.ReadString();
        var mesh = new Mesh(name) { Positions = ReadVectors(r) };
        mesh.Normals = ReadOptional(r, ReadVector);
        mesh.Tangents = ReadOptional(r, br => new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
        mesh.UV0 = ReadOptional(r, br => new Vector2(br.ReadSingle(), br.ReadSingle()));
        mesh.UV1 = ReadOptional(r, br => new Vector2(br.ReadSingle(), br.ReadSingle()));
        mesh.Colors = ReadOptional(r, ReadColor);
        var indices = new uint[Count(r)];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = r.ReadUInt32();
        }

        var subCount = Count(r);
        var subs = new SubMesh[subCount];
        var materials = new List<int>(subCount);
        for (var i = 0; i < subCount; i++)
        {
            subs[i] = new SubMesh(r.ReadInt32(), r.ReadInt32());
            materials.Add(r.ReadInt32());
        }

        mesh.SetIndices(indices, subs);
        var result = new ModelMesh(name, mesh);
        result.SubMeshMaterials.AddRange(materials);
        if (r.ReadBoolean())
        {
            var joints = new ushort[mesh.VertexCount * 4];
            for (var i = 0; i < joints.Length; i++)
            {
                joints[i] = r.ReadUInt16();
            }

            result.Joints = joints;
            result.Weights = ReadFloats(r);
        }

        result.MorphTargetCount = r.ReadInt32();
        return result;
    }

    private static void Validate(ModelData model)
    {
        void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidDataException("Inconsistent model artifact: " + message);
            }
        }

        for (var i = 0; i < model.Nodes.Count; i++)
        {
            var n = model.Nodes[i];
            Require(i == 0 ? n.Parent == -1 : n.Parent >= 0 && n.Parent < i, $"node {i} has parent {n.Parent} (nodes are parent-first)");
            Require(n.Mesh >= -1 && n.Mesh < model.Meshes.Count, $"node {i} refers to mesh {n.Mesh}");
            Require(n.Skin >= -1 && n.Skin < model.Skins.Count, $"node {i} refers to skin {n.Skin}");
        }

        foreach (var m in model.Meshes)
        {
            Require(m.Mesh.Validate().Count == 0, $"mesh '{m.Name}': {string.Join(" ", m.Mesh.Validate())}");
            Require(m.SubMeshMaterials.All(x => x >= -1 && x < model.Materials.Count), $"mesh '{m.Name}' refers to a missing material");
            Require(m.Joints is null || (m.Joints.Length == m.Mesh.VertexCount * 4 && m.Weights?.Length == m.Joints.Length), $"mesh '{m.Name}' has inconsistent skin weights");
        }

        Require(model.Materials.All(m => m.Textures.Values.All(t => t >= 0 && t < model.Textures.Count)), "a material refers to a missing texture");
        Require(model.Skins.All(s => s.Joints.All(j => j >= 0 && j < model.Nodes.Count)), "a skin refers to a missing joint node");
        foreach (var c in model.Animations.SelectMany(a => a.Channels))
        {
            var width = (c.Path == AnimationPath.Rotation ? 4 : 3) * (c.Interpolation == AnimationInterpolation.CubicSpline ? 3 : 1);
            Require(c.Node >= 0 && c.Node < model.Nodes.Count && c.Values.Length == c.Times.Length * width, $"an animation channel of node {c.Node} has {c.Values.Length} values for {c.Times.Length} keys");
        }
    }

    private static int Count(BinaryReader r) => Check(r.ReadInt32());

    private static int Check(int count) => count is >= 0 and <= MaxCount ? count : throw new InvalidDataException($"Invalid count {count} in model artifact.");

    private static T Enum<T>(int value)
        where T : struct, System.Enum => System.Enum.IsDefined(typeof(T), value) ? (T)(object)value : throw new InvalidDataException($"Invalid {typeof(T).Name} {value} in model artifact.");

    private static void WriteVector(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static Vector3 ReadVector(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    private static void WriteColor(BinaryWriter w, Color c)
    {
        w.Write(c.R);
        w.Write(c.G);
        w.Write(c.B);
        w.Write(c.A);
    }

    private static Color ReadColor(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    private static void WriteVectors(BinaryWriter w, Vector3[] values)
    {
        w.Write(values.Length);
        foreach (var v in values)
        {
            WriteVector(w, v);
        }
    }

    private static Vector3[] ReadVectors(BinaryReader r)
    {
        var values = new Vector3[Count(r)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = ReadVector(r);
        }

        return values;
    }

    private static void WriteOptional<T>(BinaryWriter w, T[]? values, Action<BinaryWriter, T> write)
    {
        w.Write(values?.Length ?? -1);
        foreach (var v in values ?? [])
        {
            write(w, v);
        }
    }

    private static T[]? ReadOptional<T>(BinaryReader r, Func<BinaryReader, T> read)
    {
        var length = r.ReadInt32();
        if (length < 0)
        {
            return null;
        }

        var values = new T[Check(length)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = read(r);
        }

        return values;
    }

    private static void WriteFloats(BinaryWriter w, float[] values)
    {
        w.Write(values.Length);
        foreach (var v in values)
        {
            w.Write(v);
        }
    }

    private static float[] ReadFloats(BinaryReader r)
    {
        var values = new float[Count(r)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = r.ReadSingle();
        }

        return values;
    }
}
