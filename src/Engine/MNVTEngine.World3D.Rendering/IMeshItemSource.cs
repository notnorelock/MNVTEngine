using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Graphics.Rendering3D;

namespace MNVTEngine.Runtime;

/// <summary>What a mesh source adds to this frame of the 3D pass.</summary>
/// <param name="Frame">Increments once per 3D view; sources release what was not drawn in the latest frame.</param>
/// <param name="Meshes">GPU copies of CPU meshes, shared with the pass.</param>
/// <param name="Items">This frame's draw list; sources append to it.</param>
/// <param name="IsHidden">Hidden in the Scene view.</param>
public sealed record MeshItemContext(Scene Scene, int Frame, GpuMeshCache Meshes, List<MeshDrawItem> Items, Func<GameObject, bool> IsHidden);

/// <summary>A module that draws meshes of its own components in the 3D pass (terrain), sharing its lights and shadows.</summary>
public interface IMeshItemSource : IDisposable
{
    void AddItems(MeshItemContext context);
}

/// <summary>A module that contributes an <see cref="IMeshItemSource"/> to the 3D pass of every renderer.</summary>
public interface IMeshItemIntegration
{
    IMeshItemSource CreateItemSource(RenderServices services);
}
