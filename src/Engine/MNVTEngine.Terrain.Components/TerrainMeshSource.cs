using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Materials;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Graphics.Rendering3D;
using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Foliage;
using MNVTEngine.Terrain.Meshes;
using MNVTEngine.Terrain.Project;

namespace MNVTEngine.Runtime;

/// <summary>
/// The terrain part of the 3D pass: every active <see cref="WorldTerrain"/> as meshes in its GameObject's space. Each
/// element group is one mesh with the Standard shader (its texture, texture rotation, material hint and auto-smooth
/// normals); terrain tiles draw with the four-layer splat shader. Foliage presets are crossed quads and
/// placed props are their model meshes, both instanced. Rebuilt when the component's version changes.
/// </summary>
public sealed partial class TerrainMeshSource(RenderServices services) : IMeshItemSource
{
    private readonly Renderer2D renderer = services.Renderer;
    private readonly ResourceCache resources = services.Resources;
    private GpuMeshCache? _meshes;
    private readonly Dictionary<WorldTerrain, TerrainDraw> _terrains = new(ReferenceEqualityComparer.Instance);
    private Mesh? _foliageQuads;
    private Material? _propFallback;
    private bool _terrainEvents;

    public void AddItems(MeshItemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scene = context.Scene;
        _meshes = context.Meshes;
        foreach (var terrain in scene.World.Pool<WorldTerrain>().Components)
        {
            if (!terrain.IsActiveAndEnabled || context.IsHidden(terrain.GameObject) || terrain.Data is not { } data)
            {
                continue;
            }

            if (!_terrainEvents)
            {
                // Reimported textures or models replace what the built materials hold: rebuild every terrain.
                resources.TexturesEvicted += _ => InvalidateTerrains();
                resources.Models.MeshesEvicted += _ => InvalidateTerrains();
                _terrainEvents = true;
            }

            if (!_terrains.TryGetValue(terrain, out var draw))
            {
                _terrains[terrain] = draw = new TerrainDraw();
            }

            if (draw.Version != terrain.Version)
            {
                Rebuild(draw, terrain, data);
            }

            draw.LastFrame = context.Frame;
            var world = terrain.Transform.LocalToWorldMatrix;
            var white = resources.GetDefaultTexture(DefaultTexture.White);
            foreach (var surface in draw.Surfaces)
            {
                context.Items.Add(new MeshDrawItem(context.Meshes.Get(surface.Mesh), surface.SubMesh, surface.Material, white, world * surface.Local, surface.Cull));
            }

            foreach (var instance in draw.Instances)
            {
                if ((instance.Foliage && terrain.DrawFoliage) || (!instance.Foliage && terrain.DrawProps))
                {
                    context.Items.Add(new MeshDrawItem(context.Meshes.Get(instance.Mesh), instance.SubMesh, instance.Material, white, world * instance.Local, instance.Cull));
                }
            }
        }

        // Release terrains that stopped drawing (destroyed or disabled); no allocation while every terrain draws.
        foreach (var (component, draw) in _terrains)
        {
            if (draw.LastFrame != context.Frame)
            {
                ReleaseTerrain(draw);
                _terrains.Remove(component);
            }
        }
    }

    private void InvalidateTerrains()
    {
        foreach (var draw in _terrains.Values)
        {
            draw.Version = -1;
        }
    }

    private void Rebuild(TerrainDraw draw, WorldTerrain terrain, TerrainProject data)
    {
        ReleaseTerrain(draw);
        draw.Version = terrain.Version;
        foreach (var element in data.World.Elements)
        {
            AddElement(draw, terrain, element);
        }

        AddFoliage(draw, terrain, data.Foliage);
        AddProps(draw, terrain, data.Meshes);
    }

    private void AddElement(TerrainDraw draw, WorldTerrain terrain, WorldElement element)
    {
        var groups = element.Geometry.Where(g => g.Triangles.Count > 0).ToList();
        if (groups.Count == 0)
        {
            return;
        }

        var normals = Terrain.Meshing.SmoothNormals.Compute(groups);
        var splatBounds = element is Terrain.Elements.Terrain tile ? (MinX: tile.Center.X - (tile.Width / 2), MinZ: tile.Center.Z - (tile.Length / 2), tile.Width, tile.Length) : default;
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var count = group.Triangles.Count * 3;
            var positions = new Vector3[count];
            var vertexNormals = new Vector3[count];
            var uv0 = new Vector2[count];
            var uv1 = new Vector2[count];
            var rotation = element.TextureRotations.GetValueOrDefault(group.Name);
            var (cos, sin) = (Math.Cos(rotation * Math.PI / 180), Math.Sin(rotation * Math.PI / 180));
            var i = 0;
            foreach (var t in group.Triangles)
            {
                foreach (var (p, uv) in ((Vector3, Vector2)[])[(t.A, t.UvA), (t.B, t.UvB), (t.C, t.UvC)])
                {
                    positions[i] = (p);
                    vertexNormals[i] = normals[g][i];

                    // three.js texture.rotation about the texture center (0.5, 0.5); then V flipped, since three.js
                    // images start at the bottom row and engine textures at the top.
                    uv0[i] = new Vector2(
                        (float)((cos * uv.X) + (sin * uv.Y) - ((cos * 0.5) + (sin * 0.5)) + 0.5),
                        (float)(1 - ((-sin * uv.X) + (cos * uv.Y) - ((-sin * 0.5) + (cos * 0.5)) + 0.5)));
                    if (splatBounds.Width > 0)
                    {
                        uv1[i] = new Vector2((float)((p.X - splatBounds.MinX) / splatBounds.Width), (float)((p.Z - splatBounds.MinZ) / splatBounds.Length));
                    }

                    i++;
                }
            }

            var mesh = new Mesh($"{element.GetType().Name}.{group.Name}") { Positions = positions, Normals = vertexNormals, UV0 = uv0, UV1 = uv1 };
            mesh.SetIndices([.. Enumerable.Range(0, count).Select(v => (uint)v)], new SubMesh(0, count));
            mesh.RecalculateBounds();
            var hint = element.GetMaterialHint(group.Name);
            var material = element is Terrain.Elements.Terrain splatTile && group.Name == "terrain"
                ? SplatMaterial(draw, terrain, splatTile)
                : SurfaceMaterial(terrain, element.GetGroupTextureReferences().GetValueOrDefault(group.Name), hint);
            CullMode? cull = element.DoubleSided || hint?.DoubleSided == true ? CullMode.None : null;
            draw.Surfaces.Add(new TerrainSurface(mesh, 0, material, cull, Matrix4x4.Identity));
            draw.OwnedMeshes.Add(mesh);
        }
    }

    private Material SurfaceMaterial(WorldTerrain terrain, string? texture, MaterialHint? hint)
    {
        var material = new Material(resources.GetBuiltinShader(Serialization.MaterialDefinition.BuiltinStandard), BlendMode.Opaque, "Terrain surface");
        MaterialProperties.ApplyDefaults(material, MaterialShaders.StandardProperties, resources.GetDefaultTexture);
        var color = hint?.Color is { } rgb ? new Color(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f) : Color.White;
        var opacity = (float)(hint?.Opacity ?? 1);
        color.A = opacity;
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Roughness", (float)(hint?.Roughness ?? 1));
        material.SetFloat("_Metallic", (float)(hint?.Metalness ?? 0));
        if (TerrainTexture(terrain, texture) is { } map)
        {
            material.SetTexture("_BaseColorMap", map);
        }

        if (opacity < 1 || hint?.DepthWrite == false)
        {
            material.BlendMode = BlendMode.Alpha;
            material.RenderQueue = Material.TransparentQueue;
            material.DepthWrite = false;
        }

        return material;
    }

    private Material SplatMaterial(TerrainDraw draw, WorldTerrain terrain, Terrain.Elements.Terrain tile)
    {
        var material = new Material(resources.GetBuiltinShader(MaterialShaders.TerrainSplatId), BlendMode.Opaque, "Terrain splat");
        MaterialProperties.ApplyDefaults(material, MaterialShaders.StandardProperties, resources.GetDefaultTexture);
        material.SetFloat("_Roughness", 1f);
        var paint = tile.TexturePaint;
        var control = renderer.Device.CreateTexture(paint.Resolution, paint.Resolution, paint.Data, SamplerState.LinearClamp, "Terrain control map");
        draw.OwnedTextures.Add(control);
        material.SetTexture("_ControlMap", control);
        var fallback = resources.GetDefaultTexture(DefaultTexture.White);
        var tiling = new float[4];
        for (var layer = 0; layer < 4; layer++)
        {
            material.SetTexture($"_Layer{layer}", TerrainTexture(terrain, tile.EffectiveLayerTexture(layer)) ?? fallback);
            tiling[layer] = (float)tile.TextureLayers[layer].Tiling;
        }

        material.SetVector("_LayerTiling", new Vector4(tiling[0], tiling[1], tiling[2], tiling[3]));
        return material;
    }

    private void AddFoliage(TerrainDraw draw, WorldTerrain terrain, FoliageStore foliage)
    {
        _foliageQuads ??= CrossedQuads();
        for (var typeIndex = 0; typeIndex < foliage.Types.Count; typeIndex++)
        {
            var type = foliage.Types[typeIndex];
            var instances = foliage.GetInstances(typeIndex);
            if (instances.Count == 0)
            {
                continue;
            }

            // Unlit, like TerrainHeaven's foliage: the texture with an alpha cutoff, or the preset's mid tint without one.
            var material = new Material(resources.GetBuiltinShader(Serialization.MaterialDefinition.BuiltinUnlit), BlendMode.Opaque, $"Foliage {type.DisplayName}");
            MaterialProperties.ApplyDefaults(material, MaterialShaders.UnlitProperties, resources.GetDefaultTexture);
            if (TerrainTexture(terrain, type.TexturePath) is { } map)
            {
                material.SetTexture("_BaseColorMap", map);
                material.SetColor("_BaseColor", Color.White);
                material.SetFloat("_AlphaCutoff", (float)type.AlphaCutoff);
            }
            else
            {
                var (a, b) = (type.ColorA, type.ColorB);
                material.SetColor("_BaseColor", new Color((float)((a.R + b.R) / 2), (float)((a.G + b.G) / 2), (float)((a.B + b.B) / 2)));
            }

            foreach (var instance in instances)
            {
                var (width, height) = type.ResolveDimensions(instance.ScaleT);
                var local = Matrix4x4.TRS((instance.Position.ToVector3()), Quaternion.AngleAxis((float)instance.RotationY, Vector3.Up), new Vector3((float)width, (float)height, (float)width));
                draw.Instances.Add(new TerrainInstance(_foliageQuads, 0, material, CullMode.None, local, Foliage: true));
            }
        }
    }

    private void AddProps(TerrainDraw draw, WorldTerrain terrain, MeshStore props)
    {
        for (var assetIndex = 0; assetIndex < props.Assets.Count; assetIndex++)
        {
            var asset = props.Assets[assetIndex];
            var instances = props.GetInstances(assetIndex);
            var reference = instances.Count == 0 ? AssetRef.None : resources.ResolveTerrainReference(terrain.Project, asset.LibraryPath);
            if (reference.IsNone)
            {
                continue;
            }

            var parts = PropParts(reference);
            var calibration = Matrix4x4.Translate((asset.PositionOffset.ToVector3())) * Matrix4x4.Scale(Vector3.One * (float)(asset.BaseScale > 0 ? asset.BaseScale : 1));
            foreach (var instance in instances)
            {
                // three.js Euler XYZ: X applied last.
                var rotation = Quaternion.AngleAxis((float)instance.RotationX, Vector3.Right) * Quaternion.AngleAxis((float)instance.RotationY, Vector3.Up) * Quaternion.AngleAxis((float)instance.RotationZ, new Vector3(0f, 0f, 1f));
                var placement = Matrix4x4.TRS((instance.Position.ToVector3()), rotation, Vector3.One * (float)instance.Scale) * calibration;
                foreach (var (mesh, local, materials) in parts)
                {
                    for (var sub = 0; sub < mesh.SubMeshes.Count; sub++)
                    {
                        draw.Instances.Add(new TerrainInstance(mesh, sub, materials[sub], null, placement * local, Foliage: false));
                    }
                }
            }
        }
    }

    /// <summary>The meshes a prop reference draws: one model mesh, or every mesh of a whole model at its node transform.</summary>
    private List<(Mesh Mesh, Matrix4x4 Local, Material[] Materials)> PropParts(AssetRef reference)
    {
        Material[] MaterialsOf(Assets.Models.ModelMesh mesh) => [.. Enumerable.Range(0, mesh.Mesh.SubMeshes.Count).Select(i =>
            resources.GetModelMaterial(reference.Guid, i < mesh.SubMeshMaterials.Count ? mesh.SubMeshMaterials[i] : -1)
            ?? PropFallbackMaterial())];

        if (reference.SubAsset is not null || resources.GetModel(reference.Guid) is not { Meshes.Count: > 1 } model)
        {
            return resources.GetModelMesh(reference) is { } single ? [(single.Mesh, Matrix4x4.Identity, MaterialsOf(single))] : [];
        }

        var parts = new List<(Mesh, Matrix4x4, Material[])>();
        for (var n = 0; n < model.Nodes.Count; n++)
        {
            if (model.Nodes[n].Mesh is var meshIndex && meshIndex >= 0 && meshIndex < model.Meshes.Count)
            {
                var local = Matrix4x4.Identity;
                for (var node = n; node >= 0; node = model.Nodes[node].Parent)
                {
                    local = Matrix4x4.TRS(model.Nodes[node].Position, model.Nodes[node].Rotation, model.Nodes[node].Scale) * local;
                }

                parts.Add((model.Meshes[meshIndex].Mesh, local, MaterialsOf(model.Meshes[meshIndex])));
            }
        }

        return parts;
    }

    private Material PropFallbackMaterial()
    {
        if (_propFallback is null)
        {
            _propFallback = new Material(resources.GetBuiltinShader(Serialization.MaterialDefinition.BuiltinStandard), BlendMode.Opaque, "Prop");
            MaterialProperties.ApplyDefaults(_propFallback, MaterialShaders.StandardProperties, resources.GetDefaultTexture);
        }

        return _propFallback;
    }

    private Texture2D? TerrainTexture(WorldTerrain terrain, string? reference) =>
        string.IsNullOrEmpty(reference) || resources.ResolveTerrainReference(terrain.Project, reference) is not { IsNone: false } asset ? null : resources.GetTexture(asset);

    /// <summary>Two unit quads crossed at right angles, 1 wide and 1 tall from the origin up (TerrainHeaven's foliage card; V flipped as for elements).</summary>
    private static Mesh CrossedQuads()
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<uint>();
        foreach (var angle in (float[])[0f, MathF.PI / 2])
        {
            var (cos, sin) = (MathF.Cos(angle), MathF.Sin(angle));
            var start = (uint)positions.Count;
            foreach (var (x, y) in ((float, float)[])[(-0.5f, 0f), (0.5f, 0f), (-0.5f, 1f), (0.5f, 1f)])
            {
                positions.Add(new Vector3(x * cos, y, -x * sin));
                normals.Add(new Vector3(sin, 0f, cos));
            }

            uvs.AddRange([new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(1f, 0f)]);
            indices.AddRange([start, start + 1, start + 2, start + 2, start + 1, start + 3]);
        }

        var mesh = new Mesh("Foliage card") { Positions = [.. positions], Normals = [.. normals], UV0 = [.. uvs] };
        mesh.SetIndices([.. indices], new SubMesh(0, indices.Count));
        mesh.RecalculateBounds();
        return mesh;
    }

    private void ReleaseTerrain(TerrainDraw draw)
    {
        foreach (var mesh in draw.OwnedMeshes)
        {
            _meshes?.Remove(mesh);
        }

        foreach (var texture in draw.OwnedTextures)
        {
            texture.Dispose();
        }

        draw.OwnedMeshes.Clear();
        draw.OwnedTextures.Clear();
        draw.Surfaces.Clear();
        draw.Instances.Clear();
    }

    public void Dispose() => DisposeTerrains();

    private void DisposeTerrains()
    {
        foreach (var draw in _terrains.Values)
        {
            ReleaseTerrain(draw);
        }

        _terrains.Clear();
        if (_foliageQuads is not null)
        {
            _meshes?.Remove(_foliageQuads);
            _foliageQuads = null;
        }
    }

    private sealed record TerrainSurface(Mesh Mesh, int SubMesh, Material Material, CullMode? Cull, Matrix4x4 Local);

    private sealed record TerrainInstance(Mesh Mesh, int SubMesh, Material Material, CullMode? Cull, Matrix4x4 Local, bool Foliage);

    private sealed class TerrainDraw
    {
        public int Version { get; set; } = -1;

        public int LastFrame { get; set; }

        public List<TerrainSurface> Surfaces { get; } = [];

        public List<TerrainInstance> Instances { get; } = [];

        public List<Mesh> OwnedMeshes { get; } = [];

        public List<Texture2D> OwnedTextures { get; } = [];
    }
}
