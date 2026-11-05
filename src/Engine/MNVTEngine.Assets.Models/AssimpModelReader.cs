using System.Runtime.InteropServices;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;
using Silk.NET.Assimp;
using AiMaterial = Silk.NET.Assimp.Material;
using AiMesh = Silk.NET.Assimp.Mesh;
using AiNode = Silk.NET.Assimp.Node;
using AiScene = Silk.NET.Assimp.Scene;
using Mesh = MNVTEngine.Graphics.Meshes.Mesh;
using NumericsMatrix = System.Numerics.Matrix4x4;

namespace MNVTEngine.Assets.Models;

/// <summary>
/// FBX through Assimp 6 (binary and ASCII FBX 7.x). Assimp triangulates, splits meshes per material (each becomes a
/// submesh of one engine mesh per node), converts units to meters, flips UVs to the engine's top-left origin, limits
/// skin influences to four, and reads embedded media. Materials are converted to metallic-roughness: PBR properties
/// when the file has them, otherwise Phong (diffuse, shininess, opacity, emission). Lights, cameras and morph targets
/// are reported, not imported.
/// </summary>
public sealed unsafe class AssimpModelReader : IModelReader
{
    private static readonly Lazy<Assimp> Api = new(Assimp.GetApi);

    // aiProcess_GlobalScale: applies the FBX unit scale (AI_CONFIG_FBX_CONVERT_TO_M); Silk.NET's enum has no name for it.
    private const PostProcessSteps GlobalScale = (PostProcessSteps)0x8000000;

    public IReadOnlyList<string> Extensions { get; } = [".fbx"];

    public ModelData Read(ModelSource source, ModelImportSettings settings, ICollection<string> warnings, ISet<Guid> dependencies)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(warnings);
        var api = Api.Value;
        var flags = PostProcessSteps.Triangulate | PostProcessSteps.SortByPrimitiveType | PostProcessSteps.FlipUVs
            | PostProcessSteps.LimitBoneWeights | PostProcessSteps.ValidateDataStructure | GlobalScale;
        if (settings.OptimizeMeshes)
        {
            flags |= PostProcessSteps.JoinIdenticalVertices | PostProcessSteps.ImproveCacheLocality;
        }

        var store = api.CreatePropertyStore();
        AiScene* scene;
        try
        {
            api.SetImportPropertyInteger(store, "AI_CONFIG_FBX_CONVERT_TO_M", 1); // centimeter files become meters
            api.SetImportPropertyInteger(store, "AI_CONFIG_PP_LBW_MAX_WEIGHTS", 4);
            fixed (byte* data = source.Data)
            {
                scene = api.ImportFileFromMemoryWithProperties(data, (uint)source.Data.Length, (uint)flags, "fbx", store);
            }
        }
        finally
        {
            api.ReleasePropertyStore(store);
        }

        if (scene is null || (scene->MFlags & Assimp.SceneFlagsIncomplete) != 0 || scene->MRootNode is null)
        {
            var reason = api.GetErrorStringS();
            if (scene is not null)
            {
                api.ReleaseImport(scene);
            }

            throw new InvalidDataException($"Assimp could not read the FBX file: {(string.IsNullOrWhiteSpace(reason) ? "incomplete scene" : reason)}");
        }

        try
        {
            return Convert(api, scene, source, settings, warnings, dependencies);
        }
        finally
        {
            api.ReleaseImport(scene);
        }
    }

    private static ModelData Convert(Assimp api, AiScene* scene, ModelSource source, ModelImportSettings settings, ICollection<string> warnings, ISet<Guid> dependencies)
    {
        var model = new ModelData();
        var textures = new TextureResolver(source, dependencies);
        var embeddedIndex = new Dictionary<int, int>();

        // Materials.
        var materialNames = ModelProcessing.UniqueNames(Enumerable.Range(0, (int)scene->MNumMaterials).Select(i => MaterialName(api, scene->MMaterials[i])), "Material");
        if (settings.ImportMaterials)
        {
            for (var i = 0; i < scene->MNumMaterials; i++)
            {
                model.Materials.Add(ConvertMaterial(api, scene, scene->MMaterials[i], materialNames[i], model, textures, embeddedIndex));
            }
        }

        // Nodes, parent first; nodes with meshes get one engine mesh with a submesh per Assimp mesh (material).
        var nodeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodeNames = new List<string?>();
        var queue = new List<(nint Node, int Parent)> { ((nint)scene->MRootNode, -1) };
        for (var q = 0; q < queue.Count; q++)
        {
            var node = (AiNode*)queue[q].Node;
            nodeNames.Add(node->MName.AsString);
            for (var c = 0; c < node->MNumChildren; c++)
            {
                queue.Add(((nint)node->MChildren[c], q));
            }
        }

        var uniqueNodeNames = ModelProcessing.UniqueNames(nodeNames, "Node");
        var meshNames = new List<string?>();
        for (var q = 0; q < queue.Count; q++)
        {
            var node = (AiNode*)queue[q].Node;
            nodeIndex.TryAdd(node->MName.AsString, q);
            // Meshes are named after their node (the object name artists keep), not the FBX geometry name, which tools
            // rename freely ("Cube.002"): references to the mesh then survive a re-save of the file.
            meshNames.Add(node->MNumMeshes > 0 ? uniqueNodeNames[q] : null);
        }

        var uniqueMeshNames = ModelProcessing.UniqueNames(meshNames.Where(n => n is not null), "Mesh");
        var meshCursor = 0;
        var skinnedMeshes = new List<(int Mesh, List<nint> Parts)>();
        // Morph animation channels name a node or an Assimp mesh; their key values index that mesh's anim meshes.
        var morphTargets = new Dictionary<string, (int Node, int[] ShapeOf)>(StringComparer.Ordinal);
        for (var q = 0; q < queue.Count; q++)
        {
            var node = (AiNode*)queue[q].Node;
            Decompose(api, node->MTransformation, out var position, out var rotation, out var scale);
            var meshIndex = -1;
            if (node->MNumMeshes > 0)
            {
                var parts = Enumerable.Range(0, (int)node->MNumMeshes).Select(i => (nint)scene->MMeshes[node->MMeshes[i]]).ToList();
                meshIndex = model.Meshes.Count;
                var converted = ConvertMesh(parts, uniqueMeshNames[meshCursor++], settings, warnings);
                model.Meshes.Add(converted);
                foreach (var part in parts)
                {
                    var aiMesh = (AiMesh*)part;
                    var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                    var shapeOf = Enumerable.Range(0, (int)aiMesh->MNumAnimMeshes).Select(s => ShapeName(aiMesh, s, seen))
                        .Select(shapeName => converted.BlendShapes.FindIndex(b => b.Name == shapeName)).ToArray();
                    morphTargets.TryAdd(aiMesh->MName.AsString, (q, shapeOf));
                    morphTargets.TryAdd(node->MName.AsString, (q, shapeOf));
                }
                if (settings.ImportSkins && parts.Any(p => ((AiMesh*)p)->MNumBones > 0))
                {
                    skinnedMeshes.Add((meshIndex, parts));
                }
            }

            model.Nodes.Add(new ModelNode(uniqueNodeNames[q], queue[q].Parent, position, rotation, scale, meshIndex));
        }

        foreach (var (meshIndex, parts) in skinnedMeshes)
        {
            var skin = ConvertSkin(model, model.Meshes[meshIndex], parts, nodeIndex, warnings);
            if (skin is not null)
            {
                var nodeWithMesh = model.Nodes.FindIndex(n => n.Mesh == meshIndex);
                model.Nodes[nodeWithMesh] = model.Nodes[nodeWithMesh] with { Skin = model.Skins.Count };
                model.Skins.Add(skin);
            }
        }

        if (settings.ImportAnimations)
        {
            var names = ModelProcessing.UniqueNames(Enumerable.Range(0, (int)scene->MNumAnimations).Select(i => scene->MAnimations[i]->MName.AsString), "Animation");
            for (var i = 0; i < scene->MNumAnimations; i++)
            {
                var animation = ConvertAnimation(scene->MAnimations[i], names[i], nodeIndex, warnings);
                AddMorphChannels(scene->MAnimations[i], animation, model, morphTargets, warnings);
                model.Animations.Add(animation);
            }
        }

        if (scene->MNumLights > 0)
        {
            warnings.Add($"The file has {scene->MNumLights} light(s); lights are not imported.");
        }

        if (scene->MNumCameras > 0)
        {
            warnings.Add($"The file has {scene->MNumCameras} camera(s); cameras are not imported.");
        }

        return model;
    }

    private static ModelMesh ConvertMesh(List<nint> parts, string name, ModelImportSettings settings, ICollection<string> warnings)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uv0 = new List<Vector2>();
        var uv1 = new List<Vector2>();
        var colors = new List<Color>();
        var indices = new List<uint>();
        var subMeshes = new List<SubMesh>();
        var materials = new List<int>();
        bool hasNormals = true, hasTangents = true, hasUv0 = true, hasUv1 = true, hasColors = true;
        var ranges = new List<(nint Part, int BaseVertex)>();
        foreach (var part in parts)
        {
            var m = (AiMesh*)part;
            hasNormals &= m->MNormals is not null;
            hasTangents &= m->MTangents is not null && m->MBitangents is not null;
            hasUv0 &= m->MTextureCoords.Element0 is not null;
            hasUv1 &= m->MTextureCoords.Element1 is not null;
            hasColors &= m->MColors.Element0 is not null;
        }

        foreach (var part in parts)
        {
            var m = (AiMesh*)part;
            if ((m->MPrimitiveTypes & (uint)PrimitiveType.Triangle) == 0)
            {
                warnings.Add($"Mesh '{name}': a part made of points or lines was skipped.");
                continue;
            }

            var baseVertex = (uint)positions.Count;
            ranges.Add((part, (int)baseVertex));
            for (var v = 0; v < m->MNumVertices; v++)
            {
                positions.Add(V3(m->MVertices[v]));
                if (hasNormals)
                {
                    normals.Add(V3(m->MNormals[v]).Normalized);
                }

                if (hasTangents)
                {
                    var n = V3(m->MNormals[v]);
                    var t = V3(m->MTangents[v]);
                    var w = Vector3.Dot(Vector3.Cross(n, t), V3(m->MBitangents[v])) < 0f ? -1f : 1f;
                    tangents.Add(new Vector4(t.X, t.Y, t.Z, w));
                }

                if (hasUv0)
                {
                    uv0.Add(new Vector2(m->MTextureCoords.Element0[v].X, m->MTextureCoords.Element0[v].Y));
                }

                if (hasUv1)
                {
                    uv1.Add(new Vector2(m->MTextureCoords.Element1[v].X, m->MTextureCoords.Element1[v].Y));
                }

                if (hasColors)
                {
                    var c = m->MColors.Element0[v];
                    colors.Add(new Color(c.X, c.Y, c.Z, c.W));
                }
            }

            var start = indices.Count;
            for (var f = 0; f < m->MNumFaces; f++)
            {
                var face = m->MFaces[f];
                if (face.MNumIndices != 3)
                {
                    continue; // points and lines left after triangulation
                }

                indices.Add(baseVertex + face.MIndices[0]);
                indices.Add(baseVertex + face.MIndices[1]);
                indices.Add(baseVertex + face.MIndices[2]);
            }

            subMeshes.Add(new SubMesh(start, indices.Count - start));
            materials.Add(settings.ImportMaterials ? (int)m->MMaterialIndex : -1);
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
        var result = new ModelMesh(name, mesh);
        result.SubMeshMaterials.AddRange(materials);
        AddBlendShapes(result, ranges, hasNormals, hasTangents);
        return result;
    }

    /// <summary>
    /// The name of anim mesh <paramref name="index"/>: its own, with "_2", "_3"... when the part repeats it (exporters may
    /// write the same name for every shape), or "Shape{index}" when empty. Parts of a node then match by these names.
    /// </summary>
    private static string ShapeName(AiMesh* mesh, int index, Dictionary<string, int> seen)
    {
        var name = mesh->MAnimMeshes[index]->MName.AsString is { Length: > 0 } n ? n : $"Shape{index}";
        var count = seen.GetValueOrDefault(name) + 1;
        seen[name] = count;
        return count == 1 ? name : $"{name}_{count}";
    }

    /// <summary>
    /// Blend shapes from Assimp's anim meshes, which hold replacement positions (base + offset): converted to offsets.
    /// The parts of a node (one per material) are matched by shape name; a part without a shape gets zero offsets.
    /// </summary>
    private static void AddBlendShapes(ModelMesh result, List<(nint Part, int BaseVertex)> ranges, bool hasNormals, bool hasTangents)
    {
        var vertexCount = result.Mesh.VertexCount;
        var order = new List<string>();
        var shapes = new Dictionary<string, (Vector3[] Positions, Vector3[]? Normals, Vector3[]? Tangents, float Weight)>(StringComparer.Ordinal);
        foreach (var (part, baseVertex) in ranges)
        {
            var m = (AiMesh*)part;
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var s = 0; s < m->MNumAnimMeshes; s++)
            {
                var shapeName = ShapeName(m, s, seen);
                var anim = m->MAnimMeshes[s];
                if (!shapes.TryGetValue(shapeName, out var shape))
                {
                    shape = (new Vector3[vertexCount], hasNormals ? new Vector3[vertexCount] : null, hasTangents ? new Vector3[vertexCount] : null, anim->MWeight);
                    shapes[shapeName] = shape;
                    order.Add(shapeName);
                }

                var count = Math.Min((int)m->MNumVertices, (int)anim->MNumVertices);
                for (var v = 0; v < count; v++)
                {
                    if (anim->MVertices is not null)
                    {
                        shape.Positions[baseVertex + v] = V3(anim->MVertices[v]) - V3(m->MVertices[v]);
                    }

                    if (shape.Normals is not null && anim->MNormals is not null && m->MNormals is not null)
                    {
                        shape.Normals[baseVertex + v] = V3(anim->MNormals[v]).Normalized - V3(m->MNormals[v]).Normalized;
                    }

                    if (shape.Tangents is not null && anim->MTangents is not null && m->MTangents is not null)
                    {
                        shape.Tangents[baseVertex + v] = V3(anim->MTangents[v]) - V3(m->MTangents[v]);
                    }
                }
            }
        }

        foreach (var shapeName in order)
        {
            var (positions, normals, tangents, weight) = shapes[shapeName];
            result.BlendShapes.Add(new BlendShape(shapeName, positions) { NormalDeltas = normals, TangentDeltas = tangents });
            result.DefaultBlendShapeWeights.Add(weight);
        }
    }

    private static ModelSkin? ConvertSkin(ModelData model, ModelMesh mesh, List<nint> parts, Dictionary<string, int> nodeIndex, ICollection<string> warnings)
    {
        var skin = new ModelSkin(mesh.Name + "_Skin");
        var jointOf = new Dictionary<int, int>();
        var vertexCount = mesh.Mesh.VertexCount;
        var influences = new List<(ushort Joint, float Weight)>[vertexCount];
        var baseVertex = 0;
        foreach (var part in parts)
        {
            var m = (AiMesh*)part;
            for (var b = 0; b < m->MNumBones; b++)
            {
                var bone = m->MBones[b];
                var boneName = bone->MName.AsString;
                if (!nodeIndex.TryGetValue(boneName, out var node))
                {
                    warnings.Add($"Mesh '{mesh.Name}': bone '{boneName}' has no node in the hierarchy; its weights were dropped.");
                    continue;
                }

                if (!jointOf.TryGetValue(node, out var joint))
                {
                    joint = skin.Joints.Count;
                    jointOf[node] = joint;
                    skin.Joints.Add(node);
                    skin.InverseBindMatrices.Add(ToEngine(bone->MOffsetMatrix));
                }

                for (var w = 0; w < bone->MNumWeights; w++)
                {
                    var weight = bone->MWeights[w];
                    var vertex = baseVertex + (int)weight.MVertexId;
                    if (vertex < vertexCount && weight.MWeight > 0f)
                    {
                        (influences[vertex] ??= []).Add(((ushort)joint, weight.MWeight));
                    }
                }
            }

            baseVertex += (int)m->MNumVertices;
        }

        if (skin.Joints.Count == 0)
        {
            return null;
        }

        var joints = new ushort[vertexCount * 4];
        var weights = new float[vertexCount * 4];
        var unweighted = 0;
        for (var v = 0; v < vertexCount; v++)
        {
            var list = influences[v];
            if (list is null || list.Count == 0)
            {
                unweighted++;
                weights[v * 4] = 1f; // follows joint 0 rather than collapsing to the origin
                continue;
            }

            var top = list.OrderByDescending(i => i.Weight).Take(4).ToList();
            var sum = top.Sum(i => i.Weight);
            for (var i = 0; i < top.Count; i++)
            {
                joints[(v * 4) + i] = top[i].Joint;
                weights[(v * 4) + i] = top[i].Weight / sum;
            }
        }

        if (unweighted > 0)
        {
            warnings.Add($"Mesh '{mesh.Name}': {unweighted} vertices have no skin weights; they follow the first joint.");
        }

        mesh.Joints = joints;
        mesh.Weights = weights;
        return skin;
    }

    /// <summary>Blend shape weight keys (sparse per key in Assimp) as dense weight channels, linearly interpolated.</summary>
    private static void AddMorphChannels(Animation* animation, ModelAnimation result, ModelData model, Dictionary<string, (int Node, int[] ShapeOf)> targets, ICollection<string> warnings)
    {
        var ticks = animation->MTicksPerSecond > 0 ? animation->MTicksPerSecond : 25.0;
        for (var c = 0; c < animation->MNumMorphMeshChannels; c++)
        {
            var channel = animation->MMorphMeshChannels[c];
            var targetName = channel->MName.AsString;
            if (!targets.TryGetValue(targetName, out var target) && !targets.TryGetValue(targetName.Split('*')[0], out target))
            {
                warnings.Add($"Animation '{result.Name}': blend shape channel for '{targetName}' targets no mesh; skipped.");
                continue;
            }

            var shapeCount = model.Meshes[model.Nodes[target.Node].Mesh].BlendShapes.Count;
            if (shapeCount == 0 || channel->MNumKeys == 0)
            {
                continue;
            }

            var times = new float[channel->MNumKeys];
            var values = new float[channel->MNumKeys * shapeCount];
            for (var k = 0; k < channel->MNumKeys; k++)
            {
                var key = channel->MKeys[k];
                times[k] = (float)(key.MTime / ticks);
                for (var v = 0; v < key.MNumValuesAndWeights; v++)
                {
                    var index = (int)key.MValues[v];
                    if (index >= 0 && index < target.ShapeOf.Length && target.ShapeOf[index] >= 0)
                    {
                        values[(k * shapeCount) + target.ShapeOf[index]] = (float)key.MWeights[v];
                    }
                }
            }

            result.Channels.Add(new AnimationChannel(target.Node, AnimationPath.Weights, AnimationInterpolation.Linear, times, values));
        }
    }

    private static ModelAnimation ConvertAnimation(Animation* animation, string name, Dictionary<string, int> nodeIndex, ICollection<string> warnings)
    {
        var result = new ModelAnimation(name);
        var ticks = animation->MTicksPerSecond > 0 ? animation->MTicksPerSecond : 25.0;
        for (var c = 0; c < animation->MNumChannels; c++)
        {
            var channel = animation->MChannels[c];
            var nodeName = channel->MNodeName.AsString;
            if (!nodeIndex.TryGetValue(nodeName, out var node))
            {
                warnings.Add($"Animation '{name}': channel for '{nodeName}' targets no node; skipped.");
                continue;
            }

            if (channel->MNumPositionKeys > 0)
            {
                result.Channels.Add(new AnimationChannel(node, AnimationPath.Translation, AnimationInterpolation.Linear,
                    [.. Enumerable.Range(0, (int)channel->MNumPositionKeys).Select(k => (float)(channel->MPositionKeys[k].MTime / ticks))],
                    [.. Enumerable.Range(0, (int)channel->MNumPositionKeys).SelectMany(k => Floats(channel->MPositionKeys[k].MValue))]));
            }

            if (channel->MNumRotationKeys > 0)
            {
                result.Channels.Add(new AnimationChannel(node, AnimationPath.Rotation, AnimationInterpolation.Linear,
                    [.. Enumerable.Range(0, (int)channel->MNumRotationKeys).Select(k => (float)(channel->MRotationKeys[k].MTime / ticks))],
                    [.. Enumerable.Range(0, (int)channel->MNumRotationKeys).SelectMany(k => { var q = channel->MRotationKeys[k].MValue; return new[] { q.X, q.Y, q.Z, q.W }; })]));
            }

            if (channel->MNumScalingKeys > 0)
            {
                result.Channels.Add(new AnimationChannel(node, AnimationPath.Scale, AnimationInterpolation.Linear,
                    [.. Enumerable.Range(0, (int)channel->MNumScalingKeys).Select(k => (float)(channel->MScalingKeys[k].MTime / ticks))],
                    [.. Enumerable.Range(0, (int)channel->MNumScalingKeys).SelectMany(k => Floats(channel->MScalingKeys[k].MValue))]));
            }
        }

        return result;
    }

    private static ModelMaterial ConvertMaterial(Assimp api, AiScene* scene, AiMaterial* source, string name, ModelData model, TextureResolver textures, Dictionary<int, int> embeddedIndex)
    {
        var material = new ModelMaterial(name);
        var pbr = TryFloat(api, source, "$mat.metallicFactor") is not null || TryFloat(api, source, "$mat.roughnessFactor") is not null;
        material.SourceModel = pbr ? "FBX PBR" : "FBX Phong";
        material.BaseColor = TryColor(api, source, "$clr.base") ?? TryColor(api, source, "$clr.diffuse") ?? Color.White;
        material.Metallic = TryFloat(api, source, "$mat.metallicFactor") ?? 0f;
        material.Roughness = TryFloat(api, source, "$mat.roughnessFactor") ?? (TryFloat(api, source, "$mat.shininess") is { } shininess ? ModelProcessing.RoughnessFromShininess(shininess) : 0.5f);
        if (TryColor(api, source, "$clr.emissive") is { } emissive)
        {
            var intensity = TryFloat(api, source, "$mat.emissiveIntensity") ?? 1f;
            material.Emissive = new Color(emissive.R * intensity, emissive.G * intensity, emissive.B * intensity, 1f);
        }

        var opacity = TryFloat(api, source, "$mat.opacity") ?? 1f;
        if (opacity < 1f)
        {
            material.BaseColor = material.BaseColor with { A = material.BaseColor.A * opacity };
            material.AlphaMode = ModelAlphaMode.Blend;
        }

        material.DoubleSided = (TryFloat(api, source, "$mat.twosided") ?? 0f) != 0f;

        // Explicit connections only; filenames are never guessed.
        foreach (var (type, role) in new[]
        {
            (TextureType.BaseColor, TextureRole.BaseColor), (TextureType.Diffuse, TextureRole.BaseColor),
            (TextureType.NormalCamera, TextureRole.Normal), (TextureType.Normals, TextureRole.Normal),
            (TextureType.Metalness, TextureRole.Metallic), (TextureType.DiffuseRoughness, TextureRole.Roughness),
            (TextureType.AmbientOcclusion, TextureRole.Occlusion), (TextureType.Lightmap, TextureRole.Occlusion),
            (TextureType.EmissionColor, TextureRole.Emission), (TextureType.Emissive, TextureRole.Emission),
            (TextureType.Opacity, TextureRole.Opacity), (TextureType.Height, TextureRole.Height),
            (TextureType.Displacement, TextureRole.Height), (TextureType.Specular, TextureRole.Unclassified),
            (TextureType.Shininess, TextureRole.Unclassified), (TextureType.Reflection, TextureRole.Unclassified),
            (TextureType.Unknown, TextureRole.Unclassified),
        })
        {
            if (material.Textures.ContainsKey(role) && role != TextureRole.Unclassified)
            {
                continue;
            }

            if (api.GetMaterialTextureCount(source, type) == 0)
            {
                continue;
            }

            AssimpString path;
            if (api.GetMaterialTexture(source, type, 0, &path, null, null, null, null, null, null) != Return.Success)
            {
                continue;
            }

            var text = path.AsString;
            int index;
            if (text.StartsWith('*') && int.TryParse(text.AsSpan(1), out var embedded) && embedded >= 0 && embedded < scene->MNumTextures)
            {
                index = Embedded(scene, embedded, model, embeddedIndex);
            }
            else if (api.GetEmbeddedTexture(scene, text) is var tex && tex is not null)
            {
                index = Embedded(scene, (int)(tex - scene->MTextures[0]), model, embeddedIndex, tex);
            }
            else
            {
                var resolved = textures.Resolve(text, name, role.ToString());
                index = model.Textures.IndexOf(resolved);
                if (index < 0)
                {
                    index = model.Textures.Count;
                    model.Textures.Add(resolved);
                }
            }

            if (role == TextureRole.Unclassified && material.Textures.ContainsKey(TextureRole.Unclassified))
            {
                continue;
            }

            material.Textures[role] = index;
        }

        if (material.Textures.ContainsKey(TextureRole.Opacity) && material.AlphaMode == ModelAlphaMode.Opaque)
        {
            material.AlphaMode = ModelAlphaMode.Mask;
        }

        return material;
    }

    private static int Embedded(AiScene* scene, int index, ModelData model, Dictionary<int, int> embeddedIndex, Texture* known = null)
    {
        if (embeddedIndex.TryGetValue(index, out var existing))
        {
            return existing;
        }

        var texture = known is not null ? known : scene->MTextures[index];
        byte[] bytes;
        if (texture->MHeight == 0)
        {
            bytes = new ReadOnlySpan<byte>(texture->PcData, (int)texture->MWidth).ToArray(); // compressed (PNG, JPEG, ...)
        }
        else
        {
            bytes = EncodeTga((int)texture->MWidth, (int)texture->MHeight, new ReadOnlySpan<byte>(texture->PcData, (int)(texture->MWidth * texture->MHeight * 4)));
        }

        var name = texture->MFilename.AsString;
        var result = new ModelTexture(string.IsNullOrWhiteSpace(name) ? $"Embedded{index}" : Path.GetFileNameWithoutExtension(name)) { Embedded = bytes };
        embeddedIndex[index] = model.Textures.Count;
        model.Textures.Add(result);
        return model.Textures.Count - 1;
    }

    /// <summary>Uncompressed BGRA texels (Assimp's aiTexel) as a 32-bit TGA the texture decoder reads.</summary>
    private static byte[] EncodeTga(int width, int height, ReadOnlySpan<byte> bgra)
    {
        var header = new byte[18];
        header[2] = 2; // uncompressed true color
        header[12] = (byte)width;
        header[13] = (byte)(width >> 8);
        header[14] = (byte)height;
        header[15] = (byte)(height >> 8);
        header[16] = 32;
        header[17] = 0x28; // top-left origin, 8 alpha bits
        return [.. header, .. bgra];
    }

    private static string? MaterialName(Assimp api, AiMaterial* material)
    {
        AssimpString name;
        return api.GetMaterialString(material, "?mat.name", 0, 0, &name) == Return.Success ? name.AsString : null;
    }

    private static float? TryFloat(Assimp api, AiMaterial* material, string key)
    {
        float value;
        uint count = 1;
        return api.GetMaterialFloatArray(material, key, 0, 0, &value, &count) == Return.Success && count > 0 ? value : null;
    }

    private static Color? TryColor(Assimp api, AiMaterial* material, string key)
    {
        System.Numerics.Vector4 color;
        return api.GetMaterialColor(material, key, 0, 0, &color) == Return.Success ? new Color(color.X, color.Y, color.Z, color.W) : null;
    }

    private static void Decompose(Assimp api, NumericsMatrix matrix, out Vector3 position, out Quaternion rotation, out Vector3 scale)
    {
        System.Numerics.Vector3 s, p;
        AssimpQuaternion q;
        api.DecomposeMatrix(&matrix, &s, &q, &p);
        (position, rotation, scale) = (new Vector3(p.X, p.Y, p.Z), new Quaternion(q.X, q.Y, q.Z, q.W), new Vector3(s.X, s.Y, s.Z));
    }

    /// <summary>Assimp matrices are row-major with column vectors (translation in a4, b4, c4): the engine's convention.</summary>
    private static Matrix4x4 ToEngine(NumericsMatrix m) => new()
    {
        M00 = m.M11, M01 = m.M12, M02 = m.M13, M03 = m.M14,
        M10 = m.M21, M11 = m.M22, M12 = m.M23, M13 = m.M24,
        M20 = m.M31, M21 = m.M32, M22 = m.M33, M23 = m.M34,
        M30 = m.M41, M31 = m.M42, M32 = m.M43, M33 = m.M44,
    };

    private static Vector3 V3(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    private static float[] Floats(System.Numerics.Vector3 v) => [v.X, v.Y, v.Z];
}
