# Mathematics conventions

Namespace: `MNVTEngine.Mathematics`. All engine-facing public APIs use these types; `System.Numerics` is only used internally (matrix inversion and determinant).

## Space and units

- **Right-handed**: +X right, +Y up, +Z towards the viewer. `Vector3.Cross(UnitX, UnitY) == UnitZ`. This is glTF's convention, so glTF models need no axis conversion.
- **Forward is -Z**: `Vector3.Forward == (0, 0, -1)`, `Vector3.Back == (0, 0, 1)`. Cameras look down their -Z, and `Transform.Forward` is the local -Z axis in world space (as in Godot and glTF; Unity is left-handed with +Z forward).
- 2D world space is the XY plane, Y up. One world unit has no fixed pixel size; the camera defines the mapping.
- **Positive rotation is counter-clockwise** when looking down the axis towards the origin (right-hand rule). In 2D this means positive Z rotation turns +X towards +Y.

## Angles

| API | Unit |
|---|---|
| `Mathf.Sin/Cos/Tan/Asin/Acos/Atan/Atan2` | radians |
| `Quaternion.Euler`, `AngleAxis`, `EulerAngles`, `Angle` | degrees |
| `Vector2.Angle/SignedAngle/Rotate`, `Vector3.Angle` | degrees |
| `Mathf.DeltaAngle/LerpAngle/MoveTowardsAngle` | degrees |

Convert with `Mathf.Deg2Rad` and `Mathf.Rad2Deg`.

## Quaternion

- Components `(X, Y, Z)` = vector part, `W` = scalar part. `Identity = (0, 0, 0, 1)`.
- `a * b` applies **b first, then a**.
- `Euler(x, y, z)` applies Z, then X, then Y: `q = Y * X * Z`.
- `LookRotation(forward, up)` turns -Z towards `forward` with +Y towards `up`; `FromToRotation(a, b)` is the shortest arc; `FromAxes(x, y, z)` builds a rotation from orthonormal axes.
- `EulerAngles` returns each angle in [0, 360). Near X = ±90° (gimbal lock) Z is folded into Y; the rotation is preserved, the individual angles are not.

## Matrix4x4

- Column-vector convention: `v' = M * v`. `A * B` applies **B first, then A**.
- `TRS(t, r, s) == Translate(t) * Rotate(r) * Scale(s)`: scale, then rotate, then translate.
- Field `Mrc` is row r, column c. Memory layout is **column-major** (`M00, M10, M20, M30, M01, ...`). Upload with `transpose: false`.
- Translation lives in `M03, M13, M23`.
- `Ortho` and `Perspective` produce GL-style clip space (y up, NDC z in [-1, 1]); the camera looks down -Z. They put z = 0 (for `Ortho(..., -1, 1)`) exactly on Vulkan's depth 0, the edge of the clip range, so the engine's renderers do not use them.
- `PerspectiveZeroToOne` and `OrthoZeroToOne` produce Vulkan's depth range (NDC z = 0 at near, 1 at far), still y up; the Vulkan device flips its viewport so clip space stays y-up. Every engine renderer uses them: `Camera2D`, the UI and the debug overlay map z = 0 to depth 0.5, and the 3D camera maps its near plane to 0.
- `LookAt(eye, target, up)` returns a **view** (world-to-camera) matrix.
- `MultiplyPoint` performs the w divide; `MultiplyPoint3x4` assumes an affine matrix; `MultiplyVector` ignores translation.

## Equality and tolerance

- `==`, `Equals` and `GetHashCode` are exact, so math types are safe as dictionary keys.
- Use `Vector2.Approximately`, `Mathf.Approximately` (relative + absolute tolerance), or `Quaternion.Approximately` (angle in degrees) for comparisons after computation.
- Normalizing a vector shorter than `Mathf.Epsilon` (1e-6) returns zero instead of NaN.

## Shapes

- `Rect`: `(X, Y)` is the minimum corner. `Contains` is half-open (min inclusive, max exclusive); `Overlaps` ignores touching edges. Negative sizes are normalized by `Min`/`Max`.
- `Bounds`: center + extents (half-size). `Contains` and `Intersects` are inclusive. `IntersectRay` uses the slab method and returns distance 0 when the ray starts inside.
- `Plane`: unit `Normal` and `Distance` with `Dot(Normal, p) + Distance = 0`; the signed distance is positive on the normal's side. `FromPoints(a, b, c)` uses the right-hand rule.
- `BoundingSphere`: `Contains`, `Intersects` (sphere, box) and `IntersectRay`.
- `Frustum.FromViewProjection(viewProjection, DepthRange)`: six inward planes (left, right, bottom, top, near, far). `Intersects(Bounds)` is the conservative plane test used for culling: it may keep a box near a corner, never drops a visible one.
- `Ray.IntersectTriangle` (Möller–Trumbore, both sides) for picking.
- `Color`: float RGBA, not clamped (HDR allowed); no implicit color-space conversion. `ToRgba32` packs bytes R, G, B, A in memory order.
