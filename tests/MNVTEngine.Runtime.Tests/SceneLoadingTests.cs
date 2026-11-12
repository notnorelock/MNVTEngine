using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Assets;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Runtime.Loading;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Runtime.Tests;

[Collection(nameof(RuntimeStatics))]
public sealed class SceneLoadingTests : IDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-load-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Asset(string path, byte[] data, string guid)
    {
        var file = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, data);
        File.WriteAllText(file + ".meta", $$"""{ "schemaVersion": 1, "guid": "{{guid}}", "settings": {} }""");
    }

    private static void WithDevice(Action<IGraphicsDevice> body)
    {
        VulkanGraphicsDevice device;
        try
        {
            device = VulkanGraphicsDevice.CreateHeadless(16, 16, NullLogger.Instance);
        }
        catch (GraphicsInitializationException ex)
        {
            Assert.Skip($"No Vulkan 1.4 device available: {ex.Message}");
            throw;
        }

        try
        {
            body(device);
        }
        finally
        {
            device.Dispose();
        }
    }

    [Fact]
    public void Progress_IsReal_Monotonic_AndTexturesArriveDecodedOnTheGameThread()
    {
        var guids = Enumerable.Range(1, 3).Select(i => $"c1000000-0000-4000-8000-00000000000{i}").ToList();
        for (var i = 0; i < guids.Count; i++)
        {
            Asset($"Textures/t{i}.png", Png, guids[i]);
        }

        Asset("Textures/broken.png", [1, 2, 3], "c1000000-0000-4000-8000-000000000009");
        var references = string.Join(", ", guids.Append("c1000000-0000-4000-8000-000000000009").Select(g => $"\"asset:{g}\""));
        Asset("Scenes/Level.mnvt", System.Text.Encoding.UTF8.GetBytes($$"""{ "schemaVersion": 2, "name": "Level", "gameObjects": [], "unused": [{{references}}] }"""), "c1000000-0000-4000-8000-0000000000aa");

        WithDevice(device =>
        {
            var assets = AssetDatabase.OpenReadOnly(_root);
            using var resources = new ResourceCache(device, assets, NullLogger.Instance);
            var built = 0;
            using var op = new SceneLoadOperation("Scenes/Level.mnvt", assets, resources, (json, path) =>
            {
                built++;
                return new Scene("Level");
            });

            var last = 0f;
            var sawDeterminate = false;
            for (var frame = 0; frame < 500 && !op.IsDone; frame++)
            {
                op.Step(TimeSpan.FromMilliseconds(2));
                if (!op.IsIndeterminate)
                {
                    sawDeterminate = true;
                    Assert.True(op.Progress >= last, $"progress went back from {last} to {op.Progress}");
                    last = op.Progress;
                }

                Thread.Sleep(1); // frames pass while the workers decode
                device.Present();
            }

            Assert.True(op.IsDone, $"not done; task: {op.CurrentTask}");
            Assert.Null(op.Error);
            Assert.NotNull(op.Result);
            Assert.True(sawDeterminate);
            Assert.Equal(1f, op.Progress, 3);
            Assert.Equal(1, built);
            Assert.All(guids, g => Assert.True(resources.HasTexture(Guid.Parse(g))));
            Assert.Single(op.TextureErrors); // the broken image is reported, not fatal
        });
    }

    [Fact]
    public void Splash_ShowsTheEngineWordmark_UnderMadeWith()
    {
        VulkanGraphicsDevice device;
        try
        {
            device = VulkanGraphicsDevice.CreateHeadless(640, 360, NullLogger.Instance, new VulkanDeviceOptions { Validation = true });
        }
        catch (GraphicsInitializationException ex)
        {
            Assert.Skip($"No Vulkan 1.4 device available: {ex.Message}");
            throw;
        }

        try
        {
            using var font = Graphics.Text.Font.LoadDefault();
            using var logo = device.CreateTexture(Graphics.Svg.EngineLogo.Render(420), SamplerState.LinearClamp, "logo");
            using var renderer2D = new Renderer2D(device);
            using var canvas = new Loading.StartupCanvas(renderer2D);
            var settings = new SplashScreenSettings { Enabled = true, Title = "", FadeIn = 0f, ShowProgress = false, ShowEngineBranding = true, Background = Mathematics.Color.Black, Foreground = Mathematics.Color.White };
            using var view = new SplashScreenView(settings, new StartupAssets(font, _ => null, _ => null, logo), AssetRef.None, AssetRef.None);
            view.Update(loaded: false, null, skip: false, new Mathematics.Vector2(640, 360), 1f, 0.016f);
            device.SetRenderTarget(null);
            device.Clear(Mathematics.Color.Black);
            view.Render(canvas, 640, 360);
            var pixels = new byte[640 * 360 * 4];
            device.ReadPixels(pixels);
            device.Present();

            // The 140 x 34 wordmark sits centered above the bottom margin (24); "Made with" is above it.
            var lit = 0;
            for (var y = 300; y < 336; y++)
            {
                for (var x = 250; x < 390; x++)
                {
                    lit += pixels[((y * 640) + x) * 4] > 60 ? 1 : 0;
                }
            }

            Assert.InRange(lit, 400, 4000); // strokes of the tinted wordmark, not an empty or filled box
            Assert.Equal(0, pixels[((340 * 640) + 100) * 4]); // background elsewhere
        }
        finally
        {
            device.Dispose();
        }

        Assert.True(device.Validation.Errors == 0, $"{device.Validation.Errors} validation error(s): {device.Validation.LastError}");
    }

    [Fact]
    public void LoadingScreen_IsOpaqueAtOnce_AndOnlyFadesOut()
    {
        using var font = Graphics.Text.Font.LoadDefault();
        using var view = new LoadingScreenView(new LoadingScreenOptions { FadeSeconds = 0.5f }, new StartupAssets(font, _ => null, _ => null));
        var size = new Mathematics.Vector2(640, 360);
        Assert.Equal(1f, view.Opacity); // the game must not show through while loading
        view.Update(null, "Loading", size, 1f, 0.016f);
        Assert.Equal(1f, view.Opacity);

        view.Closing = true;
        view.Update(1f, null, size, 1f, 0.25f);
        Assert.Equal(0.5f, view.Opacity, 3);
        view.Closing = false; // a new load while fading out
        view.Update(null, null, size, 1f, 0.016f);
        Assert.Equal(1f, view.Opacity);

        view.Closing = true;
        view.Update(1f, null, size, 1f, 1f);
        Assert.False(view.IsVisible);
    }

    [Fact]
    public void MissingScene_Fails_AndCancelledLoads_ProduceNothing()
    {
        Asset("Scenes/Ok.mnvt", """{ "schemaVersion": 2, "name": "Ok", "gameObjects": [] }"""u8.ToArray(), "c2000000-0000-4000-8000-000000000001");
        WithDevice(device =>
        {
            var assets = AssetDatabase.OpenReadOnly(_root);
            using var resources = new ResourceCache(device, assets, NullLogger.Instance);
            using var missing = new SceneLoadOperation("Scenes/Nope.mnvt", assets, resources, (_, _) => new Scene("x"));
            for (var i = 0; i < 500 && !missing.IsDone; i++)
            {
                missing.Step(TimeSpan.FromMilliseconds(1));
                Thread.Sleep(1);
            }

            Assert.IsType<SerializationException>(missing.Error);
            Assert.Null(missing.Result);

            using var cancelled = new SceneLoadOperation("Scenes/Ok.mnvt", assets, resources, (_, _) => new Scene("Ok"));
            cancelled.Cancel();
            cancelled.Step(TimeSpan.FromMilliseconds(5));
            Assert.True(cancelled.IsDone);
            Assert.Null(cancelled.Result);
        });
    }
}
