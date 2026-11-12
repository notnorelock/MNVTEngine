namespace MNVTEngine.Runtime;

/// <summary>A module's own cache in a <see cref="ResourceCache"/> (world.3d: models and environments).</summary>
public interface IResourceExtension : IDisposable
{
    /// <summary>Drops what was loaded from <paramref name="changed"/> assets; they load again on next use.</summary>
    void Evict(IReadOnlyCollection<Guid> changed);
}
