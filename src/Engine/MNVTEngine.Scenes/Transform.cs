using MNVTEngine.Mathematics;

namespace MNVTEngine;

/// <summary>
/// Position, rotation and scale relative to the parent Transform, in 3D (right-handed, +Y up, objects face -Z; see
/// <c>docs/engine/mathematics.md</c>). 2D games use the XY plane: <see cref="Rotation2D"/> is the angle about Z in
/// degrees, counter-clockwise.
/// <para>
/// World values compose through the parent chain. The local-to-world and world-to-local matrices are cached and
/// recomputed only after this transform or an ancestor changes. With non-uniform parent scale and rotation combined,
/// <see cref="LocalToWorldMatrix"/> is exact while <see cref="LossyScale"/> is an approximation.
/// </para>
/// <para>A scale component of 0 makes the transform non-invertible; a <c>Vector2</c> assigned to <see cref="LocalScale"/>
/// becomes (x, y, 0), so 2D code sets scale with <see cref="SetLocalScale2D"/> or a <c>Vector3</c>.</para>
/// </summary>
[ComponentId("MNVTEngine.Transform")]
public sealed class Transform : Component
{
    private readonly List<Transform> _children = [];
    private Vector3 _localPosition;
    private Quaternion _localRotation = Quaternion.Identity;
    private Vector3 _localScale = Vector3.One;
    private Vector3? _localEulerHint;
    private Matrix4x4 _localToWorld = Matrix4x4.Identity;
    private Matrix4x4 _worldToLocal = Matrix4x4.Identity;
    private bool _dirty = true;
    private bool _inverseDirty = true;

    public Transform? Parent { get; private set; }

    public IReadOnlyList<Transform> Children => _children;

    public int ChildCount => _children.Count;

    public Vector3 LocalPosition
    {
        get => _localPosition;
        set
        {
            _localPosition = value;
            MarkDirty();
        }
    }

    public Quaternion LocalRotation
    {
        get => _localRotation;
        set
        {
            _localRotation = value.Normalized;
            _localEulerHint = null;
            MarkDirty();
        }
    }

    /// <summary>
    /// Local rotation as Euler angles in degrees (see <see cref="Quaternion.Euler(Vector3)"/>). Returns the angles last
    /// set here while the rotation is unchanged, so editing one angle in an inspector does not disturb the others.
    /// </summary>
    public Vector3 LocalEulerAngles
    {
        get => _localEulerHint ?? _localRotation.EulerAngles;
        set
        {
            _localRotation = Quaternion.Euler(value).Normalized;
            _localEulerHint = value;
            MarkDirty();
        }
    }

    public Vector3 LocalScale
    {
        get => _localScale;
        set
        {
            _localScale = value;
            MarkDirty();
        }
    }

    public Vector3 Position
    {
        get => LocalToWorldMatrix.Translation;
        set => LocalPosition = Parent is null ? value : Parent.WorldToLocalMatrix.MultiplyPoint3x4(value);
    }

    public Quaternion Rotation
    {
        get => Parent is null ? _localRotation : (Parent.Rotation * _localRotation).Normalized;
        set => LocalRotation = Parent is null ? value : Parent.Rotation.Inverse * value;
    }

    /// <summary>World rotation as Euler angles in degrees.</summary>
    public Vector3 EulerAngles
    {
        get => Parent is null ? LocalEulerAngles : Rotation.EulerAngles;
        set => Rotation = Quaternion.Euler(value);
    }

    /// <summary>2D: the local angle about Z in degrees (counter-clockwise). Setting it replaces any X/Y rotation.</summary>
    public float LocalRotation2D
    {
        get => LocalEulerAngles.Z;
        set => LocalEulerAngles = new Vector3(0f, 0f, value);
    }

    /// <summary>2D: the world angle about Z in degrees (counter-clockwise). Setting it replaces any X/Y rotation.</summary>
    public float Rotation2D
    {
        get => Parent is null ? LocalRotation2D : Rotation.EulerAngles.Z;
        set
        {
            if (Parent is null)
            {
                LocalRotation2D = value;
            }
            else
            {
                Rotation = Quaternion.Euler(0f, 0f, value);
            }
        }
    }

    /// <summary>World scale: the product of the local scales along the chain (exact without rotated non-uniform parents).</summary>
    public Vector3 LossyScale => Parent is null ? _localScale : Vector3.Scale(Parent.LossyScale, _localScale);

    /// <summary>The local -Z axis in world space: the way the object faces.</summary>
    public Vector3 Forward => Rotation * Vector3.Forward;

    /// <summary>The local +X axis in world space.</summary>
    public Vector3 Right => Rotation * Vector3.Right;

    /// <summary>The local +Y axis in world space.</summary>
    public Vector3 Up => Rotation * Vector3.Up;

    public Matrix4x4 LocalToWorldMatrix
    {
        get
        {
            if (_dirty)
            {
                var local = Matrix4x4.TRS(_localPosition, _localRotation, _localScale);
                _localToWorld = Parent is null ? local : Parent.LocalToWorldMatrix * local;
                _dirty = false;
                _inverseDirty = true;
            }

            return _localToWorld;
        }
    }

    /// <exception cref="InvalidOperationException">A scale component is zero, so the transform has no inverse.</exception>
    public Matrix4x4 WorldToLocalMatrix
    {
        get
        {
            var localToWorld = LocalToWorldMatrix;
            if (_inverseDirty)
            {
                _worldToLocal = localToWorld.Inverse;
                _inverseDirty = false;
            }

            return _worldToLocal;
        }
    }

    /// <summary>2D convenience: sets the X and Y scale and keeps Z.</summary>
    public void SetLocalScale2D(Vector2 scale) => LocalScale = new Vector3(scale.X, scale.Y, _localScale.Z);

    public Vector3 TransformPoint(Vector3 localPoint) => LocalToWorldMatrix.MultiplyPoint3x4(localPoint);

    public Vector3 InverseTransformPoint(Vector3 worldPoint) => WorldToLocalMatrix.MultiplyPoint3x4(worldPoint);

    /// <summary>Rotates a direction from local to world space (scale is ignored; the length is kept).</summary>
    public Vector3 TransformDirection(Vector3 localDirection) => Rotation * localDirection;

    public Vector3 InverseTransformDirection(Vector3 worldDirection) => Rotation.Inverse * worldDirection;

    /// <summary>Transforms a vector by rotation and scale (not position).</summary>
    public Vector3 TransformVector(Vector3 localVector) => LocalToWorldMatrix.MultiplyVector(localVector);

    /// <summary>Turns the object so <see cref="Forward"/> points at <paramref name="target"/> with its up towards <paramref name="worldUp"/>.</summary>
    public void LookAt(Vector3 target, Vector3 worldUp) => Rotation = Quaternion.LookRotation(target - Position, worldUp);

    public void LookAt(Vector3 target) => LookAt(target, Vector3.Up);

    /// <summary>Rotates by <paramref name="eulerDegrees"/> around the local axes (or world axes with <paramref name="relativeToWorld"/>).</summary>
    public void Rotate(Vector3 eulerDegrees, bool relativeToWorld = false)
    {
        var delta = Quaternion.Euler(eulerDegrees);
        if (relativeToWorld)
        {
            Rotation = delta * Rotation;
        }
        else
        {
            LocalRotation = _localRotation * delta;
        }
    }

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
            var parentScale = parent?.LossyScale ?? Vector3.One;
            static float Divide(float value, float by) => by == 0f ? value : value / by;
            LocalScale = new Vector3(Divide(scale.X, parentScale.X), Divide(scale.Y, parentScale.Y), Divide(scale.Z, parentScale.Z));
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
