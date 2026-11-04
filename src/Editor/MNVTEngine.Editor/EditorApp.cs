using System.Diagnostics.CodeAnalysis;
using ImGuiNET;
using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using MNVTEngine.Core.Timing;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Panels;
using MNVTEngine.Editor.Services;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Mathematics;
using MNVTEngine.Platform;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;
using SysVector2 = System.Numerics.Vector2;

namespace MNVTEngine.Editor;

/// <param name="FocusWindow">Panel brought to the front on start (e.g. "Profiler"), for automated screenshots.</param>
/// <param name="Breakpoints">Script breakpoints set on start, as "path/relative/to/Assets.mnvts:line".</param>
/// <param name="EditFile">File opened in the Script Editor on start (absolute, or relative to the project folder).</param>
internal sealed record EditorOptions(int? ExitAfterFrames, string? ScreenshotPath, bool PlayOnStart, string? SelectOnStart = null, string? FocusWindow = null, IReadOnlyList<string>? Breakpoints = null, string? EditFile = null);

/// <summary>
/// The editor application: dockable workspace (menu, toolbar, panels), scene file commands with unsaved-change
/// protection, and Play Mode. UI is built in <see cref="Update"/>; views and ImGui are drawn in <see cref="Render"/>.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "GPU and ImGui resources live from IGameLoop.Load to IGameLoop.Unload.")]
internal sealed partial class EditorApp(GameProject project, ComponentRegistry registry, ILoggerFactory loggerFactory, InMemoryLoggerProvider logs, LogHub logHub, EditorOptions options) : IGameLoop
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
    private ProfilerPanel _profilerPanel = null!;
    private ScriptDebuggerPanel _scriptDebugger = null!;
    private BuildController _build = null!;
    private ScriptEditorPanel _scriptEditor = null!;
    private bool _showBuildSettings;
    private DiagnosticsSettingsWindow _diagnosticsWindow = null!;
    private int _focusSceneFrames;
    private readonly Core.Diagnostics.FrameProfiler _profiler = new();
    private ScriptReloadController _scripts = null!;
    private string _iniPath = "";
    private bool _resetLayout;
    private Action? _afterUnsavedPrompt;
    private bool _openUnsavedPrompt;
    private bool _openSaveAs;
    private bool _openScenePicker;
    private string _saveAsPath = "Scenes/New.mnvt";
    private int _frames;
    private double _lastTick;
    private double _lastAssetRefresh;
    private bool _playWhenScriptsReady;
    private string? _startupScene;
    private string? _selectOnStart;
    private string _lastAssetDiagnostics = "";

    public int ExitCode { get; private set; }

    public void Load(GameWindow window)
    {
        var device = VulkanGraphicsDevice.Create(window.VulkanSurface, window.FramebufferWidth, window.FramebufferHeight, loggerFactory.CreateLogger("Graphics"), VulkanDeviceOptions.ForApplication("Editor", window.Options.VSync, window.Options.GraphicsValidation));
        var renderer = new Renderer2D(device);
        var session = new EditorSession(project, registry, loggerFactory.CreateLogger("Assets"));
        var resources = new ResourceCache(device, session.Assets, loggerFactory.CreateLogger("Resources"));
        var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");

        _context = new EditorContext
        {
            Window = window,
            Device = device,
            Renderer = renderer,
            Resources = resources,
            SceneRenderer = new SceneRenderer(renderer, resources, _logger),
            ImGui = new ImGuiRenderer(device, renderer.DefaultMaterial, font, 17f),
            Session = session,
            PlayMode = new PlayModeController(session, loggerFactory) { Audio = AudioDevices.CreateOrSilent(Audio.OpenAL.OpenALAudioDevice.Create, loggerFactory.CreateLogger("Audio")) },
            Logs = logs,
            DiagnosticsSettings = project.Diagnostics.Clone(),
            LogHub = logHub,
            Logger = _logger,
        };

        // Play sessions get the same diagnostics as a development build; they share the editor's profiler.
        _context.PlayMode.CreateDiagnostics = () =>
        {
            var diagnostics = new RuntimeDiagnostics(_context.DiagnosticsSettings.Clone(), loggerFactory.CreateLogger("Diagnostics"),
                Path.Combine(project.RootDirectory, "Library", "Diagnostics", "Profiles"), _profiler, ownsProfilerFrames: false);
            diagnostics.Attach(new DiagnosticsTarget(device, () => _context.PlayMode.PlayScene, resources, session.Assets, _context.SceneRenderer));
            return diagnostics;
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
        _profilerPanel = new ProfilerPanel(_context, _profiler);
        _scriptDebugger = new ScriptDebuggerPanel(_context);
        _build = new BuildController(project, loggerFactory);
        _showBuildSettings = options.FocusWindow == "Build Settings";
        _diagnosticsWindow = new DiagnosticsSettingsWindow(_context) { Open = options.FocusWindow == "Diagnostics" };
        foreach (var breakpoint in options.Breakpoints ?? [])
        {
            var separator = breakpoint.LastIndexOf(':');
            if (separator > 0 && int.TryParse(breakpoint[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture, out var line)
                && _context.Session.Assets.TryGetGuid(breakpoint[..separator], out var script))
            {
                _context.ScriptDebugger.ToggleBreakpoint(script, line);
            }
            else
            {
                EditorLog.Warning(_logger, $"Ignoring --break '{breakpoint}': expected an existing script asset and a line, e.g. Missions/Intro.mnvts:12.");
            }
        }
        Core.Diagnostics.Profiler.Current = _profiler;

        LogAssetDiagnostics();
        _scripts = new ScriptReloadController(session, _context.PlayMode, loggerFactory.CreateLogger("Scripts"));

        // Opened once the first script load settles, so components resolve to real types instead of MissingComponent.
        _startupScene = project.StartupScene;
        _scripts.Reloaded += _inspector.ClearCaches;
        _scriptEditor = new ScriptEditorPanel(_context, _scripts);
        if (options.EditFile is { } file)
        {
            _context.OpenScriptRequest = Path.IsPathRooted(file) ? file : Path.Combine(project.RootDirectory, file);
        }
        window.CloseRequested = OnCloseRequested;
        _selectOnStart = options.SelectOnStart;

        if (options.PlayOnStart)
        {
            _playWhenScriptsReady = true;
        }
    }

    public void FixedUpdate(float fixedDeltaTime)
    {
        // Play Mode keeps its own clock and fixed-step accumulator; see PlayModeController.Tick.
    }

    public void Update(FrameTime time)
    {
        _profiler.EndFrame();
        _profiler.BeginFrame();
        RefreshAssetsPeriodically(time);
        _scripts.Update(DateTime.UtcNow);
        if (_startupScene is { } startup && !_scripts.IsBusy)
        {
            _startupScene = null;
            TryOpenScene(startup);
            if (_selectOnStart is { } name)
            {
                SelectByName(name);
            }
        }

        if (_playWhenScriptsReady && _startupScene is null && _scripts.CanEnterPlayMode)
        {
            _playWhenScriptsReady = false;
            TogglePlay();
        }

        _context.ImGui.PruneDisposed();
        ImGuiInput.NewFrame(_context.Window, time.UnscaledDeltaTime);
        ImGui.NewFrame();

        _gameView.FeedInput();
        // The script debugger hooks new mission runners before they run, and freezes the game while it shows a stop.
        if (_context.PlayMode.PlayScene is { } playing)
        {
            _context.ScriptDebugger.Attach(playing);
        }
        else
        {
            _context.ScriptDebugger.Reset();
        }

        if (_context.ScriptDebugger.Break is null)
        {
            _context.PlayMode.Tick(time.UnscaledTotalTime - _lastTick);
        }

        _lastTick = time.UnscaledTotalTime;

        DrawWorkspace();
        _hierarchy.Draw();
        _inspector.Draw();
        _sceneView.Draw();
        _gameView.Draw();
        _projectPanel.Draw();
        _console.Draw();
        _profilerPanel.Draw();
        _build.Update();
        DrawBuildSettings();
        _diagnosticsWindow.Draw();
        _scriptDebugger.Draw();
        if (_context.OpenScriptRequest is { } script)
        {
            _context.OpenScriptRequest = null;
            _scriptEditor.Open(script);
        }

        _scriptEditor.Draw(time.UnscaledTotalTime);
        if (options.FocusWindow is { } focus && _frames == 10) // after the dock layout and Play Mode focus settle
        {
            ImGui.SetWindowFocus(focus);
        }
        if (_focusSceneFrames > 0)
        {
            _focusSceneFrames--;
            ImGui.SetWindowFocus("Scene");
        }

        _gameView.ApplyPendingFocus();
        _scriptDebugger.ApplyFocus();
        DrawDialogs();
        HandleShortcuts();

        // A drag, a typed value or a paint stroke becomes one undo step once the interaction is over.
        if (!_context.PlayMode.IsPlaying && !ImGui.IsAnyItemActive() && !ImGui.IsAnyMouseDown())
        {
            _context.Session.History.Commit();
        }

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
        using (Core.Diagnostics.Profiler.Sample("Editor.SceneView"))
        {
            _sceneView.Render();
        }

        using (Core.Diagnostics.Profiler.Sample("Editor.GameView"))
        {
            _gameView.Render();
        }

        _gameView.EndDiagnosticsFrame();

        _context.Device.SetRenderTarget(null);
        _context.Device.Clear(new Color(0.08f, 0.08f, 0.09f));
        _context.ImGui.Render(ImGui.GetDrawData(), _context.Window.FramebufferWidth, _context.Window.FramebufferHeight);

        _frames++;
        var exiting = options.ExitAfterFrames is { } limit && _frames >= limit;
        if (exiting && options.ScreenshotPath is { } path)
        {
            SaveScreenshot(path); // before presenting: the back buffer is gone afterwards
        }

        _context.Device.Present();
        if (exiting)
        {
            _context.Window.Close();
        }
    }

    public void Resize(int width, int height) => _context.Device.ResizeBackBuffer(width, height);

    public void Unload()
    {
        if (_context is null)
        {
            return; // the graphics device could not be created
        }

        SaveLayout();
        _context.PlayMode.Stop();
        _scripts.Dispose();
        _sceneView.Dispose();
        _gameView.Dispose();
        _context.ImGui.Dispose();
        _context.SceneRenderer.Dispose();
        _context.Resources.Dispose();
        _context.Renderer.Dispose();
        if (_context.Device.Resources.LiveCount > 0)
        {
            ExitCode = 1;
            LogLeaks(_logger, _context.Device.Resources.LiveCount);
        }

        _context.Device.Dispose();
        _context.PlayMode.Audio?.Dispose();
    }

    /// <summary>Picks up files added or changed outside the editor; logs asset problems when they change.</summary>
    private void RefreshAssetsPeriodically(FrameTime time)
    {
        if (time.UnscaledTotalTime - _lastAssetRefresh < 2.0)
        {
            return;
        }

        _lastAssetRefresh = time.UnscaledTotalTime;
        var result = _context.Session.Assets.Refresh();
        foreach (var guid in result.Added.Concat(result.Reimported))
        {
            EditorLog.Imported(_logger, _context.Session.Assets.Get(guid)?.Path);
        }

        _context.PlayMode.ReloadMissionScripts(result.Reimported); // live .mnvts edits while playing

        LogAssetDiagnostics();
    }

    private void LogAssetDiagnostics()
    {
        var current = string.Join('\n', _context.Session.Assets.Diagnostics);
        if (current == _lastAssetDiagnostics)
        {
            return;
        }

        _lastAssetDiagnostics = current;
        foreach (var diagnostic in _context.Session.Assets.Diagnostics)
        {
            EditorLog.AssetDiagnostic(_logger, diagnostic.Severity == DiagnosticSeverity.Error ? LogLevel.Error : LogLevel.Warning, diagnostic.Path, diagnostic.Message);
        }
    }

    /// <summary>Selects an object in the edit scene by name, or an asset by path.</summary>
    private void SelectByName(string name)
    {
        if (_context.Session.Scene.Find(name) is { } gameObject)
        {
            _context.Session.Selection.Select(gameObject);
        }
        else if (_context.Session.Assets.TryGetGuid(name, out var asset))
        {
            _context.Session.Selection.SelectAsset(asset);
        }
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
            _focusSceneFrames = 2; // the last panel drawn would otherwise be the selected tab
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
        DockBuilder.DockWindow("Profiler", bottom);
        DockBuilder.DockWindow("Script Debugger", bottom);
        DockBuilder.DockWindow("Game", center);
        DockBuilder.DockWindow("Script Editor", center);
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
            if (ImGui.MenuItem("Project Hub...", null, false))
            {
                OpenProjectHub();
            }

            ImGui.Separator();
            if (ImGui.MenuItem("Exit"))
            {
                GuardUnsaved(_context.Window.Close);
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Edit"))
        {
            var history = _context.Session.History;
            var editing = !_context.PlayMode.IsPlaying;
            if (ImGui.MenuItem(history.UndoLabel is { } undo ? $"Undo {undo}" : "Undo", "Ctrl+Z", false, editing && history.CanUndo))
            {
                history.Undo();
            }

            if (ImGui.MenuItem(history.RedoLabel is { } redo ? $"Redo {redo}" : "Redo", "Ctrl+Y", false, editing && history.CanRedo))
            {
                history.Redo();
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

        if (ImGui.BeginMenu("Build"))
        {
            var idle = !_build.IsBusy;
            if (ImGui.MenuItem("Build Settings..."))
            {
                _showBuildSettings = true;
            }

            ImGui.Separator();
            if (ImGui.MenuItem($"Build ({_build.Profile})", "Ctrl+B", false, idle))
            {
                StartBuild(run: false);
            }

            if (ImGui.MenuItem($"Build and Run ({_build.Profile})", "Ctrl+Shift+B", false, idle))
            {
                StartBuild(run: true);
            }

            if (ImGui.MenuItem("Run Last Build", null, false, idle) && !_build.RunLastBuild())
            {
                EditorLog.Warning(_logger, $"Profile '{_build.Profile}' has not been built yet.");
            }

            if (ImGui.MenuItem("Validate Project", null, false, idle))
            {
                GuardUnsaved(_build.StartValidation);
            }

            ImGui.Separator();
            if (ImGui.MenuItem("Open Build Folder", null, false, Directory.Exists(_build.OutputDirectory)))
            {
                OpenFolder(_build.OutputDirectory);
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Window"))
        {
            if (ImGui.MenuItem("Diagnostics Settings", null, _diagnosticsWindow.Open))
            {
                _diagnosticsWindow.Open = !_diagnosticsWindow.Open;
            }

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

        // The button toggles Play Mode, so IsPlaying may differ after it: the scope remembers whether it pushed.
        using (Gui.StyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.26f, 0.42f, 0.64f, 1f), play.IsPlaying))
        {
            if (ImGui.Button(play.IsPlaying ? "Stop" : "Play", new SysVector2(70f, 0f)))
            {
                TogglePlay();
            }
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

        ImGui.SameLine(ImGui.GetWindowWidth() - 300f);
        var color = _scripts.State switch
        {
            ScriptState.Failed => new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f),
            ScriptState.Compiling => new System.Numerics.Vector4(1f, 0.8f, 0.4f, 1f),
            _ => new System.Numerics.Vector4(0.55f, 0.57f, 0.62f, 1f),
        };
        if (_build.Status is { } building)
        {
            Gui.TextColored(new System.Numerics.Vector4(1f, 0.8f, 0.4f, 1f), building);
            ImGui.SameLine();
        }

        Gui.TextColored(color, _scripts.StatusText);
        if (_scripts.State != ScriptState.None)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Recompile"))
            {
                _scripts.Rebuild();
            }
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
        if (_context.ScriptDebugger.Break is not null && !io.WantTextInput)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.F5, false))
            {
                _context.ScriptDebugger.Continue();
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.F10, false))
            {
                _context.ScriptDebugger.Step();
            }
        }

        // Ctrl+S in the Script Editor saves the file, even while its text box has keyboard focus.
        if (io.KeyCtrl && _scriptEditor.IsFocused && ImGui.IsKeyPressed(ImGuiKey.S, false))
        {
            _scriptEditor.SaveActive();
            _lastAssetRefresh = double.MinValue; // pick the change up next frame, not in up to 2 s
            return;
        }

        if (!io.KeyCtrl || io.WantTextInput)
        {
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.S, false) && !_context.PlayMode.IsPlaying)
        {
            Save();
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.B, false) && !_build.IsBusy)
        {
            StartBuild(run: io.KeyShift);
        }
        else if (!_context.PlayMode.IsPlaying && (ImGui.IsKeyPressed(ImGuiKey.Y, false) || (io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Z, false))))
        {
            _context.Session.History.Redo();
        }
        else if (!_context.PlayMode.IsPlaying && ImGui.IsKeyPressed(ImGuiKey.Z, false))
        {
            _context.Session.History.Undo();
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

        if (!_scripts.CanEnterPlayMode)
        {
            EditorLog.Warning(_logger, "Cannot enter Play Mode: " + _scripts.StatusText);
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

    private void SaveScreenshot(string path) => Screenshot.Save(_context.Device, path);

    /// <summary>Builds what is on disk, so unsaved scene changes are resolved first (Save / Don't Save / Cancel).</summary>
    private void StartBuild(bool run) => GuardUnsaved(() => _build.StartBuild(run));

    private void DrawBuildSettings()
    {
        if (!_showBuildSettings)
        {
            return;
        }

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, 0), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.FirstUseEver, new System.Numerics.Vector2(0.5f, 0.5f));
        if (ImGui.Begin("Build Settings", ref _showBuildSettings, ImGuiWindowFlags.NoDocking))
        {
            var profiles = project.EffectiveBuildProfiles;
            var names = profiles.Select(p => p.Name).ToArray();
            var index = Math.Max(0, Array.IndexOf(names, _build.Profile));
            if (ImGui.Combo("Profile", ref index, names, names.Length))
            {
                _build.Profile = names[index];
            }

            var profile = profiles[index];
            Gui.Muted($"{profile.Configuration}, {profile.Target}, console window: {(profile.ShowsConsole ? "yes" : "no")}");
            Gui.Muted($"Output: {_build.OutputDirectory}");
            Gui.Muted("Profiles are defined in the project file (\"buildProfiles\"); see docs/build-system/building.md.");
            ImGui.Spacing();
            ImGui.BeginDisabled(_build.IsBusy);
            if (ImGui.Button("Build"))
            {
                StartBuild(run: false);
            }

            ImGui.SameLine();
            if (ImGui.Button("Build and Run"))
            {
                StartBuild(run: true);
            }

            ImGui.SameLine();
            if (ImGui.Button("Validate"))
            {
                GuardUnsaved(_build.StartValidation);
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Open Folder") && Directory.Exists(_build.OutputDirectory))
            {
                OpenFolder(_build.OutputDirectory);
            }

            if (_build.Status is { } status)
            {
                Gui.TextColored(new System.Numerics.Vector4(1f, 0.8f, 0.4f, 1f), status + " - progress in the Console");
            }
            else if (_build.LastBuild is { } last)
            {
                var errors = last.Diagnostics.Count(d => d.Severity == Build.BuildSeverity.Error);
                var warnings = last.Diagnostics.Count(d => d.Severity == Build.BuildSeverity.Warning);
                Gui.TextColored(
                    last.Succeeded ? new System.Numerics.Vector4(0.5f, 0.9f, 0.5f, 1f) : new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f),
                    last.Succeeded ? $"Last build succeeded in {last.Duration.TotalSeconds:0.0} s ({warnings} warnings)" : $"Last build failed: {errors} errors, {warnings} warnings (see the Console)");
            }
        }

        ImGui.End();
    }

    /// <summary>Starts another editor process without a project: the Project Hub (this editor keeps running).</summary>
    private void OpenProjectHub()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            EditorLog.Warning(_logger, "Could not start the Project Hub: " + ex.Message);
        }
    }

    private void OpenFolder(string folder)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            EditorLog.Warning(_logger, $"Could not open {folder}: {ex.Message}");
        }
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

/// <summary>Writes the back buffer to a PNG (automation: <c>--screenshot</c>).</summary>
internal static class Screenshot
{
    public static void Save(IGraphicsDevice device, string path)
    {
        device.SetRenderTarget(null);
        var pixels = new byte[device.BackBufferWidth * device.BackBufferHeight * 4];
        device.ReadPixels(pixels);
        using var stream = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(pixels, device.BackBufferWidth, device.BackBufferHeight, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
    }
}
