using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Foliage;
using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshes;

namespace MNVTEngine.Terrain.Tests;

/// <summary>Ported from TerrainHeaven's FoliageData.test.ts and MeshData.test.ts.</summary>
public sealed class FoliageMeshStoreTests
{
    private static FoliageInstanceData Foliage(Vector3 p, double scaleT = 0, double colorT = 0, int typeIndex = 0, double rotationY = 0) =>
        new() { Position = PointData.From(p), RotationY = rotationY, ScaleT = scaleT, ColorT = colorT, TypeIndex = typeIndex };

    private static MeshInstanceData Prop(Vector3 p, double rx = 0, double ry = 0, double rz = 0, double scale = 1) =>
        new() { Position = PointData.From(p), RotationX = rx, RotationY = ry, RotationZ = rz, Scale = scale };

    [Fact]
    public void Foliage_StartsEmptyAndKeepsTheUnityInstanceContract()
    {
        var store = new FoliageStore();
        Assert.Empty(store.Types);
        var typeIndex = store.AddType(FoliageTypeData.CreateDefault("Custom Flowers"));
        store.AddInstance(typeIndex, Foliage(new Vector3(1, 2, 3), 0.25, 0.75, typeIndex, 123));
        var data = store.Serialize();
        Assert.Equal(["Custom Flowers"], data.Types.Select(t => t.DisplayName));
        var instance = data.Layers[0].Instances[0];
        Assert.Equal((new Vector3(1, 2, 3), 123.0, 0.25, 0.75, 0), (instance.Position.ToVector3(), instance.RotationY, instance.ScaleT, instance.ColorT, instance.TypeIndex));
    }

    [Fact]
    public void Foliage_RoundTripsLayersAndErasesInASphericalBrush()
    {
        var store = new FoliageStore([FoliageTypeData.CreateDefault("Grass")]);
        store.AddInstance(0, Foliage(Vector3.Zero));
        store.AddInstance(0, Foliage(new Vector3(4, 0, 0), 1, 1));
        var restored = new FoliageStore();
        restored.Load(store.Serialize());
        Assert.True(restored.IsTooClose(0, new Vector3(0.1, 0, 0), 0.3));
        Assert.Equal(1, restored.RemoveInstancesNear(Vector3.Zero, 1));
        Assert.Single(restored.GetInstances(0));
    }

    [Fact]
    public void Foliage_EditsDuplicatesAndRemovesPresetsKeepingTypeIndicesValid()
    {
        var store = new FoliageStore();
        store.AddType(FoliageTypeData.CreateDefault("Grass"));
        var bush = store.AddType(FoliageTypeData.CreateDefault("Bush"));
        store.UpdateType(bush, t =>
        {
            t.MaxSize = 3;
            t.CastShadows = true;
        });
        store.AddInstance(bush, Foliage(Vector3.Zero, typeIndex: bush));
        Assert.Equal(2, store.DuplicateType(0));
        Assert.True(store.RemoveType(0));
        Assert.Equal(["Bush", "Grass Copy"], store.Types.Select(t => t.DisplayName));
        Assert.Equal(3, store.Types[0].MaxSize);
        Assert.Equal(0, store.GetInstances(0)[0].TypeIndex);
    }

    [Fact]
    public void Foliage_StoresNormalizedFactorsAndResolvesSizeFromTheCurrentPreset()
    {
        var preset = FoliageTypeData.CreateDefault("Tree");
        preset.MinSize = 2;
        preset.MaxSize = 4;
        preset.LengthFactor = 0.5;
        var store = new FoliageStore([preset]);
        store.AddInstance(0, Foliage(Vector3.Zero, 3.5, -2, 99));
        var instance = store.GetInstances(0)[0];
        Assert.Equal((1.0, 0.0, 0), (instance.ScaleT, instance.ColorT, instance.TypeIndex));
        Assert.Equal((2.0, 4.0), store.Types[0].ResolveDimensions(instance.ScaleT));

        store.UpdateType(0, t =>
        {
            t.MinSize = 6;
            t.MaxSize = 10;
            t.LengthFactor = 0.25;
        });
        Assert.Equal((2.5, 10.0), store.Types[0].ResolveDimensions(instance.ScaleT));
    }

    [Fact]
    public void Meshes_StartEmptyAndKeepTheUnityInstanceContract()
    {
        var store = new MeshStore();
        Assert.Empty(store.Assets);
        var assetIndex = store.AddAsset(MeshAssetData.Create("Rock", "rock.obj"));
        store.AddInstance(assetIndex, Prop(new Vector3(1, 2, 3), 5, 123, -5, 1.5));
        var data = store.Serialize();
        Assert.Equal(("Rock", "rock.obj", 1.0, Vector3.Zero), (data.Assets[0].DisplayName, data.Assets[0].LibraryPath, data.Assets[0].BaseScale, data.Assets[0].PositionOffset.ToVector3()));
        Assert.Matches("^[0-9a-z]+-[0-9a-z]{7}$", data.Assets[0].Id);
        var i = data.Layers[0].Instances[0];
        Assert.Equal((new Vector3(1, 2, 3), 5.0, 123.0, -5.0, 1.5, 0), (i.Position.ToVector3(), i.RotationX, i.RotationY, i.RotationZ, i.Scale, i.AssetIndex));
    }

    [Fact]
    public void Meshes_RoundTripLayersAndEraseInASphericalBrush()
    {
        var store = new MeshStore([MeshAssetData.Create("Crate", "crate.glb")]);
        store.AddInstance(0, Prop(Vector3.Zero));
        store.AddInstance(0, Prop(new Vector3(4, 0, 0)));
        var restored = new MeshStore();
        restored.Load(store.Serialize());
        Assert.True(restored.IsTooClose(0, new Vector3(0.1, 0, 0), 0.3));
        Assert.Equal(1, restored.RemoveInstancesNear(Vector3.Zero, 1));
        Assert.Single(restored.GetInstances(0));
    }

    [Fact]
    public void Meshes_RenameAndRemoveAssetsKeepingAssetIndicesValid()
    {
        var store = new MeshStore();
        store.AddAsset(MeshAssetData.Create("Tree", "tree.fbx"));
        var crate = store.AddAsset(MeshAssetData.Create("Crate", "crate.glb"));
        store.RenameAsset(crate, "Wooden Crate");
        store.AddInstance(crate, Prop(Vector3.Zero));
        Assert.True(store.RemoveAsset(0));
        Assert.Equal(["Wooden Crate"], store.Assets.Select(a => a.DisplayName));
        Assert.Equal(0, store.GetInstances(0)[0].AssetIndex);
    }

    [Fact]
    public void Meshes_CalibrateScaleAndPivotAndRoundTripThem()
    {
        var store = new MeshStore([MeshAssetData.Create("Barrel", "barrel.fbx")]);
        store.UpdateAsset(0, 0.01, new Vector3(0, 1.2, 0));
        store.UpdateAsset(0, -5);
        Assert.Equal(0.01, store.Assets[0].BaseScale);
        var restored = new MeshStore();
        restored.Load(store.Serialize());
        Assert.Equal((0.01, new Vector3(0, 1.2, 0)), (restored.Assets[0].BaseScale, restored.Assets[0].PositionOffset.ToVector3()));
    }

    [Fact]
    public void Meshes_BackfillCalibrationDefaultsForOlderFiles()
    {
        var restored = new MeshStore();
        restored.Load(new MeshProjectData { Assets = [new MeshAssetData { Id = "a", DisplayName = "Old Prop", LibraryPath = "old.obj", BaseScale = 0, PositionOffset = null! }], Layers = [new()] });
        Assert.Equal((1.0, Vector3.Zero), (restored.Assets[0].BaseScale, restored.Assets[0].PositionOffset.ToVector3()));
    }
}
