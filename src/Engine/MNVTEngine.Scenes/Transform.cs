using MNVTEngine.Mathematics;

namespace MNVTEngine;

/// <summary>
/// 2D position, rotation (degrees, counter-clockwise) and scale, relative to the parent Transform.
/// World values compose through the parent chain. With non-uniform parent scale and rotation combined,
/// <see cref="LocalToWorldMatrix"/> is exact while <see cref="Rotation"/> and <see cref="LossyScale"/> are approximations.
/// </summary>
[ComponentId("MNVTEngine.Transform")]
public sealed class Transform : Component
{
    private readonly List<Transform> _children = [];
    private Vector2 _localPosition;
    private float _localRotation;
    private Vector2 _localScale = Vector2.One;
    private Matrix4x4 _localToWorld = Matrix4x4.Identity;
    private bool _dirty = true;

    public Transform? Parent { get; private set; }

    public IReadOnlyList<Transform> Children => _children;

    public int ChildCount => _children.Count;

    public Vector2 LocalPosition
    {
        get => _localPosition;
        set
        {
            _localPosition = value;
            MarkDirty();
        }
    }

    public float LocalRotation
    {
        get => _localRotation;
        set
        {
            _localRotation = value;
            MarkDirty();
        }
    }

    public Vector2 LocalScale
    {
        get => _localScale;
        set
        {
            _localScale = value;
            MarkDirty();
        }
    }

    public Vector2 Position
    {
        get => (Vector2)LocalToWorldMatrix.Translation;
        set => LocalPosition = Parent is null ? value : (Vector2)Parent.WorldToLocalMatrix.MultiplyPoint3x4(new Vector3(value, 0f));
    }

    public float Rotation
    {
        get => (Parent?.Rotation ?? 0f) + _localRotation;
        set => LocalRotation = value - (Parent?.Rotation ?? 0f);
    }

    public Vector2 LossyScale => Parent is null ? _localScale : Vector2.Scale(Parent.LossyScale, _localScale);

    /// <summary>Unit vector of the local +X axis in world space.</summary>
    public Vector2 Right => Vector2.Rotate(Vector2.Right, Rotation);

    /// <summary>Unit vector of the local +Y axis in world space.</summary>
    public Vector2 Up => Vector2.Rotate(Vector2.Up, Rotation);

    public Matrix4x4 LocalToWorldMatrix
    {
        get
        {
            if (_dirty)
            {
                var local = Matrix4x4.TRS(new Vector3(_localPosition, 0f), Quaternion.Euler(0f, 0f, _localRotation), new Vector3(_localScale, 1f));
                _localToWorld = Parent is null ? local : Parent.LocalToWorldMatrix * local;
                _dirty = false;
            }

            return _localToWorld;
        }
    }

    /// <exception cref="InvalidOperationException">A scale component is zero, so the transform has no inverse.</exception>
    public Matrix4x4 WorldToLocalMatrix => LocalToWorldMatrix.Inverse;

    public Vector2 TransformPoint(Vector2 localPoint) => (Vector2)LocalToWorldMatrix.MultiplyPoint3x4(new Vector3(localPoint, 0f));

    public Vector2 InverseTransformPoint(Vector2 worldPoint) => (Vector2)WorldToLocalMatrix.MultiplyPoint3x4(new Vector3(worldPoint, 0f));

    public Vector2 TransformDirection(Vector2 localDirection) => Vector2.Rotate(localDirection, Rotation);

    /// <param name="worldPositionStays">Keep the current world position, rotation and scale instead of the local values.</param>
    /// <exception cref="InvalidOperationException">The new parent is this transform, one of its descendants, or in another scene.</exception>
    public void SetParent(Transform? parent, bool worldPositionStays = true)
    {
        if (parent == Parent)
        {
            return;
        }

        if (parent is not null)
        {
            if (parent.GameObject.Scene != GameObject.Scene)
            {
                throw new InvalidOperationException("Cannot parent across scenes.");
            }

            for (var t = parent; t is not null; t = t.Parent)
            {
                if (t == this)
                {
                    throw new InvalidOperationException($"Cannot parent '{GameObject.Name}' to its own descendant '{parent.GameObject.Name}'.");
                }
            }
        }

        var (position, rotation, scale) = (Position, Rotation, LossyScale);
        Parent?._children.Remove(this);
        Parent = parent;
        parent?._children.Add(this);
        MarkDirty();

        if (worldPositionStays)
        {
            var parentScale = parent?.LossyScale ?? Vector2.One;
            LocalScale = new Vector2(
                parentScale.X == 0f ? scale.X : scale.X / parentScale.X,
                parentScale.Y == 0f ? scale.Y : scale.Y / parentScale.Y);
            Rotation = rotation;
            Position = position;
        }

        GameObject.Scene.SyncHierarchyLifecycle(GameObject);
    }

    public int GetSiblingIndex() => Parent?._children.IndexOf(this) ?? GameObject.Scene.RootIndexOf(GameObject);

    public Transform? Find(string childName) => _children.FirstOrDefault(c => c.GameObject.Name == childName);

    internal void DetachChild(Transform child) => _children.Remove(child);

    internal void ClearParentForDestroy() => Parent = null;

    private void MarkDirty()
    {
        if (_dirty && _children.Count == 0)
        {
            return;
        }

        _dirty = true;
        foreach (var child in _children)
        {
            child.MarkDirty();
        }
    }
}
