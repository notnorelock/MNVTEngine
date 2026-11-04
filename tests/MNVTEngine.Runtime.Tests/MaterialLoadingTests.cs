using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Assets;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;

namespace MNVTEngine.Runtime.Tests;

/// <summary>Material assets on a real device: shaders, render settings, textures, defaults and reloading.</summary>
public sealed class MaterialLoadingTests : IDisposable
{
    private static readonly Guid Texture = Guid.Parse("e5000000-0000-4000-8000-000000000001");
    private static readonly Guid Glass = Guid.Parse("e5000000-0000-4000-8000-000000000002");
    private static readonly Guid Cutout = Guid.Parse("e5000000-0000-4000-8000-000000000003");
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-materials-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Assets => Path.Combine(_root, "Assets");

    private void Write(string path, string text, Guid guid)
    {
        var file = Path.Combine(Assets, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        File.WriteAllText(file + ".meta", $$"""{ "schemaVersion": 1, "guid": "{{guid}}", "settings": {} }""");
    }

    private void Png(string path, byte r, byte g, byte b)
    {
        var file = Path.Combine(Assets, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var stream = File.Create(file);
        new StbImageWriteSharp.ImageWriter().WritePng(new byte[] { r, g, b, 255 }, 1, 1, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
    }

    [Fact]
    public void StandardMaterials_GetRenderSettingsTexturesAndDefaults_AndReloadWhenATextureChanges()
    {
        Png("Textures/base.png", 200, 100, 50);
        File.WriteAllText(Path.Combine(Assets, "Textures", "base.png.meta"), $$"""{ "schemaVersion": 1, "guid": "{{Texture}}", "settings": {} }""");
        Write("Materials/Glass.mnvtmat", $$"""{ "schemaVersion": 2, "shader": "builtin:standard", "blend": "Alpha", "properties": { "_BaseColor": [1, 1, 1, 0.5], "_BaseColorMap": "asset:{{Texture}}", "_Roughness": 0.1 } }""", Glass);
        Write("Materials/Leaves.mnvtmat", """{ "schemaVersion": 2, "shader": "builtin:standard", "cull": "None", "properties": { "_AlphaCutoff": 0.5 } }""", Cutout);

        VulkanGraphicsDevice device;
        try
        {
            device = VulkanGraphicsDevice.CreateHeadless(16, 16, NullLogger.Instance);
        }
        catch (GraphicsInitializationException ex)
        {
            Assert.Skip($"No Vulkan 1.4 device available: {ex.Message}");
            throw;
        }

        using (device)
        {
            var assets = AssetDatabase.OpenForEditing(Assets, Path.Combine(_root, "Library"));
            using var resources = new ResourceCache(device, assets, NullLogger.Instance);
            var glass = resources.GetMaterial(new AssetRef(Glass))!;
            Assert.Equal((BlendMode.Alpha, Material.TransparentQueue, false, true, CullMode.Back), (glass.BlendMode, glass.RenderQueue, glass.DepthWrite, glass.DepthTest, glass.Cull));
            Assert.Same(resources.GetTexture(new AssetRef(Texture)), glass.GetTexture("_BaseColorMap"));
            Assert.Same(resources.GetDefaultTexture(Graphics.Materials.DefaultTexture.FlatNormal), glass.GetTexture("_NormalMap"));
            Assert.True(glass.TryGetUniform("_Roughness", out var roughness) && roughness.Vector.X == 0.1f);
            Assert.True(glass.TryGetUniform("_Metallic", out var metallic) && metallic.Vector.X == 0f); // documented default
            Assert.True(glass.TryGetUniform("_OcclusionStrength", out var occlusion) && occlusion.Vector.X == 1f);

            var leaves = resources.GetMaterial(new AssetRef(Cutout))!;
            Assert.Equal((BlendMode.Opaque, Material.AlphaTestQueue, true, CullMode.None), (leaves.BlendMode, leaves.RenderQueue, leaves.DepthWrite, leaves.Cull));
            Assert.Same(resources.GetDefaultTexture(Graphics.Materials.DefaultTexture.White), leaves.GetTexture("_BaseColorMap"));

            Png("Textures/base.png", 10, 20, 30); // the texture changes: the material must not keep the released one
            File.SetLastWriteTimeUtc(Path.Combine(Assets, "Textures", "base.png"), DateTime.UtcNow.AddSeconds(5));
            assets.Refresh();
            var reloaded = resources.GetMaterial(new AssetRef(Glass))!;
            Assert.NotSame(glass, reloaded);
            Assert.False(reloaded.GetTexture("_BaseColorMap")!.IsDisposed);
            Assert.Same(leaves, resources.GetMaterial(new AssetRef(Cutout))); // unrelated materials stay
        }
    }
}
