using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;
using SharpGLTF.Schema2;
using GltfMesh = SharpGLTF.Schema2.Mesh;
using GltfNode = SharpGLTF.Schema2.Node;
using Mesh = MNVTEngine.Graphics.Meshes.Mesh;

namespace MNVTEngine.Assets.Models;

/// <summary>
/// glTF 2.0 (.gltf with external or embedded buffers, and .glb) through SharpGLTF. glTF already uses the engine's
/// conventions (right-handed, +Y up, meters, top-left UV origin) and metallic-roughness materials, so values are taken
/// as they are: each glTF mesh becomes one engine mesh with a submesh per primitive. Textures inside Assets/ are
/// referenced as project textures; embedded ones are kept in the artifact.
/// </summary>
public sealed class GltfModelReader : IModelReader
{
    public IReadOnlyList<string> Extensions { get; } = [".gltf", ".glb"];

    public ModelData Read(ModelSource source, ModelImportSettings settings, ICollection<string> warnings, ISet<Guid> dependencies)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(warnings);
        ModelRoot root;
        try
        {
            var read = new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix };
            if (source.FullPath is not null)
            {
                root = ModelRoot.Load(source.FullPath, read);
                RecordBuffers(source);
            }
            else if (source.Extension == ".glb")
            {
                root = ModelRoot.ParseGLB(source.Data, read);
            }
            else
            {
                throw new InvalidDataException("A .gltf file with external buffers must be imported from disk.");
            }

            return Convert(root, source, settings, warnings, dependencies);
        }
        catch (Exception ex) when (ex is not InvalidDataException and not OutOfMemoryException)
        {
            throw new InvalidDataException($"The glTF file could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>External .bin files are import inputs: changing one reimports the model.</summary>
    private static void RecordBuffers(ModelSource source)
    {
        if (source.Extension != ".gltf" || source.AssetsDirectory is null)
        {
            return;
        }

        var folder = Path.GetDirectoryName(source.RelativePath)?.Replace('\\', '/') ?? "";
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(source.FullPath!)!, "*.bin"))
        {
            var relative = (folder.Length == 0 ? "" : folder + "/") + Path.GetFileName(file);
            if (source.ResolveAsset(relative) is { } guid)
            {
                source.Inputs.Add(guid);
            }
        }
    }

    private static ModelData Convert(ModelRoot root, ModelSource source, ModelImportSettings settings, ICollection<string> warnings, ISet<Guid> dependencies)
    {
        var model = new ModelData();
        var textures = new TextureResolver(source, dependencies);

        if (settings.ImportMaterials)
        {
            var names = ModelProcessing.UniqueNames(root.LogicalMaterials.Select(m => m.Name), "Material");
            var imageIndex = new Dictionary<int, int>();
            foreach (var material in root.LogicalMaterials)
            {
                model.Materials.Add(ConvertMaterial(material, names[material.LogicalIndex], model, textures, imageIndex, source, warnings));
            }
        }

        var meshNames = ModelProcessing.UniqueNames(root.LogicalMeshes.Select(m => m.Name), "Mesh");
        var meshIndex = new Dictionary<int, int>();
        foreach (var mesh in root.LogicalMeshes)
        {
            if (ConvertMesh(mesh, meshNames[mesh.LogicalIndex], settings, warnings) is { } converted)
            {
                meshIndex[mesh.LogicalIndex] = model.Meshes.Count;
                model.Meshes.Add(converted);
            }
        }

        // Nodes of the default scene, parent first, under a root node (glTF scenes can have several roots).
        var scene = root.DefaultScene ?? (root.LogicalScenes.Count > 0 ? root.LogicalScenes[0] : null);
        var order = new List<(GltfNode Node, int Parent)>();
        if (scene is not null)
        {
            foreach (var child in scene.VisualChildren)
            {
                order.Add((child, 0));
            }

            for (var i = 0; i < order.Count; i++)
            {
                foreach (var child in order[i].Node.VisualChildren)
                {
                    order.Add((child, i + 1));
                }
            }
        }

        if (root.LogicalScenes.Count > 1)
        {
            warnings.Add($"The file has {root.LogicalScenes.Count} scenes; only the default scene is imported.");
        }

        var nodeNames = ModelProcessing.UniqueNames(new[] { scene?.Name ?? Path.GetFileNameWithoutExtension(source.RelativePath) }.Concat(order.Select(o => o.Node.Name)), "Node");
        model.Nodes.Add(new ModelNode(nodeNames[0], -1, Vector3.Zero, Quaternion.Identity, Vector3.One));
        var nodeIndex = new Dictionary<int, int>();
        for (var i = 0; i < order.Count; i++)
        {
            var (node, parent) = order[i];
            var local = node.LocalTransform.GetDecomposed(); // nodes may store a matrix
            var mesh = node.Mesh is { } m && meshIndex.TryGetValue(m.LogicalIndex, out var index) ? index : -1;
            nodeIndex[node.LogicalIndex] = i + 1;
            model.Nodes.Add(new ModelNode(nodeNames[i + 1], parent, V3(local.Translation), Q(local.Rotation), V3(local.Scale), mesh));
        }

        if (settings.ImportSkins)
        {
            foreach (var skin in root.LogicalSkins)
            {
                ConvertSkin(skin, model, nodeIndex, order, warnings);
            }
        }

        if (settings.ImportAnimations)
        {
            var names = ModelProcessing.UniqueNames(root.LogicalAnimations.Select(a => a.Name), "Animation");
            foreach (var animation in root.LogicalAnimations)
            {
                model.Animations.Add(ConvertAnimation(animation, names[animation.LogicalIndex], nodeIndex, warnings));
            }
        }

        if (root.LogicalCameras.Count > 0)
        {
            warnings.Add($"The file has {root.LogicalCameras.Count} camera(s); cameras are not imported.");
        }

        return model;
    }

    private static ModelMesh? ConvertMesh(GltfMesh source, string name, ModelImportSettings settings, ICollection<string> warnings)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uv0 = new List<Vector2>();
        var uv1 = new List<Vector2>();
        var colors = new List<Color>();
        var joints = new List<ushort>();
        var weights = new List<float>();
        var indices = new List<uint>();
        var subMeshes = new List<SubMesh>();
        var materials = new List<int>();
        var triangles = source.Primitives.Where(p => p.DrawPrimitiveType == PrimitiveType.TRIANGLES && p.GetVertexAccessor("POSITION") is not null).ToList();
        if (triangles.Count < source.Primitives.Count)
        {
            warnings.Add($"Mesh '{name}': {source.Primitives.Count - triangles.Count} primitive(s) of points, lines or strips were skipped.");
        }

        if (triangles.Count == 0)
        {
            return null;
        }

        bool Has(string attribute) => triangles.All(p => p.GetVertexAccessor(attribute) is not null);
        bool hasNormals = Has("NORMAL"), hasTangents = Has("TANGENT"), hasUv0 = Has("TEXCOORD_0"), hasUv1 = Has("TEXCOORD_1"), hasColors = Has("COLOR_0");
        var skinned = settings.ImportSkins && Has("JOINTS_0") && Has("WEIGHTS_0");
        var morphTargets = 0;
        foreach (var primitive in triangles)
        {
            var baseVertex = (uint)positions.Count;
            var count = primitive.GetVertexAccessor("POSITION").Count;
            positions.AddRange(primitive.GetVertexAccessor("POSITION").AsVector3Array().Select(V3));
            if (hasNormals)
            {
                normals.AddRange(primitive.GetVertexAccessor("NORMAL").AsVector3Array().Select(n => V3(n).Normalized));
            }

            if (hasTangents)
            {
                tangents.AddRange(primitive.GetVertexAccessor("TANGENT").AsVector4Array().Select(t => new Vector4(t.X, t.Y, t.Z, t.W < 0f ? -1f : 1f)));
            }

            if (hasUv0)
            {
                uv0.AddRange(primitive.GetVertexAccessor("TEXCOORD_0").AsVector2Array().Select(V2));
            }

            if (hasUv1)
            {
                uv1.AddRange(primitive.GetVertexAccessor("TEXCOORD_1").AsVector2Array().Select(V2));
            }

            if (hasColors)
            {
                var accessor = primitive.GetVertexAccessor("COLOR_0");
                colors.AddRange(accessor.Dimensions == SharpGLTF.Schema2.DimensionType.VEC3
                    ? accessor.AsVector3Array().Select(c => new Color(c.X, c.Y, c.Z, 1f))
                    : accessor.AsVector4Array().Select(c => new Color(c.X, c.Y, c.Z, c.W)));
            }

            if (skinned)
            {
                var j = primitive.GetVertexAccessor("JOINTS_0").AsVector4Array();
                var w = primitive.GetVertexAccessor("WEIGHTS_0").AsVector4Array();
                for (var v = 0; v < count; v++)
                {
                    var sum = w[v].X + w[v].Y + w[v].Z + w[v].W;
                    var scale = sum > 0f ? 1f / sum : 0f;
                    joints.AddRange([(ushort)j[v].X, (ushort)j[v].Y, (ushort)j[v].Z, (ushort)j[v].W]);
                    weights.AddRange(sum > 0f ? [w[v].X * scale, w[v].Y * scale, w[v].Z * scale, w[v].W * scale] : [1f, 0f, 0f, 0f]);
                }
            }

            morphTargets = Math.Max(morphTargets, primitive.MorphTargetsCount);
            var start = indices.Count;
            var primitiveIndices = primitive.GetIndices();
            if (primitiveIndices is null)
            {
                for (var v = 0u; v < count; v++)
                {
                    indices.Add(baseVertex + v);
                }
            }
            else
            {
                indices.AddRange(primitiveIndices.Select(i => baseVertex + i));
            }

            subMeshes.Add(new SubMesh(start, indices.Count - start));
            materials.Add(settings.ImportMaterials && primitive.Material is { } material ? material.LogicalIndex : -1);
        }

        var mesh = new Mesh(name)
        {
            Positions = [.. positions],
            Normals = hasNormals ? [.. normals] : null,
            Tangents = hasTangents ? [.. tangents] : null,
            UV0 = hasUv0 ? [.. uv0] : null,
            UV1 = hasUv1 ? [.. uv1] : null,
            Colors = hasColors ? [.. colors] : null,
        };
        mesh.SetIndices([.. indices], [.. subMeshes]);
        var result = new ModelMesh(name, mesh) { MorphTargetCount = morphTargets };
        result.SubMeshMaterials.AddRange(materials);
        if (skinned)
        {
            result.Joints = [.. joints];
            result.Weights = [.. weights];
        }

        return result;
    }

    private static ModelMaterial ConvertMaterial(SharpGLTF.Schema2.Material source, string name, ModelData model, TextureResolver textures, Dictionary<int, int> imageIndex, ModelSource modelSource, ICollection<string> warnings)
    {
        var material = new ModelMaterial(name)
        {
            SourceModel = source.Unlit ? "glTF unlit" : "glTF PBR",
            AlphaMode = source.Alpha switch { AlphaMode.MASK => ModelAlphaMode.Mask, AlphaMode.BLEND => ModelAlphaMode.Blend, _ => ModelAlphaMode.Opaque },
            AlphaCutoff = source.AlphaCutoff,
            DoubleSided = source.DoubleSided,
        };

        int? Texture(MaterialChannel? channel, TextureRole role)
        {
            if (channel?.Texture?.PrimaryImage is not { } image)
            {
                return null;
            }

            if (channel.Value.TextureCoordinate != 0)
            {
                warnings.Add($"Material '{name}': the {role} texture uses UV set {channel.Value.TextureCoordinate}; the engine samples UV set 0.");
            }

            if (imageIndex.TryGetValue(image.LogicalIndex, out var existing))
            {
                return existing;
            }

            ModelTexture texture;
            var content = image.Content;
            if (content.SourcePath is { Length: > 0 } path && modelSource.FullPath is not null)
            {
                texture = textures.Resolve(Path.GetRelativePath(Path.GetDirectoryName(modelSource.FullPath)!, path), name, role.ToString());
            }
            else
            {
                texture = new ModelTexture(image.Name is { Length: > 0 } n ? n : $"Image{image.LogicalIndex}") { Embedded = content.Content.ToArray() };
            }

            imageIndex[image.LogicalIndex] = model.Textures.Count;
            model.Textures.Add(texture);
            return model.Textures.Count - 1;
        }

        void Assign(TextureRole role, int? index)
        {
            if (index is { } i)
            {
                material.Textures[role] = i;
            }
        }

        if (source.FindChannel("BaseColor") is { } baseColor)
        {
            var c = baseColor.Color;
            material.BaseColor = new Color(c.X, c.Y, c.Z, c.W);
            Assign(TextureRole.BaseColor, Texture(baseColor, TextureRole.BaseColor));
        }

        if (source.FindChannel("MetallicRoughness") is { } metallicRoughness)
        {
            material.Metallic = metallicRoughness.GetFactor("MetallicFactor");
            material.Roughness = metallicRoughness.GetFactor("RoughnessFactor");
            Assign(TextureRole.MetallicRoughness, Texture(metallicRoughness, TextureRole.MetallicRoughness));
        }

        if (source.FindChannel("Normal") is { } normal)
        {
            material.NormalScale = normal.GetFactor("NormalScale");
            Assign(TextureRole.Normal, Texture(normal, TextureRole.Normal));
        }

        if (source.FindChannel("Occlusion") is { } occlusion)
        {
            material.OcclusionStrength = occlusion.GetFactor("OcclusionStrength");
            Assign(TextureRole.Occlusion, Texture(occlusion, TextureRole.Occlusion));
        }

        if (source.FindChannel("Emissive") is { } emissive)
        {
            var c = emissive.Color;
            var strength = emissive.Parameters.Any(p => p.Name == "EmissiveStrength") ? emissive.GetFactor("EmissiveStrength") : 1f;
            material.Emissive = new Color(c.X * strength, c.Y * strength, c.Z * strength, 1f);
            Assign(TextureRole.Emission, Texture(emissive, TextureRole.Emission));
        }

        return material;
    }

    private static void ConvertSkin(Skin source, ModelData model, Dictionary<int, int> nodeIndex, List<(GltfNode Node, int Parent)> order, ICollection<string> warnings)
    {
        var skin = new ModelSkin(source.Name is { Length: > 0 } n ? n : $"Skin{source.LogicalIndex}");
        for (var i = 0; i < source.JointsCount; i++)
        {
            var (joint, inverseBind) = source.GetJoint(i);
            if (!nodeIndex.TryGetValue(joint.LogicalIndex, out var node))
            {
                warnings.Add($"Skin '{skin.Name}': joint '{joint.Name}' is not in the default scene; the skin was skipped.");
                return;
            }

            skin.Joints.Add(node);
            skin.InverseBindMatrices.Add(FromNumerics(inverseBind));
        }

        var skinIndex = model.Skins.Count;
        model.Skins.Add(skin);
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].Node.Skin?.LogicalIndex == source.LogicalIndex)
            {
                model.Nodes[i + 1] = model.Nodes[i + 1] with { Skin = skinIndex };
            }
        }
    }

    private static ModelAnimation ConvertAnimation(Animation source, string name, Dictionary<int, int> nodeIndex, ICollection<string> warnings)
    {
        var result = new ModelAnimation(name);
        foreach (var channel in source.Channels)
        {
            if (channel.TargetNode is not { } target || !nodeIndex.TryGetValue(target.LogicalIndex, out var node))
            {
                continue;
            }

            switch (channel.TargetNodePath)
            {
                case PropertyPath.translation when channel.GetTranslationSampler() is { } s:
                    result.Channels.Add(Vector3Channel(node, AnimationPath.Translation, s));
                    break;
                case PropertyPath.scale when channel.GetScaleSampler() is { } s:
                    result.Channels.Add(Vector3Channel(node, AnimationPath.Scale, s));
                    break;
                case PropertyPath.rotation when channel.GetRotationSampler() is { } s:
                    result.Channels.Add(RotationChannel(node, s));
                    break;
                case PropertyPath.weights:
                    warnings.Add($"Animation '{name}': morph target weights are not imported yet.");
                    break;
            }
        }

        return result;
    }

    private static AnimationChannel Vector3Channel(int node, AnimationPath path, IAnimationSampler<System.Numerics.Vector3> sampler)
    {
        if (sampler.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE)
        {
            var keys = sampler.GetCubicKeys().ToList();
            return new AnimationChannel(node, path, AnimationInterpolation.CubicSpline, [.. keys.Select(k => k.Key)],
                [.. keys.SelectMany(k => new[] { k.Value.TangentIn.X, k.Value.TangentIn.Y, k.Value.TangentIn.Z, k.Value.Value.X, k.Value.Value.Y, k.Value.Value.Z, k.Value.TangentOut.X, k.Value.TangentOut.Y, k.Value.TangentOut.Z })]);
        }

        var linear = sampler.GetLinearKeys().ToList();
        return new AnimationChannel(node, path, sampler.InterpolationMode == AnimationInterpolationMode.STEP ? AnimationInterpolation.Step : AnimationInterpolation.Linear,
            [.. linear.Select(k => k.Key)], [.. linear.SelectMany(k => new[] { k.Value.X, k.Value.Y, k.Value.Z })]);
    }

    private static AnimationChannel RotationChannel(int node, IAnimationSampler<System.Numerics.Quaternion> sampler)
    {
        if (sampler.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE)
        {
            var keys = sampler.GetCubicKeys().ToList();
            return new AnimationChannel(node, AnimationPath.Rotation, AnimationInterpolation.CubicSpline, [.. keys.Select(k => k.Key)],
                [.. keys.SelectMany(k => new[] { k.Value.TangentIn.X, k.Value.TangentIn.Y, k.Value.TangentIn.Z, k.Value.TangentIn.W, k.Value.Value.X, k.Value.Value.Y, k.Value.Value.Z, k.Value.Value.W, k.Value.TangentOut.X, k.Value.TangentOut.Y, k.Value.TangentOut.Z, k.Value.TangentOut.W })]);
        }

        var linear = sampler.GetLinearKeys().ToList();
        return new AnimationChannel(node, AnimationPath.Rotation, sampler.InterpolationMode == AnimationInterpolationMode.STEP ? AnimationInterpolation.Step : AnimationInterpolation.Linear,
            [.. linear.Select(k => k.Key)], [.. linear.SelectMany(k => new[] { k.Value.X, k.Value.Y, k.Value.Z, k.Value.W })]);
    }

    /// <summary>System.Numerics uses row vectors (translation in M41..M43); the engine uses column vectors.</summary>
    private static Matrix4x4 FromNumerics(System.Numerics.Matrix4x4 m) => new()
    {
        M00 = m.M11, M01 = m.M21, M02 = m.M31, M03 = m.M41,
        M10 = m.M12, M11 = m.M22, M12 = m.M32, M13 = m.M42,
        M20 = m.M13, M21 = m.M23, M22 = m.M33, M23 = m.M43,
        M30 = m.M14, M31 = m.M24, M32 = m.M34, M33 = m.M44,
    };

    private static Vector3 V3(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vector2 V2(System.Numerics.Vector2 v) => new(v.X, v.Y);

    private static Quaternion Q(System.Numerics.Quaternion q) => new(q.X, q.Y, q.Z, q.W);
}
