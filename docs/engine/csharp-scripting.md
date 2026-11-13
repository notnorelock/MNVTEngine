# C# game scripting

Game components are written in C# in a class library that belongs to the project. This is separate from the `.mnvts` gameplay scripting language and its VM (Milestone 6).

## Project setup

```json
// MyGame.mnvtproj
"scripts": { "project": "Scripts/MyGame.Scripts.csproj" }
```

```xml
<!-- Scripts/MyGame.Scripts.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\..\src\Engine\MNVTEngine.Scenes\MNVTEngine.Scenes.csproj" Private="false" />
  </ItemGroup>
</Project>
```

- **What game code references:** only the gameplay API (`MNVTEngine.Scenes` and what it exposes). The editor, ImGui and graphics backends are not available to game code.
- **Path rules:** the scripts project path must stay inside the project folder.
- **Build output:** `Library/ScriptAssemblies/` (gitignored). A source stamp there records which sources were built.
- Every public, non-abstract `Component` subclass with a public parameterless constructor is registered automatically. No attribute is needed: a component is saved under its full type name (`Namespace.Class`). After renaming or moving a class, add `[ComponentAlias("Old.Namespace.OldName")]` and scenes saved under the old name still load, and they are saved under the new name from then on. `[ComponentId("...")]` fixes the id explicitly; you rarely need it.

> **Security:** compiling runs `dotnet build`, which executes the project's MSBuild logic. Opening an untrusted project runs its code, as it would in Visual Studio or Unity.

## Loading

`GameAssemblyHost` loads each generation of game code into its own collectible `GameAssemblyLoadContext`:

- **From bytes:** assemblies (and their `.pdb`, for line numbers in stack traces) are loaded from bytes, so build output is never locked.
- **Host assemblies always win:** any assembly the host already has (engine, framework, Newtonsoft.Json, anything next to the host) resolves to the host's copy. Game code therefore shares the engine's `Component` type and cannot replace engine assemblies.
- **Load problems become diagnostics** instead of crashes:

| Code | Problem |
|---|---|
| `LOAD001` | Unreadable or invalid assembly |
| `LOAD002` | Type load failures |
| `LOAD003` | Component without a public parameterless constructor (warning) |
| `LOAD004` | Component id clash |

## Hot reload in the editor

`ScriptReloadController` polls the source stamp every second and compiles in the background. When a build succeeds:

1. The edit scene is saved to JSON while the old types are still registered.
2. The new generation is loaded and its components replace the old ones in the `ComponentRegistry`.
3. The scene is loaded back from the JSON. Data, ids, references, the selection and the unsaved-changes flag all survive. Components whose class was removed become `MissingComponent`, and their data is kept.
4. The old generation is unloaded. A couple of seconds later the controller checks that it was actually garbage-collected, and logs a warning if something still holds it.

Rules:

- **Static fields** in game code reset on reload.
- **Compile errors** are shown in the Console with file, line and code. The last good code stays loaded, and Play Mode is blocked until the errors are fixed.
- **During Play Mode** the new code is applied live:
  - The running game is saved with the old types, together with the progress of every `MissionRunner`. It is then loaded back with the new types and continues.
  - Kept: serialized fields (public fields and `[SerializeField]`), transforms, objects spawned at runtime, the game clock, and mission progress.
  - Reset: private fields and statics. Components run `Awake`/`Start` again, and sounds restart.
  - The edit scene is reloaded too, but Play Mode changes still never reach it.
- **On editor startup**, the scene is opened after the first script load, so its components resolve directly.

### Why unloading needs care

A collectible load context is only freed when nothing references its types. The engine releases its own references on unload: registry entries, `ComponentTypeInfo` cache entries, and `TypeDescriptor` metadata.

Newtonsoft.Json is the hard case. It keeps process-wide reflection caches (`ThreadSafeStore`) keyed by `Type` and `MemberInfo`, and has no API to clear them. Investigation showed these caches alone kept every serialized generation alive. `ReflectionCacheCleaner` clears them by reflection on unload; they repopulate on demand. A test fails if a Newtonsoft upgrade moves those internals, and the reload tests verify actual collection with a `WeakReference`.

Editor code that inspects game types must drop its per-type caches on `Reloaded`; the Inspector does this. Tests must not keep old-generation objects in locals of an `async` method, because those live in the state machine.

## Running outside the editor

```bash
dotnet run --project src/Tooling/MNVTEngine.Player -- samples/SceneDemo
```

- **Player:** `MNVTEngine.Player` is the development runner. It compiles the scripts if they are out of date, loads them, and runs the project.
- **Exit codes:** 3 if game code fails to compile or load.
- **Exported games:** `mnvt build` compiles the scripts with the profile's configuration and stores them in the globals content package under `Scripts/` (without assemblies the host ships). The game host loads them from memory into the same collectible context, without the SDK. See [building](../build-system/building.md).

## Tests

- `MNVTEngine.Build.Tests` compiles real game code against the engine assemblies. It covers loading, reload with data preserved, unloading, compile error locations, load failures, and host-shared assembly resolution.
- `MNVTEngine.Editor.Tests` covers hot reload end to end: initial compile, edit, compile error, fix, and live reload during Play Mode with game state kept.
