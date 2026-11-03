namespace MNVTEngine.ECS;

/// <summary>
/// Handle to an entity in a <see cref="World"/>. The generation makes handles to destroyed entities
/// detectably stale even after their index is reused. <c>default</c> is the null entity.
/// </summary>
public readonly record struct Entity(int Index, int Generation)
{
    public static Entity Null => default;

    /// <summary>True for <see cref="Null"/>. Generations start at 1, so live handles are never null.</summary>
    public bool IsNull => Generation == 0;

    public override string ToString() => IsNull ? "Entity(null)" : $"Entity({Index}v{Generation})";
}
