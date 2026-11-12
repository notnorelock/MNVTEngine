# Engine composition audit (Phase A)

Goal: an exported game contains only the engine functionality its project uses, like a Tailwind production build. This audit maps what exists, what forces code into every game today, and the proposed architecture. It changes no code. All sizes were measured on the Phase 1 export of an empty 2D project (`Flat`, Release, win-x64). The 3D export was byte-identical in content.

## 1. What an export contains today

| Part | Size | Files | Notes |
|---|---:|---:|---|
| Private .NET runtime (`Dependencies/Runtime`) | 85,265 KB | 196 | The whole `Microsoft.NETCore.App` shared framework, copied verbatim |
| Engine and package assemblies (`Dependencies/Managed`) | 11,936 KB | 53 | Every engine module, whatever the project uses |
| Native libraries (`Dependencies/Native`) | 8,324 KB | 4 | glfw3 227 KB, shaderc_shared 6,800 KB, soft_oal 1,087 KB, vma 205 KB |
| Game host, content | ~180 KB | | `<Game>.exe/.dll`, `<Game>_Data` |
| **Total** | **105,720 KB** | | |

**Framework.** A static assembly-reference closure from every shipped managed file reaches 107 of the 181 managed framework assemblies (59,933 of 66,848 KB). Newtonsoft.Json alone pulls in System.Private.Xml (7.4 MB), System.Data.Common, System.Linq.Expressions and System.Private.DataContractSerialization. Pruning unreachable assemblies saves about 7 MB. The 15 native runtime files (17,630 KB) include debugging and diagnostics components a shipped game does not load: Microsoft.DiaSymReader.Native (2.3 MB), two copies of mscordaccore (2.9 MB), mscordbi (1.2 MB), the standalone GCs clrgc/clrgcexp (1.4 MB) and msquic (0.5 MB). Large framework savings beyond that need IL trimming, which the current reflection-based serialization does not support yet (section 7).

**Shader compiler.** Exports precompile every shader: the Platformer export logged "2 precompiled, 0 compiled at run time". Yet every game ships shaderc (6.8 MB native plus Silk.NET.Shaderc 107 KB), because `VulkanGraphicsDevice` constructs the compiler unconditionally.

**Duplicate Vulkan binding.** Silk.NET.Vulkan (3,175 KB) and Vortice.Vulkan (1,601 KB) are both shipped. Vortice comes in only through Vortice.VulkanMemoryAllocator.

## 2. Assembly graph

Sizes are of the shipped DLL. `-` means not shipped (tooling, editor, executable).

| Project | Area | KB | Project references | Packages |
|---|---|---:|---|---|
| Core | Engine | 55 | - | - |
| Mathematics | Engine | 40 | - | - |
| Modules | Engine | 15 | - | - |
| ECS | Engine | 14 | Core | - |
| Input | Engine | 18 | Mathematics | - |
| Serialization | Engine | 60 | Mathematics | Newtonsoft.Json |
| Diagnostics | Engine | 80 | Core, Mathematics | Newtonsoft.Json |
| Scenes | Engine | 128 | ECS, Input, Serialization | - |
| Platform | Engine | 21 | Core, Input, Mathematics | Silk.NET.Input.Glfw, Silk.NET.Windowing.Glfw |
| Graphics | Engine | 669 | Core, Mathematics | StbImageSharp, StbTrueTypeSharp |
| Graphics.Vulkan | Engine | 155 | Graphics | Silk.NET.Vulkan(+EXT, KHR), Silk.NET.Shaderc(+Native), Vortice.VulkanMemoryAllocator |
| Assets | Engine | 120 | Graphics, **Scripting.Missions**, Serialization, **Terrain** | - |
| Assets.Models | Engine | - | Assets | SharpGLTF.Core, Silk.NET.Assimp, StbImageWriteSharp |
| Audio | Engine | 24 | Scenes | NVorbis |
| Audio.OpenAL | Engine | 12 | Audio | Silk.NET.OpenAL, OpenAL Soft native |
| UI | Engine | 140 | Graphics, Input, Scenes | - |
| Scripting | Engine | 170 | - | - |
| Scripting.Missions | Engine | 33 | Scripting, Serialization | - |
| Scripting.Bindings | Engine | 26 | **Audio**, Scenes, Scripting.Missions | - |
| Physics2D | Engine | 38 | Scenes | Box2D.NET |
| Physics3D | Engine | 56 | Scenes | BepuPhysics |
| Terrain | Engine | 402 | Mathematics | Clipper2, Newtonsoft.Json |
| Terrain.Components | Engine | 11 | Physics3D, Scenes, Terrain | - |
| CrashReporting | Engine | 49 | - | StbImageSharp, StbImageWriteSharp |
| GameAssemblies | Engine | 24 | Scenes | - |
| Runtime | Engine | 232 | Assets, **Audio**, CrashReporting, Diagnostics, Graphics, Modules, **Physics2D**, **Physics3D**, Platform, Scenes, **Scripting.Bindings**, **Terrain.Components**, **UI** | StbImageWriteSharp |
| DebugUI | Engine | - (Debug only) | Runtime | ImGui.NET |
| GameHost | Engine | exe | Audio.OpenAL, CrashReporting, GameAssemblies, Graphics.Vulkan, Runtime, DebugUI (conditional) | Microsoft.Extensions.Logging |
| Editor.Services | Editor | - | Runtime, Build | - |
| Editor | Editor | - | Audio.OpenAL, CrashReporting, DebugUI, Graphics.Vulkan, Editor.Services | Microsoft.Extensions.Logging |
| Build | Tooling | - | Assets.Models, GameAssemblies, Graphics.Vulkan, Runtime | - |
| CLI, Player | Tooling | - | Build, Graphics.Vulkan, Audio.OpenAL (+DebugUI, CrashReporting) | Microsoft.Extensions.Logging |

There are no project cycles; an architecture test enforces this. Microsoft.Extensions.DependencyInjection (93 KB) comes in through Microsoft.Extensions.Logging, and Microsoft.Extensions.DependencyModel and PlatformAbstractions (105 KB) through game-code loading.

## 3. Systems, components, renderers, loaders

**Components** (40, all with stable serialized ids):

| Group | Components | Assembly today |
|---|---|---|
| Scene basics | Transform, Camera, HierarchyStyle | Scenes |
| 2D world | SpriteRenderer, SpriteAnimator, Tilemap, Light2D, GlobalLight2D | **Scenes** |
| 3D world | MeshRenderer, Light, AmbientLight, SceneEnvironment, ModelAnimator, ModelAnimatorController | **Scenes** |
| Physics 2D | Rigidbody2D, BoxCollider2D, CircleCollider2D, TilemapCollider2D | Physics2D |
| Physics 3D | Rigidbody, Box/Sphere/Capsule/Cylinder/MeshCollider, Fixed/Ball/Hinge/Distance/SpringJoint, CharacterController | Physics3D |
| Terrain | WorldTerrain | Terrain.Components |
| Audio | AudioSource, AudioListener, AudioMixer | Audio |
| UI | UIDocument | UI |
| Scripting | MissionRunner | Scripting.Bindings |

**Scene systems** (attached per scene on first use): PhysicsWorld2D, PhysicsWorld3D, AudioSystem, UISystem. **AudioSystem and UISystem are attached to every scene by `GameRuntime`**, whether or not the scene uses them.

**Renderers:**

| Renderer | Assembly |
|---|---|
| Renderer2D, SpriteBatch, Camera2D, text and fonts, SVG | Graphics |
| Renderer3D, Lighting (shadows, sky), Meshes, Skinning, all mesh and terrain shader sources (MaterialShaders, 725 lines) | **Graphics**, so in every 2D game |
| Scene 2D pass (sprites, tilemaps, 2D lighting) | Runtime (`ResourceCache.RenderView`) |
| Scene 3D pass | Runtime (`SceneRenderer3D`) |
| Terrain pass | Runtime (`SceneRendererTerrain`) |
| UIRenderer | UI. **GameRuntime always creates it**, and the splash and loading screens are built from UI widgets |
| ImGui (debug UI) | DebugUI |

**Asset importers** live in Assets, a runtime assembly, so they ship in every game: Texture, Scene/Prefab, Animation, AnimatorController, Material, UILayout, **Terrain**, Json, Text, **Script** (compiles `.mnvts`, the reason for Assets → Scripting.Missions), Magic, and the ModelPlaceholder importer. Only model import (FBX, glTF, Assimp) is already separated (Assets.Models, build tooling).

**Runtime loaders:**

| Loader | Where it lives |
|---|---|
| Asset database, content archives | Assets |
| Textures, materials, shaders | ResourceCache (Runtime) |
| Models, model meshes, model materials, environment textures | ResourceCache (Runtime) |
| Terrain references | ResourceCache (Runtime) |
| Audio clips | read by AudioSystem through a callback from GameRuntime |
| Mission bytecode | Assets / Scripting.Missions |

**Editor-only dependencies** never ship today: ImGui.NET (in Release; Debug builds include DebugUI), Assimp, SharpGLTF, MSBuild and Roslyn (build). The validator already enforces this (MNVB507).

## 4. What forces code into every game

These are the couplings that make a module impossible to remove today, in order of impact:

1. **Runtime references every optional module.**
   - `GameRuntime` attaches AudioSystem and UISystem to every scene and always creates a UIRenderer.
   - Startup screens use UI widgets.
   - `RuntimeDiagnostics` reads PhysicsWorld2D, AudioSystem, UISystem and mission metrics directly.
   - `ResourceCache` resolves terrain references, and `SceneRendererTerrain` draws terrain.
   - `EngineModules` (phase 2) creates every module.
2. **Scenes holds both worlds' components.** `ComponentRegistry.CreateDefault` registers the 2D and 3D components, and `Scene` carries a host service for ModelAnimator.
3. **Graphics holds the 3D renderer and every mesh shader** (Rendering3D, Meshes, Skinning, MaterialShaders).
4. **Assets holds importers**, including the terrain and script importers. That is why Assets references Terrain and Scripting.Missions.
5. **Scripting.Bindings references Audio**: mission natives play sounds. So every game with missions ships audio, and the catalog says `scripting` requires `audio`.
6. **Graphics.Vulkan always loads shaderc**, although exports run with precompiled shaders only.
7. **GameHost references Runtime and its whole closure unconditionally.** The only existing conditional reference is DebugUI (`MnvtDevTools`).
8. **Phase 2's module semantics include every profile module by default.** "Default" activation means a 3D project gets terrain whether it uses it or not. That is the opposite of usage-based composition.

## 5. Proposed architecture

### 5.1 Three concepts, one resolver

- **Available.** The catalog: every module the engine distribution has, with its profiles, dependencies, assemblies, native libraries, build assets, components, asset types and script natives.
- **Enabled.** The project's switches: `"modules": { "audio": true, "terrain": false }`. `true` includes a module even if nothing uses it, for example when a script adds components by name. `false` forbids it, and using it becomes an error. A module with no switch is **automatic**: it is included if and only if the project uses it.
- **Used.** What the build's usage analysis finds (5.3).

The composition is (Used ∪ switched on), plus required dependencies, transitively. It must stay inside the profile and outside the switched-off set. Phase 2's `Default` activation becomes `Auto`. `Always` stays for infrastructure (core, platform, graphics, assets, runtime). `OptIn` stays for modules that are never inferred (devtools). Every included module carries the evidence that included it, and every excluded module its reason. The resolver keeps phase 2's guarantees: deterministic, no silent inclusion of incompatible modules, actionable MNVB errors.

### 5.2 Module boundaries

Existing assemblies are kept where they are coherent. New assemblies are created only where code of different modules is mixed today.

| Module | Assemblies (new in bold) | Change |
|---|---|---|
| core | Core, Mathematics, ECS, Serialization, Modules, Scenes, Input, Diagnostics | Scenes loses the 2D and 3D components. Diagnostics loses its direct reads of module systems; modules contribute metrics |
| platform | Platform | - |
| graphics | Graphics, Graphics.Vulkan | Graphics keeps the device abstraction, SpriteBatch, text, textures and materials. The 3D renderer and mesh shaders move to world.3d |
| shader compiler (tooling, editor, Player) | **Graphics.Vulkan.ShaderCompiler** | shaderc behind an `IShaderCompiler` given by the host. Exports run precompiled only, and the build fails if a shader was not precompiled |
| assets | Assets | Keeps the database, archives and runtime loading. Importers become contributions: core importers stay, the script importer moves to scripting and the terrain importer to terrain |
| runtime | Runtime, GameAssemblies, CrashReporting | Host only: project, loop, scene loading, resource cache core. No references to optional modules |
| world.2d | **World2D** | 2D components (same namespace and ids) plus the 2D scene pass from Runtime |
| world.3d | **World3D** | 3D components, `IMeshDataSource`, Renderer3D, Lighting, Meshes, Skinning, mesh, shadow and sky shaders (from Graphics), the 3D scene pass, model loading, instantiation and animation (from Runtime) |
| physics.2d | Physics2D | References World2D, for Tilemap |
| physics.3d | Physics3D | References World3D, for `IMeshDataSource` |
| terrain | Terrain, Terrain.Components | Gets the terrain pass, the terrain reference resolution and the terrain importer |
| audio | Audio, Audio.OpenAL | Attaches its system to scenes through an integration point |
| ui | UI | Attaches its system and draws through integration points. Startup screens no longer use UI widgets: they are drawn with SpriteBatch and text in Runtime |
| scripting | Scripting, Scripting.Missions, Scripting.Bindings | Bindings stop referencing Audio. Audio natives call a scene audio interface, implemented by the audio module when it is present; usage analysis requires audio when a mission uses them |
| devtools | DebugUI | Driven by the project switch, the configuration default and a report line (5.6) |
| editor (host) | Editor, Editor.Services, **Editor.World2D**, **Editor.World3D** | Tile painter, sprite tools and the 2D Scene view go to Editor.World2D; the 3D Scene view, gizmos, picking and terrain tools to Editor.World3D. The editor touches only the active profile's assembly, so the other never loads |

That is 5 new projects, and the total grows from 33 to 38. No one-class projects.

**Integration points.** These are explicit interfaces a module implements. The host checks the module instances it was given (`module is ISceneIntegration`): no scanning, no service locator.

| Interface | Purpose | Implemented by |
|---|---|---|
| `IEngineModule` | Register components; initialize and shut down | Every module with runtime code (phase 2) |
| `ISceneIntegration` | `SceneLoaded(scene, host)`, to attach a scene system with host services (audio device, font, clipboard, asset reader) | audio, ui |
| `IRenderIntegration` | A scene pass or a screen-space pass, with a draw order | world.2d, world.3d, terrain, ui |
| `IDiagnosticsIntegration` | Metrics providers for the overlay and profiler | physics, audio, ui, scripting |
| `IAssetImporterContribution` | Importers, in build and editor only | scripting (`.mnvts`), terrain (`.mnvtterrain`), models (already Assets.Models) |
| `IMissionNatives` | Script natives with their module, used for usage analysis | scripting core, audio |

### 5.3 Usage analysis (build time)

The build reads the project and reports evidence per module, for example `physics.2d: Rigidbody2D in Scenes/Level1.mnvt`.

| Source | Gives |
|---|---|
| Scenes and prefabs | Component ids, through the catalog's component ownership |
| Packaged assets | Asset type, through the catalog's asset-type ownership: `.ogg/.wav` → audio, `.mnvts` → scripting, `.mnvtterrain` → terrain, UI layouts → ui, model artifacts and mesh materials → world.3d, sprite animations → world.2d |
| Materials | Built-in shader ids → the module that owns the shader |
| C# game code | Assembly references and type references in the compiled scripts assembly (System.Reflection.Metadata) → owning engine assembly → module |
| Mission bytecode | Called natives → the natives' module |
| Project settings | Splash and loading screen fonts and images; devtools |
| Switches | `true` includes, `false` forbids |

**Limits.** Code that creates components only from strings, for example through reflection or `ComponentRegistry` lookups by name, cannot be seen. That is what `"module": true` is for, and `mnvt build analyze` says when a project has no evidence for a module it may need.

### 5.4 Generated bootstrap and conditional references

The build passes `-p:MnvtModules=core;platform;graphics;world.2d;physics.2d` to the GameHost publish.

- GameHost references each optional module's assemblies with `Condition="$(MnvtModules.Contains(';id;'))"`, the same way as `MnvtDevTools`.
- The build writes `ModuleBootstrap.g.cs` into the host's intermediate folder: an explicit list (`new Physics2DModule(), ...`) passed to `GameApplication`.
- Runtime no longer creates modules itself (`EngineModules` moves to the hosts).
- The editor and Player have their own explicit lists, which contain every module.

### 5.5 Packaging and proof

- **Native libraries** come from the included modules only: soft_oal only with audio, shaderc never in exports, cimgui only with devtools.
- **Shaders.** Only the included modules' built-in shaders are precompiled and packaged.
- **Framework.** Copy the static closure of the shipped managed files plus the runtime's required native files. Leave out the debugging and diagnostics natives listed in section 1 once each is verified unused by a shipped game; createdump stays if crash reporting needs it.
- **The validator fails a package** that contains an assembly or native library of an excluded module, an editor assembly or an importer-only assembly.
- **`build-report.json`** is written next to the package, and `mnvt build analyze <project>` prints it:
  - profile, and enabled, automatic, used and excluded modules, each with its evidence or reason;
  - included and excluded managed assemblies and native libraries, and included shaders and assets, each with its size and owning module;
  - managed, native, framework and total sizes;
  - trimming results, once trimming exists.

### 5.6 devtools

devtools is included when the project switches it on, or by default in Debug builds unless the project switches it off. Release follows the switch, which replaces `diagnostics.includeInRelease` with a migration. `MnvtDevTools` becomes an output of the resolver, not an independent input. The report and the validator use the same resolved value.

### 5.7 Migration

Projects without switches move from "every profile module" to "what they use". Existing projects keep loading. A project whose code needs a module that analysis cannot see adds a switch. `mnvt build analyze` and the build report show every change. Scene files are unchanged: ids stay, and types keep their namespaces in the new assemblies. Game script projects reference the module assemblies they use; templates and samples are updated, and a missing reference is a compile error, not a silent change.

## 6. Measured removable code

These are the shipped sizes of code that a project not using the module would no longer carry. Sizes are measured from the current export; the final figures come from real exports in Phase E.

| Module | Managed KB | Native KB |
|---|---:|---:|
| physics.3d (Physics3D, BepuPhysics, BepuUtilities) | 1,009 | - |
| terrain (Terrain, Terrain.Components, Clipper2) | 514 | - |
| physics.2d (Physics2D, Box2D.NET) | 348 | - |
| audio (Audio, Audio.OpenAL, Silk.NET.OpenAL, NVorbis) | 180 | 1,087 |
| scripting (Scripting, Missions, Bindings) | 229 | - |
| ui | 140 | - |
| shader compiler (Silk.NET.Shaderc, shaderc) | 107 | 6,800 |
| world.3d code in Graphics and Runtime | part of 669 + 232 | - |
| devtools (Release already excludes it) | - | - |
| framework: unreachable assemblies | 6,915 | - |
| framework: debugging and diagnostics natives (to verify) | - | up to ~9,800 |

The duplicate Vulkan binding (Vortice.Vulkan, 1,601 KB) is shared by every game. Replacing it means changing how VMA is bound; that is separate from module composition, and I recommend a separate task.

## 7. Trimming

IL trimming (`PublishTrimmed`, self-contained) is the only way to cut the framework substantially below its static closure. The blockers are:

- Newtonsoft.Json is reflection-based and not trim-safe.
- Component serialization reads public and `[SerializeField]` members by reflection.
- C# game code is loaded at run time, so the trimmer cannot see what it needs.

Recommendation: no trimming in Phase D. Ship the assembly-level closure and the native pruning, both of which are exact. Then evaluate trimming as an opt-in experiment: annotate the serializers and preserve game-referenced framework assemblies, and verify by running the exported samples. The report says plainly when trimming is off.

## 8. Tests (Phases B to E)

| Kind | What it checks |
|---|---|
| Architecture | Runtime, Scenes, Graphics, Assets and Diagnostics reference no optional module; shrink-only coupling list ends empty; World2D and World3D never reach each other |
| Usage analysis | Each evidence source, each switch rule, transitive dependencies, forbidden-but-used errors |
| Export | Real exports of minimal 2D, minimal 3D, and feature combinations (audio, UI, scripting, terrain, devtools), checking the package files themselves, not configuration |
| Run | Each export launched with Vulkan validation; the loaded assemblies are recorded and compared with the report |
| Compatibility | Existing scenes load unchanged; all samples run |
| Editor | The loaded assemblies after opening a 2D and a 3D project contain only the active profile's editor and world assemblies |
