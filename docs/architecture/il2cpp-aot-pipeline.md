# IL2CPP and ahead-of-time compilation

How MNVTEngine games can run without a JIT, what exists today, and what does not. Summary: `mnvt build --scripting-backend aot` produces a **NativeAOT** game, a single native executable that contains the engine and the game's C# scripts. There is **no IL2CPP backend**, so MNVTEngine generates no C++, and `--scripting-backend il2cpp` is refused (MNVB111).

## Three ways to run C#

| | JIT (`jit`, default) | NativeAOT (`aot`) | IL → C++ (IL2CPP-style) |
|---|---|---|---|
| What ships | IL assemblies plus a private .NET runtime (CoreCLR, RyuJIT) | One native executable: the code is compiled ahead of time by RyuJIT through ILCompiler and linked with the NativeAOT runtime | C++ source generated from IL, compiled by a platform C++ compiler, plus a runtime library written in C++ |
| Code generation | At run time | At build time (`dotnet publish -p:PublishAot=true`) | At build time, in two steps (IL → C++ → machine code) |
| Loading code at run time | Yes (`AssemblyLoadContext`, hot reload) | No | No |
| `Reflection.Emit`, dynamic code | Yes | No (expressions are interpreted) | No |
| Reflection | Full | Only on metadata the compiler kept | Only on metadata the converter kept |
| Platforms where a JIT is forbidden (consoles, iOS) | No | Where the .NET NativeAOT runtime supports the platform | Yes, given a C++ compiler for the platform; this is why Unity built IL2CPP |
| Available to MNVTEngine | Yes | Yes: part of the .NET SDK, MIT licensed | No (see below) |

"IL2CPP" names Unity's IL-to-C++ converter specifically. A NativeAOT build is not IL2CPP: no C++ is generated. MNVTEngine never labels an AOT build as IL2CPP. The build report describes the `aot` backend as "Not IL2CPP: no C++ is generated".

## Why not Unity's IL2CPP

- **Licensing.** IL2CPP is a proprietary part of the Unity Editor. The converter and the `libil2cpp` runtime that ships with the editor come under Unity's terms for making Unity content. Unity offers no separate license that lets another engine use, extract or redistribute them. This project assumes they cannot be reused, and nothing in it depends on Unity code.
- **Coupling.** The converter targets Unity's class libraries and runtime conventions: its own GC integration, its own icalls and Mono-derived class libraries. Even technically it is not a general-purpose .NET tool.

Other IL-to-C++ work exists, but none of it is a maintained, general .NET 11 option. CoreRT had an experimental C++ code generator that was not carried into NativeAOT, and the community converters are research-grade. Building our own converter would mean a new compiler and a new runtime (GC, exceptions, generics, threading). That is far outside this engine's scope. If a platform without a JIT is ever needed, the first choice is NativeAOT for that platform where .NET supports it.

## The NativeAOT backend

### Approaches

**A. Native closure (implemented).** The host, the engine modules the game uses and the game's own C# assembly are compiled into one executable. The game's code is known at build time, so the build references it like any other assembly.

**B. Bridge.** A native host embeds a runtime that loads the game's IL. This keeps run-time loading, but it needs CoreCLR or Mono inside the game, which is the JIT build again with extra steps. It was rejected because it brings none of AOT's benefits (no JIT, smaller size, faster start).

### What the build does (`mnvt build <project> --scripting-backend aot`, or `"scriptingBackend": "aot"` in a build profile)

1. Content, usage analysis and module resolution run as for JIT builds.
2. `ModuleBootstrap` writes the usual `ModuleBootstrap.g.cs`. `AotBootstrap` adds `GameComponents.g.cs`:
   - `Register<T>()` for every game component, taken from the compiled scripts at build time. There is no `AssemblyLoadContext`, no `Assembly.Load` and no type scanning in the game.
   - A method that is never called but names every closed generic collection over a value type that component serialization creates by reflection, for example `List<TileCell>`, `ReadOnlyCollection<TileCell>`, `TileCell[]` and dictionaries. The types come from the serializable members (`ComponentTypeInfo`) of every component of the game's modules and of the game, followed into nested data types. Without this the game fails at scene load with "missing native code" (found in the proof of concept with `Tilemap.Cells`).
3. `ModuleBootstrap.g.props` additionally sets `PublishAot`, defines `MNVT_AOT`, references the scripts assembly, and lists the **preservation roots** (`TrimmerRootAssembly`):
   - every assembly of the game's modules, engine and third-party, so components and settings keep the metadata Newtonsoft.Json reflects over. BepuPhysics, for one, creates its constraint solvers with `Activator.CreateInstance`;
   - the game's scripts assembly;
   - Newtonsoft.Json.
   The interop bindings (Silk.NET, Vortice) are not rooted: they are large, call native code directly and need no reflection.
4. `dotnet publish` of GameHost compiles the closure with ILCompiler. ILCompiler's AOT and trim analysis warnings (IL2026, IL3050, IL2104, IL3053, ...) appear among the build's diagnostics as warnings. The roots above are what makes the flagged reflection work; the test run verifies it.
5. Layout: `<Game>.exe`, `Dependencies/Native` (glfw3, vma, soft_oal with audio), `<Game>_Data`. There is no `Dependencies/Runtime`, no `Dependencies/Managed` and no host `.dll`. Release builds ship no `.pdb`. The manifest records `"scriptingBackend": "aot"` and no scripts assembly. The validator checks this layout (MNVB503). It checks the content with the key the build passes; a standalone `mnvt validate` of an AOT game warns that the content was not checked (the key is compiled into the executable).

### Reflection, serialization and generics

| Area | Under NativeAOT | How it is handled |
|---|---|---|
| Component (de)serialization: Newtonsoft over public fields, `[SerializeField]`, `ComponentContractResolver` | Needs member metadata and constructors | The assemblies are rooted; Newtonsoft's late-bound reflection delegates work without `Reflection.Emit` |
| Generic collections over value types created by reflection | No native code unless named somewhere | `KeepGenericInstantiations` in `GameComponents.g.cs` |
| `GameProject`, `GamePackage`, import settings (`ToObject<T>`) | Constructors trimmed without roots (found in the proof of concept: "Unable to find a constructor ... GamePackage") | Engine assemblies rooted |
| `World.PoolFor(Type)` (`MakeGenericType`) | Works for component types whose pools are compiled | Components are registered with `Register<T>()`, so their generic code is generated |
| Game component discovery | Reflection scanning is not allowed | Generated `Register<T>()` list |
| Hot reload, run-time loading of C# | Impossible | JIT builds and the editor only |
| `.mnvts` mission scripts | Own bytecode VM, no IL | Unchanged: bytecode ships in the packages |
| Crash reporting | No `createdump` (no runtime folder) | The in-process dump path still writes `crash.dmp`, as seen in the proof of concept |

What still needs care is game code that reflects over types the build cannot see, such as types named only in strings. For those, put the type in a rooted assembly (the game's own assembly is rooted whole) or use JIT.

### Measured (release, win-x64, this machine)

| Export | JIT, pruned framework (KB) | NativeAOT (KB) | of which the executable | Run (120 frames, `--gpu-validation`) |
|---|---:|---:|---:|---|
| Platformer (2D, audio, scripting, UI, physics) | 82,758 | 27,504 | 25.3 MB | OK, 0 VUIDs, script and UI output as JIT |
| UIShowcase (2D, scripting, UI) | 81,458 | 24,740 | 23.3 MB | OK, 0 VUIDs |
| Showcase3D (3D, physics) | 85,278 | 32,368 | 27.8 MB | OK, 0 VUIDs, same image |
| TerrainDemo (3D, physics, terrain) | 82,499 | 31,885 | 30.6 MB | OK, 0 VUIDs, same image |

The NativeAOT publish adds about one minute to a build. The basic-2d template's AOT export, test run included, is covered by `AotBuildTests`.

## Limitations and next steps

- **Windows x64 only**, like the JIT backend. Other NativeAOT targets need their native libraries (glfw, vma, OpenAL) and a host linker.
- **Requires the MSVC linker** (Visual Studio C++ build tools) on the build machine, as NativeAOT does on Windows.
- **Debug AOT builds** include dev tools like Debug JIT builds; the overlay works (Platformer, `--overlay`). They keep the executable's native `.pdb` (about 170 MB) for native debugging.
- **Preservation is coarse.** Whole module assemblies are rooted. Source-generated serializers for components would let the trimmer drop unused engine code and remove most analysis warnings. That is the path to smaller executables, and it would be needed for any IL2CPP-like future.
- **Game code reflection** beyond component serialization is the game's responsibility (rooted game assembly; see above).
