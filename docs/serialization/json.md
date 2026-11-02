# JSON serialization

All engine files use Newtonsoft.Json configured by `MnvtJson.CreateSettings()` (`MNVTEngine.Serialization`).
Never construct `JsonSerializerSettings` by hand for engine data.

## Settings

| Setting | Value | Why |
|---|---|---|
| `TypeNameHandling` | `None` | Project files are untrusted; type names in data are a code-execution vector |
| `MetadataPropertyHandling` | `Ignore` | `$type`, `$id`, `$ref` are treated as ordinary (unknown) members |
| `MissingMemberHandling` | `Error` | Typos and stale fields surface instead of being silently dropped |
| Naming | camelCase | |
| Enums | camelCase strings; integers rejected | Stable across enum reordering |
| `DateParseHandling` | `None` | Strings stay strings |
| Culture | Invariant | |
| `MaxDepth` | 64 | Guards against pathological nesting |

Polymorphic data (components, mission nodes) will use an explicit type registry with stable string IDs (Milestone 2), not CLR type names.

## Math type formats

| Type | JSON |
|---|---|
| `Vector2` | `{"x": 1.0, "y": 2.0}` |
| `Vector3` | `{"x", "y", "z"}` |
| `Vector4`, `Quaternion` | `{"x", "y", "z", "w"}` |
| `Color` | `{"r", "g", "b", "a"}` |
| `Rect` | `{"x", "y", "width", "height"}` |
| `Bounds` | `{"cx", "cy", "cz", "ex", "ey", "ez"}` (center, extents) |
| `Matrix4x4` | array of 16 numbers, column-major |

Reading is strict: all components are required, unknown or duplicate properties fail, non-numbers and non-finite values fail. Errors are `JsonSerializationException` with JSON path, line and position. Floats round-trip exactly.
