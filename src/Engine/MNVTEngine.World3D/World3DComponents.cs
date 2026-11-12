using MNVTEngine.Scenes;

namespace MNVTEngine;

/// <summary>The components of the 3D world (module world.3d), and the scene services they use.</summary>
public static class World3DComponents
{
    public static ComponentRegistry Register(ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register<MeshRenderer>().Register<Light>().Register<AmbientLight>().Register<SceneEnvironment>()
            .Register<ModelAnimator>().Register<ModelAnimatorController>();
    }

    extension(Scene scene)
    {
        /// <summary>Host service for <see cref="ModelAnimator"/>: the animation clips of imported models.</summary>
        public IModelAnimationProvider? ModelAnimations
        {
            get => scene.GetService<IModelAnimationProvider>();
            set => scene.SetService(value);
        }

        /// <summary>Host service for systems that need a renderer's triangles on the CPU (mesh colliders).</summary>
        public IMeshDataProvider? MeshData
        {
            get => scene.GetService<IMeshDataProvider>();
            set => scene.SetService(value);
        }
    }
}
