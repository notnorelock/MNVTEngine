using MNVTEngine.Editor.Services;
using MNVTEngine.Particles;
using MNVTEngine.Scenes;

namespace MNVTEngine.Editor.Tests;

public sealed class ParticlePreviewTests
{
    [Fact]
    public void Preview_PlaysTheSelectedObjectsSystems_InEditModeOnly_AndStopsOnDeselect()
    {
        var scene = new Scene();
        var fire = scene.CreateGameObject("Fire");
        var system = fire.AddComponent<ParticleSystem>();
        system.Emission.RateOverTime = 50f;
        var child = scene.CreateGameObject("Embers", fire.Transform).AddComponent<ParticleSystem>();
        child.Emission.RateOverTime = 20f;
        var preview = new ParticlePreview();

        for (var i = 0; i < 10; i++)
        {
            preview.Update(fire, playMode: false, 0.05f);
        }

        Assert.Equal(2, preview.Systems.Count); // the selection and its children
        Assert.InRange(system.ParticleCount, 20, 25);
        Assert.InRange(child.ParticleCount, 8, 10);
        Assert.InRange(preview.Time, 0.49f, 0.51f);

        preview.Pause();
        var paused = system.ParticleCount;
        preview.Update(fire, playMode: false, 0.05f);
        Assert.Equal(paused, system.ParticleCount);

        preview.Speed = 2f;
        preview.Restart();
        preview.Update(fire, playMode: false, 0.05f);
        Assert.InRange(system.ParticleCount, 4, 5); // restarted, at twice the speed

        preview.Update(null, playMode: false, 0.05f);
        Assert.Equal((0, 0), (system.ParticleCount, preview.Systems.Count)); // deselected: stopped and cleared

        preview.Update(fire, playMode: true, 0.05f);
        Assert.Empty(preview.Systems); // Play Mode simulates the running scene instead
    }
}
