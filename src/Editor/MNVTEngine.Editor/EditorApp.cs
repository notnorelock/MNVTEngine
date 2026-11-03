using System.Diagnostics.CodeAnalysis;
using ImGuiNET;
using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using MNVTEngine.Core.Timing;
using MNVTEngine.Editor.ImGuiBackend;
using MNVTEngine.Editor.Panels;
using MNVTEngine.Editor.Services;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.OpenGL;
using MNVTEngine.Mathematics;
using MNVTEngine.Platform;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;
using SysVector2 = System.Numerics.Vector2;

namespace MNVTEngine.Editor;

internal sealed record EditorOptions(int? ExitAfterFrames, string? ScreenshotPath, bool PlayOnStart, string? SelectOnStart = null);

/// <summary>
/// The editor application: dockable workspace (menu, toolbar, panels), scene file commands with unsaved-change
/// protection, and Play Mode. UI is built in <see cref="Update"/>; views and ImGui are drawn in <see cref="Render"/>.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "GPU and ImGui resources live from IGameLoop.Load to IGameLoop.Unload.")]
internal sealed partial class EditorApp(GameProject project, ComponentRegistry registry, ILoggerFactory loggerFactory, InMemoryLoggerProvider logs, EditorOptions options) : IGameLoop
{
    private const string DockSpaceName = "MainDockSpace";

    private readonly ILogger _logger = loggerFactory.CreateLogger("Editor");
    private EditorContext _context = null!;
    private HierarchyPanel _hierarchy = null!;
    private InspectorPanel _inspector = null!;
    private SceneViewPanel _sceneView = null!;
    private GameViewPanel _gameView = null!;
    private ProjectPanel _projectPanel = null!;
    private ConsolePanel _console = null!;
    private string _iniPath = "";
    private bool _resetLayout;
    private Action? _afterUnsavedPrompt;
    private bool _openUnsavedPrompt;
    private bool _openSaveAs;
    private bool _openScenePicker;
    private string _saveAsPath = "Scenes/New.mnvt";
    private int _frames;
    private double _lastTick;

    public int ExitCode { get; private set; }

    public void Load(GameWindow window)
    {
        var device = GLGraphicsDevice.Create(window.GraphicsContext, window.FramebufferWidth, window.FramebufferHeight, loggerFactory.CreateLogger("Graphics"));
        var renderer = new Renderer2D(device);
        var resources = new ResourceCache(device, project.AssetsDirectory, loggerFactory.CreateLogger("Resources"));
        var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
        var session = new EditorSession(project, registry);

        _context = new EditorContext
        {
            Window = window,
            Device = device,
            Renderer = renderer,
            Resources = resources,
            SceneRenderer = new SceneRenderer(renderer, resources, _logger),
            ImGui = new ImGuiRenderer(device, renderer.DefaultMaterial, font, 17f),
            Session = session,
            PlayMode = new PlayModeController(session, loggerFactory),
            Logs = logs,
            Logger = _logger,
        };

        Gui.ApplyDarkTheme();
        _iniPath = Path.Combine(project.RootDirectory, "Library", "Editor", "layout.ini");
        if (File.Exists(_iniPath))
        {
            ImGui.LoadIniSettingsFromDisk(_iniPath);
        }

        _hierarchy = new HierarchyPanel(_context);
        _inspector = new InspectorPanel(_context);
        _sceneView = new SceneViewPanel(_context);
        _gameView = new GameViewPanel(_context);
        _projectPanel = new ProjectPanel(_context);
        _console = new ConsolePanel(_context);

        TryOpenScene(project.StartupScene);
        window.CloseRequested = OnCloseRequested;
        if (options.SelectOnStart is { } name)
        {
            _context.Session.Selection.Select(_context.Session.Scene.Find(name));
        }

        if (options.PlayOnStart)
        {
            TogglePlay();
        }
    }

    public void FixedUpdate(float fixedDeltaTime)
    {
        // Play Mode keeps its own clock and fixed-step accumulator; see PlayModeController.Tick.
    }

    public void Update(FrameTime time)
    {
        ImGuiInput.NewFrame(_context.Window, time.UnscaledDeltaTime);
        ImGui.NewFrame();

        _gameView.FeedInput();
        _context.PlayMode.Tick(time.UnscaledTotalTime - _lastTick);
        _lastTick = time.UnscaledTotalTime;

        DrawWorkspace();
        _hierarchy.Draw();
        _inspector.Draw();
        _sceneView.Draw();
        _gameView.Draw();
        _projectPanel.Draw();
        _console.Draw();
        _gameView.ApplyPendingFocus();
        DrawDialogs();
        HandleShortcuts();

        if (_context.OpenSceneRequest is { } request)
        {
            _context.OpenSceneRequest = null;
            GuardUnsaved(() => TryOpenScene(request));
        }

        ImGui.Render();

        var io = ImGui.GetIO();
        if (io.WantSaveIniSettings)
        {
            SaveLayout();
            io.WantSaveIniSettings = false;
        }

        var state = _context.PlayMode.State == PlayState.Editing ? "" : $" [{_context.PlayMode.State}]";
        _context.Window.Title = $"MNVTEngine Editor - {project.Name} - {_context.Session.DisplayName}{state}";
    }

    public void Render(FrameTime time, float interpolation)
    {
        _sceneView.Render();
        _gameView.Render();

        _context.Device.SetRenderTarget(null);
        _context.Device.Clear(new Color(0.08f, 0.08f, 0.09f));
        _context.ImGui.Render(ImGui.GetDrawData(), _context.Window.FramebufferWidth, _context.Window.FramebufferHeight);

        _frames++;
        if (options.ExitAfterFrames is { } limit && _frames >= limit)
        {
            if (options.ScreenshotPath is { } path)
            {
                SaveScreenshot(path);
            }

            _context.Window.Close();
        }
    }

    public void Resize(int width, int height) => _context.Device.ResizeBackBuffer(width, height);

    public void Unload()
    {
        SaveLayout();
        _context.PlayMode.Stop();
        _sceneView.Dispose();
        _gameView.Dispose();
        _context.ImGui.Dispose();
        _context.Resources.Dispose();
        _context.Renderer.Dispose();
        if (_context.Device.Resources.LiveCount > 0)
        {
            ExitCode = 1;
            LogLeaks(_logger, _context.Device.Resources.LiveCount);
        }

        _context.Device.Dispose();
    }

    private void DrawWorkspace()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new SysVector2(4f, 4f));
        const ImGuiWindowFlags Flags = ImGuiWindowFlags.MenuBar | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus;
        ImGui.Begin("##Workspace", Flags);
        ImGui.PopStyleVar(3);

        DrawMenuBar();
        DrawToolbar();

        var dockId = ImGui.GetID(DockSpaceName);
        var size = ImGui.GetContentRegionAvail();
        if (_resetLayout || DockBuilder.GetNode(dockId) == 0)
        {
            _resetLayout = false;
            BuildDefaultLayout(dockId, size);
        }

        ImGui.DockSpace(dockId, size);
        ImGui.End();
    }

    private static void BuildDefaultLayout(uint dockId, SysVector2 size)
    {
        DockBuilder.RemoveNode(dockId);
        _ = DockBuilder.AddNode(dockId, DockBuilder.DockSpaceFlag);
        DockBuilder.SetNodeSize(dockId, size);
        DockBuilder.SplitNode(dockId, DockBuilder.Direction.Left, 0.18f, out var left, out var rest);
        DockBuilder.SplitNode(rest, DockBuilder.Direction.Right, 0.28f, out var right, out rest);
        DockBuilder.SplitNode(rest, DockBuilder.Direction.Down, 0.30f, out var bottom, out var center);
        DockBuilder.DockWindow("Hierarchy", left);
        DockBuilder.DockWindow("Inspector", right);
        DockBuilder.DockWindow("Project", bottom);
        DockBuilder.DockWindow("Console", bottom);
        DockBuilder.DockWindow("Game", center);
        DockBuilder.DockWindow("Scene", center);
        DockBuilder.Finish(dockId);
    }

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMenuBar())
        {
            return;
        }

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New Scene", "Ctrl+N"))
            {
                GuardUnsaved(() => _context.Session.NewScene());
            }

            if (ImGui.MenuItem("Open Scene...", "Ctrl+O"))
            {
                _openScenePicker = true;
            }

            if (ImGui.MenuItem("Save Scene", "Ctrl+S", false, !_context.PlayMode.IsPlaying))
            {
                Save();
            }

            if (ImGui.MenuItem("Save Scene As...", null, false, !_context.PlayMode.IsPlaying))
            {
                _openSaveAs = true;
            }

            ImGui.Separator();
            if (ImGui.MenuItem("Exit"))
            {
                GuardUnsaved(_context.Window.Close);
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("GameObject"))
        {
            _hierarchy.DrawCreateMenu(_context.Selected?.Transform);
            ImGui.Separator();
            if (ImGui.MenuItem("Delete", "Del", false, _context.Selected is not null))
            {
                _context.Delete(_context.Selected!);
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Window"))
        {
            if (ImGui.MenuItem("Reset Layout"))
            {
                _resetLayout = true;
            }

            ImGui.EndMenu();
        }

        ImGui.EndMenuBar();
    }

    private void DrawToolbar()
    {
        var play = _context.PlayMode;
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX((width * 0.5f) - 110f);

        if (play.IsPlaying)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.26f, 0.42f, 0.64f, 1f));
        }

        if (ImGui.Button(play.IsPlaying ? "Stop" : "Play", new SysVector2(70f, 0f)))
        {
            TogglePlay();
        }

        if (play.IsPlaying)
        {
            ImGui.PopStyleColor();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!play.IsPlaying);
        if (ImGui.Button(play.State == PlayState.Paused ? "Resume" : "Pause", new SysVector2(70f, 0f)))
        {
            play.TogglePause();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(play.State != PlayState.Paused);
        if (ImGui.Button("Step", new SysVector2(60f, 0f)))
        {
            play.Step();
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();

        if (play.IsPlaying)
        {
            ImGui.SameLine();
            Gui.Muted($"t = {play.Time.TotalTime:0.00}s  (changes made while playing are discarded on Stop)");
        }
    }

    private void DrawDialogs()
    {
        if (_openUnsavedPrompt)
        {
            ImGui.OpenPopup("Unsaved changes");
            _openUnsavedPrompt = false;
        }

        if (_openSaveAs)
        {
            _saveAsPath = _context.Session.ScenePath ?? $"Scenes/{_context.Session.Scene.Name}.mnvt";
            ImGui.OpenPopup("Save scene as");
            _openSaveAs = false;
        }

        if (_openScenePicker)
        {
            ImGui.OpenPopup("Open scene");
            _openScenePicker = false;
        }

        var open = true;
        if (ImGui.BeginPopupModal("Unsaved changes", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted($"'{_context.Session.DisplayName.TrimEnd('*')}' has unsaved changes.");
            if (ImGui.Button("Save", new SysVector2(110f, 0f)))
            {
                if (Save())
                {
                    RunPending();
                }

                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Don't Save", new SysVector2(110f, 0f)))
            {
                RunPending();
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new SysVector2(110f, 0f)))
            {
                _afterUnsavedPrompt = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        open = true;
        if (ImGui.BeginPopupModal("Save scene as", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Path relative to Assets:");
            ImGui.SetNextItemWidth(360f);
            ImGui.InputText("##path", ref _saveAsPath, 260);
            if (ImGui.Button("Save", new SysVector2(110f, 0f)))
            {
                var path = _saveAsPath.EndsWith(".mnvt", StringComparison.OrdinalIgnoreCase) ? _saveAsPath : _saveAsPath + ".mnvt";
                if (Save(path))
                {
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new SysVector2(110f, 0f)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        open = true;
        if (ImGui.BeginPopupModal("Open scene", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var scenes = Directory.EnumerateFiles(project.AssetsDirectory, "*.mnvt", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(project.AssetsDirectory, f).Replace('\\', '/'))
                .Order(StringComparer.OrdinalIgnoreCase);
            foreach (var scene in scenes)
            {
                if (ImGui.Selectable(scene))
                {
                    _context.OpenSceneRequest = scene;
                    ImGui.CloseCurrentPopup();
                }
            }

            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void HandleShortcuts()
    {
        var io = ImGui.GetIO();
        if (!io.KeyCtrl || io.WantTextInput)
        {
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.S, false) && !_context.PlayMode.IsPlaying)
        {
            Save();
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.N, false))
        {
            GuardUnsaved(() => _context.Session.NewScene());
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.O, false))
        {
            _openScenePicker = true;
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.P, false))
        {
            TogglePlay();
        }
    }

    private void TogglePlay()
    {
        if (_context.PlayMode.IsPlaying)
        {
            _context.PlayMode.Stop();
            return;
        }

        _context.PlayMode.Play();
        _gameView.Focus();
    }

    /// <summary>Runs <paramref name="action"/> now, or after the user resolves unsaved changes.</summary>
    private void GuardUnsaved(Action action)
    {
        _context.PlayMode.Stop();
        if (!_context.Session.IsDirty)
        {
            action();
            return;
        }

        _afterUnsavedPrompt = action;
        _openUnsavedPrompt = true;
    }

    private void RunPending()
    {
        var action = _afterUnsavedPrompt;
        _afterUnsavedPrompt = null;
        action?.Invoke();
    }

    private bool OnCloseRequested()
    {
        if (!_context.Session.IsDirty)
        {
            return true;
        }

        GuardUnsaved(_context.Window.Close);
        return false;
    }

    private bool Save(string? path = null)
    {
        if (path is null && _context.Session.ScenePath is null)
        {
            _openSaveAs = true;
            return false;
        }

        try
        {
            _context.Session.SaveScene(path);
            LogSaved(_logger, _context.Session.ScenePath!);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            LogSaveFailed(_logger, ex, path ?? _context.Session.ScenePath ?? "?");
            return false;
        }
    }

    private void TryOpenScene(string path)
    {
        try
        {
            var result = _context.Session.OpenScene(path);
            foreach (var diagnostic in result.Diagnostics)
            {
                LogDiagnostic(_logger, diagnostic.Severity == DiagnosticSeverity.Error ? LogLevel.Error : LogLevel.Warning, path, diagnostic.Path, diagnostic.Message);
            }

            LogOpened(_logger, path, result.Scene.GameObjects.Count);
        }
        catch (SerializationException ex)
        {
            LogOpenFailed(_logger, ex, path);
        }
    }

    private void SaveLayout()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_iniPath)!);
        ImGui.SaveIniSettingsToDisk(_iniPath);
    }

    private void SaveScreenshot(string path)
    {
        var device = _context.Device;
        device.SetRenderTarget(null);
        var pixels = new byte[device.BackBufferWidth * device.BackBufferHeight * 4];
        device.ReadPixels(pixels);
        using var stream = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(pixels, device.BackBufferWidth, device.BackBufferHeight, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
    }

    [LoggerMessage(EventId = 5100, Level = LogLevel.Information, Message = "Opened '{Scene}' ({Count} objects)")]
    private static partial void LogOpened(ILogger logger, string scene, int count);

    [LoggerMessage(EventId = 5101, Level = LogLevel.Error, Message = "Could not open '{Scene}'")]
    private static partial void LogOpenFailed(ILogger logger, Exception exception, string scene);

    [LoggerMessage(EventId = 5102, Message = "{Scene}: {Path}: {Message}")]
    private static partial void LogDiagnostic(ILogger logger, LogLevel level, string scene, string path, string message);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Information, Message = "Saved '{Scene}'")]
    private static partial void LogSaved(ILogger logger, string scene);

    [LoggerMessage(EventId = 5104, Level = LogLevel.Error, Message = "Could not save '{Scene}'")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string scene);

    [LoggerMessage(EventId = 5105, Level = LogLevel.Error, Message = "{Count} GPU resources leaked")]
    private static partial void LogLeaks(ILogger logger, int count);
}
