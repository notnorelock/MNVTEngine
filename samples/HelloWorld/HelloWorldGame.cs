using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Timing;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Mathematics;
using MNVTEngine.Platform;

namespace MNVTEngine.Samples.HelloWorld;

/// <summary>
/// Milestone 1 sample: textured sprites, batching, alpha and additive blending, debug primitives,
/// a render-target minimap, camera zoom/rotation, and keyboard/mouse input.
/// Controls: WASD/arrows move, mouse wheel zooms, Q/E rotates the camera, Escape quits.
/// </summary>
internal sealed class HelloWorldGame(ILogger logger, int? exitAfterFrames, string? screenshotPath) : IGameLoop
{
    private const int OrbCount = 3000;
    private static readonly Rect World = new(-1200f, -700f, 2400f, 1400f);

    private readonly Orb[] _orbs = new Orb[OrbCount];
    private GameWindow _window = null!;
    private VulkanGraphicsDevice _device = null!;
    private Renderer2D _renderer = null!;
    private Texture2D _player = null!, _orb = null!, _tile = null!;
    private RenderTarget2D _minimap = null!;
    private Material _additive = null!;
    private Camera2D _camera = null!, _minimapCamera = null!, _uiCamera = null!;
    private Vector2 _playerPosition, _previousPlayerPosition, _playerVelocity;
    private float _playerAngle, _statsTimer;
    private int _frame;

    public int ExitCode { get; private set; }

    public void Load(GameWindow window)
    {
        _window = window;
        _device = VulkanGraphicsDevice.Create(window.VulkanSurface, window.FramebufferWidth, window.FramebufferHeight, logger, VulkanDeviceOptions.ForApplication("HelloWorld", window.Options.VSync, window.Options.GraphicsValidation));
        _renderer = new Renderer2D(_device);
        _additive = _renderer.CreateSpriteMaterial(BlendMode.Additive, "Sprite/Additive");

        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        _player = _device.LoadTexture(Path.Combine(assets, "player.png"), SamplerState.PointClamp);
        _orb = _device.LoadTexture(Path.Combine(assets, "orb.png"), SamplerState.LinearClamp);
        _tile = _device.LoadTexture(Path.Combine(assets, "tile.png"), SamplerState.PointRepeat);
        _minimap = _device.CreateRenderTarget(320, 180, SamplerState.LinearClamp, "Minimap");

        _camera = new Camera2D(window.FramebufferWidth, window.FramebufferHeight);
        _camera.UsePixelUnits();
        _minimapCamera = new Camera2D(_minimap.Width, _minimap.Height) { OrthographicSize = World.Height * 0.5f };
        _uiCamera = CreateUiCamera(window.FramebufferWidth, window.FramebufferHeight);

        var random = new Random(1234);
        for (var i = 0; i < _orbs.Length; i++)
        {
            var hue = (float)random.NextDouble();
            _orbs[i] = new Orb(
                new Vector2(World.X + ((float)random.NextDouble() * World.Width), World.Y + ((float)random.NextDouble() * World.Height)),
                Vector2.Rotate(Vector2.Right, (float)random.NextDouble() * 360f) * (40f + ((float)random.NextDouble() * 120f)),
                new Color(0.4f + (0.6f * hue), 0.5f, 1.4f - hue, 0.6f));
        }

        logger.LogInformation("Loaded {Orbs} orbs; {Resources} GPU resources live", _orbs.Length, _device.Resources.LiveCount);
    }

    public void FixedUpdate(float dt)
    {
        _previousPlayerPosition = _playerPosition;
        _playerPosition += _playerVelocity * dt;
        _playerPosition = Vector2.Min(Vector2.Max(_playerPosition, World.Min), World.Max);

        for (var i = 0; i < _orbs.Length; i++)
        {
            ref var orb = ref _orbs[i];
            orb.Position += orb.Velocity * dt;
            if (orb.Position.X < World.Min.X || orb.Position.X > World.Max.X)
            {
                orb.Velocity.X = -orb.Velocity.X;
            }

            if (orb.Position.Y < World.Min.Y || orb.Position.Y > World.Max.Y)
            {
                orb.Velocity.Y = -orb.Velocity.Y;
            }
        }
    }

    public void Update(FrameTime time)
    {
        var input = _window.Input;
        if (input.WasKeyPressed(Key.Escape))
        {
            _window.Close();
        }

        var move = new Vector2(input.GetAxis("Horizontal"), input.GetAxis("Vertical"));
        _playerVelocity = move.Normalized * 400f;
        if (move != Vector2.Zero)
        {
            _playerAngle = Mathf.MoveTowardsAngle(_playerAngle, Vector2.SignedAngle(Vector2.Up, move), 720f * time.DeltaTime);
        }

        if (input.ScrollDelta.Y != 0f)
        {
            _camera.Zoom = Mathf.Clamp(_camera.Zoom * MathF.Pow(1.1f, input.ScrollDelta.Y), 0.25f, 8f);
        }

        if (input.IsKeyDown(Key.Q))
        {
            _camera.Rotation += 60f * time.DeltaTime;
        }

        if (input.IsKeyDown(Key.E))
        {
            _camera.Rotation -= 60f * time.DeltaTime;
        }

        _statsTimer += time.UnscaledDeltaTime;
        if (_statsTimer >= 0.5f)
        {
            _window.Title = $"MNVTEngine - Hello World | {1f / time.UnscaledDeltaTime:0} fps | {_renderer.Batch.DrawCallCount} draws | {_renderer.Batch.SpriteCount} sprites";
            _statsTimer = 0f;
        }
    }

    public void Render(FrameTime time, float interpolation)
    {
        _renderer.Batch.ResetStats();
        var player = Vector2.LerpUnclamped(_previousPlayerPosition, _playerPosition, interpolation);
        _camera.Position = Vector2.Lerp(_camera.Position, player, 1f - MathF.Exp(-8f * time.DeltaTime));

        // Pass 1: minimap into a render target.
        var map = _renderer.Begin(_minimapCamera, target: _minimap);
        _device.Clear(new Color(0.05f, 0.06f, 0.1f));
        foreach (var orb in _orbs)
        {
            map.Draw(_renderer.Batch.WhitePixel, orb.Position, new Vector2(6f, 6f), orb.Color.WithAlpha(1f));
        }

        map.DrawRectangle(_camera.VisibleWorldBounds, Color.White, 8f);
        map.Draw(_player, player, new Vector2(60f, 60f), Color.Yellow, _playerAngle);
        _renderer.End();

        // Pass 2: world.
        var world = _renderer.Begin(_camera, SpriteSortMode.Deferred);
        _device.Clear(new Color(0.08f, 0.09f, 0.12f));
        world.Draw(_tile, World.Center, World.Size, Color.White, source: new Rect(0f, 0f, World.Width / 4f, World.Height / 4f));
        for (var x = World.Min.X; x <= World.Max.X; x += 200f)
        {
            world.DrawLine(new Vector2(x, World.Min.Y), new Vector2(x, World.Max.Y), new Color(1f, 1f, 1f, 0.08f), 2f);
        }

        for (var y = World.Min.Y; y <= World.Max.Y; y += 200f)
        {
            world.DrawLine(new Vector2(World.Min.X, y), new Vector2(World.Max.X, y), new Color(1f, 1f, 1f, 0.08f), 2f);
        }

        world.DrawRectangle(World, Color.Cyan, 4f);
        _renderer.End();

        var glow = _renderer.Begin(_camera, SpriteSortMode.Deferred, _additive);
        foreach (var orb in _orbs)
        {
            glow.Draw(_orb, orb.Position, new Vector2(24f, 24f), orb.Color);
        }

        _renderer.End();

        var actors = _renderer.Begin(_camera, SpriteSortMode.BackToFront);
        actors.Draw(_orb, player, new Vector2(160f, 160f), new Color(0.2f, 0.8f, 1f, 0.35f), depth: 1f);
        actors.Draw(_player, player, new Vector2(64f, 64f), Color.White, _playerAngle, depth: 0f);
        var mouseWorld = _camera.ScreenToWorld(_window.Input.MousePosition);
        actors.DrawCircle(mouseWorld, 20f * _camera.UnitsPerPixel, Color.Yellow, 2f * _camera.UnitsPerPixel, depth: -1f);
        _renderer.End();

        // Pass 3: screen-space UI (1 unit = 1 pixel, origin bottom-left).
        var ui = _renderer.Begin(_uiCamera);
        var mapRect = new Rect(_uiCamera.ViewportWidth - _minimap.Width - 16f, _uiCamera.ViewportHeight - _minimap.Height - 16f, _minimap.Width, _minimap.Height);
        ui.FillRectangle(new Rect(mapRect.X - 4f, mapRect.Y - 4f, mapRect.Width + 8f, mapRect.Height + 8f), new Color(0f, 0f, 0f, 0.6f));
        ui.Draw(_minimap, mapRect.Min, mapRect.Size, Color.White, pivot: Vector2.Zero);
        ui.DrawRectangle(mapRect, Color.Cyan, 2f);
        _renderer.End();

        _frame++;
        var exiting = exitAfterFrames is { } limit && _frame >= limit;
        if (exiting && screenshotPath is not null)
        {
            SaveScreenshot(screenshotPath); // before presenting: the back buffer is gone afterwards
        }

        _device.Present();
        if (exiting)
        {
            logger.LogInformation("Frame {Frame}: {Draws} draw calls, {Sprites} sprites", _frame, _renderer.Batch.DrawCallCount, _renderer.Batch.SpriteCount);
            _window.Close();
        }
    }

    public void Resize(int width, int height)
    {
        _device.ResizeBackBuffer(width, height);
        _camera.SetViewportSize(width, height);
        _camera.UsePixelUnits();
        _uiCamera = CreateUiCamera(width, height);
    }

    public void Unload()
    {
        _minimap.Dispose();
        _tile.Dispose();
        _orb.Dispose();
        _player.Dispose();
        _renderer.Dispose();
        ExitCode = _device.Resources.LiveCount == 0 ? 0 : 1;
        if (ExitCode != 0)
        {
            logger.LogError("{Count} GPU resources were not disposed", _device.Resources.LiveCount);
        }

        _device.Dispose();
    }

    private static Camera2D CreateUiCamera(int width, int height)
    {
        var camera = new Camera2D(width, height) { Position = new Vector2(width * 0.5f, height * 0.5f) };
        camera.UsePixelUnits();
        return camera;
    }

    private void SaveScreenshot(string path)
    {
        _device.SetRenderTarget(null);
        var pixels = new byte[_device.BackBufferWidth * _device.BackBufferHeight * 4];
        _device.ReadPixels(pixels);
        using var stream = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(pixels, _device.BackBufferWidth, _device.BackBufferHeight, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
        logger.LogInformation("Screenshot saved to {Path}", Path.GetFullPath(path));
    }

    private struct Orb(Vector2 position, Vector2 velocity, Color color)
    {
        public Vector2 Position = position;
        public Vector2 Velocity = velocity;
        public Color Color = color;
    }
}
