# Mathematics conventions

Namespace: `MNVTEngine.Mathematics`. All engine-facing public APIs use these types; `System.Numerics` is only used internally (matrix inversion and determinant).

## Space and units

- **Right-handed**: +X right, +Y up, +Z towards the viewer. `Vector3.Cross(UnitX, UnitY) == UnitZ`.
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
- `EulerAngles` returns each angle in [0, 360). Near X = ±90° (gimbal lock) Z is folded into Y; the rotation is preserved, the individual angles are not.

## Matrix4x4

- Column-vector convention: `v' = M * v`. `A * B` applies **B first, then A**.
- `TRS(t, r, s) == Translate(t) * Rotate(r) * Scale(s)`: scale, then rotate, then translate.
- Field `Mrc` is row r, column c. Memory layout is **column-major** (`M00, M10, M20, M30, M01, ...`). Upload with `transpose: false`.
- Translation lives in `M03, M13, M23`.
- `Ortho` and `Perspective` produce OpenGL clip space (NDC z in [-1, 1]); the camera looks down -Z.
- `MultiplyPoint` performs the w divide; `MultiplyPoint3x4` assumes an affine matrix; `MultiplyVector` ignores translation.

## Equality and tolerance

- `==`, `Equals` and `GetHashCode` are exact, so math types are safe as dictionary keys.
- Use `Vector2.Approximately`, `Mathf.Approximately` (relative + absolute tolerance), or `Quaternion.Approximately` (angle in degrees) for comparisons after computation.
- Normalizing a vector shorter than `Mathf.Epsilon` (1e-6) returns zero instead of NaN.

## Shapes

- `Rect`: `(X, Y)` is the minimum corner. `Contains` is half-open (min inclusive, max exclusive); `Overlaps` ignores touching edges. Negative sizes are normalized by `Min`/`Max`.
- `Bounds`: center + extents (half-size). `Contains` and `Intersects` are inclusive. `IntersectRay` uses the slab method and returns distance 0 when the ray starts inside.
- `Color`: float RGBA, not clamped (HDR allowed); no implicit color-space conversion. `ToRgba32` packs bytes R, G, B, A in memory order.
