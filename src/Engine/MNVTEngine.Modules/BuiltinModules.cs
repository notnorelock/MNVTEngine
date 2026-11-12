namespace MNVTEngine.Modules;

/// <summary>
/// The engine's modules, registered explicitly. Assembly lists name what each module ships; where an engine assembly
/// still references another module's assembly outside its declared dependencies, the architecture tests list it as a
/// known coupling to remove (docs/engine/modules.md). Hosts are not modules: the game executable (GameHost) and the
/// shared editor (Editor, Editor.Services) host every module; profile-specific editor assemblies belong to their module.
/// </summary>
public static class BuiltinModules
{
    private static readonly ProjectProfile[] TwoD = [ProjectProfile.TwoD];
    private static readonly ProjectProfile[] ThreeD = [ProjectProfile.ThreeD];

    public static ModuleDefinition Core { get; } = new("core", "Core", "Foundation: timing, logging, math, ECS, scenes, serialization, input and diagnostics.")
    {
        Activation = ModuleActivation.Always,
        RuntimeAssemblies =
        [
            "MNVTEngine.Core", "MNVTEngine.Mathematics", "MNVTEngine.ECS", "MNVTEngine.Serialization", "MNVTEngine.Modules",
            "MNVTEngine.Scenes", "MNVTEngine.Input", "MNVTEngine.Diagnostics", "Newtonsoft.Json", "Silk.NET.Core", "Silk.NET.Maths",
        ],
        Components = ["MNVTEngine.Transform", "MNVTEngine.Camera", "MNVTEngine.HierarchyStyle"],
    };

    public static ModuleDefinition Platform { get; } = new("platform", "Platform", "Window, Vulkan surface and input devices (GLFW).")
    {
        Activation = ModuleActivation.Always,
        Requires = ["core"],
        RuntimeAssemblies = ["MNVTEngine.Platform", "Silk.NET.GLFW", "Silk.NET.Input.Common", "Silk.NET.Input.Glfw", "Silk.NET.Windowing.Common", "Silk.NET.Windowing.Glfw"],
        NativeLibraries = ["glfw3.dll"],
    };

    public static ModuleDefinition Graphics { get; } = new("graphics", "Graphics", "Device abstraction, the Vulkan 1.4 backend, textures, text, sprite batching and materials.")
    {
        Activation = ModuleActivation.Always,
        Requires = ["core"],
        RuntimeAssemblies =
        [
            "MNVTEngine.Graphics", "MNVTEngine.Graphics.Vulkan", "Silk.NET.Vulkan", "Silk.NET.Vulkan.Extensions.EXT", "Silk.NET.Vulkan.Extensions.KHR",
            "Silk.NET.Shaderc", "Vortice.Vulkan", "Vortice.VulkanMemoryAllocator", "StbImageSharp", "StbTrueTypeSharp",
        ],
        NativeLibraries = ["shaderc_shared.dll", "vma.dll"],
        BuildAssets = ["shader:sprite"],
    };

    public static ModuleDefinition Assets { get; } = new("assets", "Assets", "Asset database, content archives and importers.")
    {
        Activation = ModuleActivation.Always,
        Requires = ["core", "graphics"],
        Optional = ["scripting"],
        RuntimeAssemblies = ["MNVTEngine.Assets"],
    };

    public static ModuleDefinition Runtime { get; } = new("runtime", "Runtime", "Game host: project loading, the frame loop, rendering of scenes, crash reporting and game code loading.")
    {
        Activation = ModuleActivation.Always,
        Requires = ["core", "platform", "graphics", "assets"],
        RuntimeAssemblies =
        [
            "MNVTEngine.Runtime", "MNVTEngine.GameAssemblies", "MNVTEngine.CrashReporting", "StbImageWriteSharp", "Microsoft.Extensions.Logging",
            "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyModel", "Microsoft.DotNet.PlatformAbstractions",
        ],
    };

    public static ModuleDefinition Audio { get; } = new("audio", "Audio", "Audio sources, listener and mixer (OpenAL Soft, Ogg Vorbis).")
    {
        Requires = ["core"],
        RuntimeAssemblies = ["MNVTEngine.Audio", "MNVTEngine.Audio.OpenAL", "Silk.NET.OpenAL", "NVorbis"],
        NativeLibraries = ["soft_oal.dll"],
        Components = ["MNVTEngine.AudioSource", "MNVTEngine.AudioListener", "MNVTEngine.AudioMixer"],
    };

    public static ModuleDefinition Scripting { get; } = new("scripting", "Scripting", "The .mnvts language, its VM and missions, and their engine bindings.")
    {
        Requires = ["core", "audio"],
        RuntimeAssemblies = ["MNVTEngine.Scripting", "MNVTEngine.Scripting.Missions", "MNVTEngine.Scripting.Bindings"],
        Components = ["MNVTEngine.MissionRunner"],
    };

    public static ModuleDefinition UI { get; } = new("ui", "UI", "Screen-space UI documents; the same in 2D and 3D projects.")
    {
        Requires = ["core", "graphics"],
        RuntimeAssemblies = ["MNVTEngine.UI"],
        Components = ["MNVTEngine.UIDocument"],
    };

    public static ModuleDefinition DevTools { get; } = new("devtools", "Developer tools", "In-game debug UI (ImGui). Debug builds include it; Release only when diagnostics.includeInRelease is set.")
    {
        Activation = ModuleActivation.OptIn,
        Requires = ["runtime"],
        RuntimeAssemblies = ["MNVTEngine.DebugUI", "ImGui.NET"],
        NativeLibraries = ["cimgui.dll"],
    };

    public static ModuleDefinition World2D { get; } = new("world.2d", "2D world", "Sprites, sprite animation, tilemaps and 2D lighting, and their rendering.")
    {
        Profiles = TwoD,
        Requires = ["core", "graphics"],
        Components = ["MNVTEngine.SpriteRenderer", "MNVTEngine.SpriteAnimator", "MNVTEngine.Tilemap", "MNVTEngine.Light2D", "MNVTEngine.GlobalLight2D"],
    };

    public static ModuleDefinition World3D { get; } = new("world.3d", "3D world", "Meshes, models and their animation, 3D lights, shadows, environment and sky, and their rendering.")
    {
        Profiles = ThreeD,
        Requires = ["core", "graphics", "assets"],
        BuildAssets =
        [
            "shader:standard", "shader:standard-skinned", "shader:standard-instanced", "shader:unlit", "shader:unlit-skinned",
            "shader:unlit-instanced", "shader:shadow-caster", "shader:shadow-caster-skinned", "shader:sky",
        ],
        Components =
        [
            "MNVTEngine.MeshRenderer", "MNVTEngine.Light", "MNVTEngine.AmbientLight", "MNVTEngine.SceneEnvironment",
            "MNVTEngine.ModelAnimator", "MNVTEngine.ModelAnimatorController",
        ],
    };

    public static ModuleDefinition Physics2D { get; } = new("physics.2d", "2D physics", "Rigidbodies, colliders and tilemap collision (Box2D).")
    {
        Profiles = TwoD,
        Requires = ["world.2d"],
        RuntimeAssemblies = ["MNVTEngine.Physics2D", "Box2D.NET"],
        Components = ["MNVTEngine.Rigidbody2D", "MNVTEngine.BoxCollider2D", "MNVTEngine.CircleCollider2D", "MNVTEngine.TilemapCollider2D"],
    };

    public static ModuleDefinition Physics3D { get; } = new("physics.3d", "3D physics", "Rigidbodies, colliders, joints and the character controller (BEPUphysics 2).")
    {
        Profiles = ThreeD,
        Requires = ["world.3d"],
        RuntimeAssemblies = ["MNVTEngine.Physics3D", "BepuPhysics", "BepuUtilities"],
        Components =
        [
            "MNVTEngine.Rigidbody", "MNVTEngine.BoxCollider", "MNVTEngine.SphereCollider", "MNVTEngine.CapsuleCollider",
            "MNVTEngine.CylinderCollider", "MNVTEngine.MeshCollider", "MNVTEngine.FixedJoint", "MNVTEngine.BallJoint",
            "MNVTEngine.HingeJoint", "MNVTEngine.DistanceJoint", "MNVTEngine.SpringJoint", "MNVTEngine.CharacterController",
        ],
    };

    public static ModuleDefinition Terrain { get; } = new("terrain", "Terrain", "Terrain worlds: terrain, roads, rivers, foliage and props, with collision.")
    {
        Profiles = ThreeD,
        Requires = ["world.3d", "physics.3d"],
        RuntimeAssemblies = ["MNVTEngine.Terrain", "MNVTEngine.Terrain.Components", "Clipper2Lib"],
        BuildAssets = ["shader:terrain-splat"],
        Components = ["MNVTEngine.WorldTerrain"],
    };

    public static IReadOnlyList<ModuleDefinition> All { get; } =
        [Core, Platform, Graphics, Assets, Runtime, Audio, Scripting, UI, DevTools, World2D, World3D, Physics2D, Physics3D, Terrain];
}
