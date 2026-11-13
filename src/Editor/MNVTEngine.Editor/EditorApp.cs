using System.Diagnostics.CodeAnalysis;
using ImGuiNET;
using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using MNVTEngine.Core.Timing;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Panels;
using MNVTEngine.Build;
using MNVTEngine.Editor.Services;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Mathematics;
using MNVTEngine.Platform;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;
using SysVector2 = System.Numerics.Vector2;
using MNVTEngine.Graphics.Vulkan.ShaderCompilation;

namespace MNVTEngine.Editor;

/// <param name="FocusWindow">Panel brought to the front on start (e.g. "Profiler"), for automated screenshots.</param>
/// <param name="Breakpoints">Script breakpoints set on start, as "path/relative/to/Assets.mnvts:line".</param>
/// <param name="EditFile">File opened in the Script Editor on start (absolute, or relative to the project folder).</param>
/// <param name="BuildProfile">Builds the game with this profile once the editor has started (automation, as Build > Build does).</param>
internal sealed record EditorOptions(int? ExitAfterFrames, string? ScreenshotPath, bool PlayOnStart, string? SelectOnStart = null, string? FocusWindow = null, IReadOnlyList<string>? Breakpoints = null, string? EditFile = null, string? BuildProfile = null);

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
    private NativeMenuBar? _nativeMenu;
    private InspectorPanel _inspector = null!;
    private SceneViewPanel _sceneView = null!;
    private TerrainPanel? _terrainPanel; // 3D projects only
    private GameViewPanel _gameView = null!;
    private ProjectPanel _projectPanel = null!;
    private ConsolePanel _console = null!;
    private ProfilerPanel _profilerPanel = null!;
    private MixerPanel _mixerPanel = null!;
    private ScriptDebuggerPanel _scriptDebugger = null!;
    private BuildController _build = null!;
    private ScriptEditorPanel _scriptEditor = null!;
    private UIEditorPanel _uiEditor = null!;
    private AnimatorEditorPanel _animator = null!;
    private bool _showBuildSettings;
    private DiagnosticsSettingsWindow _diagnosticsWindow = null!;
    private int _focusSceneFrames;
    private readonly Core.Diagnostics.FrameProfiler _profiler = new();
    private ScriptReloadController _scripts = null!;
    private Graphics.Text.Font _gameUIFont = null!;
    private string _iniPath = "";
    private bool _resetLayout;
    private EditorLayout _layoutKind;
    private string? _pendingLayoutIni;
    private string _newLayoutName = "";
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
        var device = VulkanGraphicsDevice.Create(window.VulkanSurface, window.FramebufferWidth, window.FramebufferHeight, loggerFactory.CreateLogger("Graphics"), VulkanDeviceOptions.ForApplication("Editor", window.Options.VSync, window.Options.GraphicsValidation).WithShaderc());
        var renderer = new Renderer2D(device);
        var session = new EditorSession(project, registry, loggerFactory.CreateLogger("Assets"));
        var resources = new ResourceCache(device, session.Assets, loggerFactory.CreateLogger("Resources"));
        var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");

        // One set of module instances for the renderer's passes, diagnostics and Play Mode: modules keep per-host state
        // (the browser module's service and textures, world.3d's model data) that all three must share.
        var modules = EngineModuleSet.ForProfile(project.Profile!.Value);
        _context = new EditorContext
        {
            Window = window,
            Device = device,
            Renderer = renderer,
            Resources = resources,
            SceneRenderer = new SceneRenderer(renderer, resources, _logger, ModuleIntegration.CreatePasses(modules, renderer, resources, loggerFactory)),
            ImGui = new ImGuiRenderer(device, renderer.DefaultMaterial, font, 17f, iconFont: Icons.Font),
            GameUI = new UI.UIRenderer(device, renderer.DefaultMaterial),
            Session = session,
            PlayMode = new PlayModeController(session, loggerFactory) { Audio = Audio.AudioDevices.CreateOrSilent(Audio.OpenAL.OpenALAudioDevice.Create, loggerFactory.CreateLogger("Audio")), Modules = modules },
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
            diagnostics.Attach(new DiagnosticsTarget(device, () => _context.PlayMode.PlayScene, resources, session.Assets, _context.SceneRenderer, modules));
            ModuleIntegration.AddDiagnostics(modules, diagnostics.Collector, diagnostics.Commands, () => _context.PlayMode.PlayScene);
            return diagnostics;
        };

        _gameUIFont = Graphics.Text.Font.LoadDefault();
        var gameUIServices = new UI.UIServices(_gameUIFont, new UI.WindowClipboard(window), asset => session.Assets.ReadAllBytes(asset.Guid), asset => resources.GetTexture(asset));
        _context.PlayMode.UIServices = gameUIServices;

        Gui.ApplyDarkTheme();
        _iniPath = Path.Combine(project.RootDirectory, "Library", "Editor", "layout.ini");
        LoadSceneVisibility(Path.Combine(project.RootDirectory, "Library", "Editor", "scene-visibility.json"));
        if (File.Exists(_iniPath))
        {
            ImGui.LoadIniSettingsFromMemory(EditorWindows.MigrateLayout(File.ReadAllText(_iniPath)));
        }

        _hierarchy = new HierarchyPanel(_context);
        _inspector = new InspectorPanel(_context);
        _sceneView = new SceneViewPanel(_context);
        if (project.Profile == Modules.ProjectProfile.ThreeD)
        {
            CreateTerrainTools();
        }
        _gameView = new GameViewPanel(_context);
        _projectPanel = new ProjectPanel(_context);
        _console = new ConsolePanel(_context);
        _profilerPanel = new ProfilerPanel(_context, _profiler);
        _mixerPanel = new MixerPanel(_context);
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
        _uiEditor = new UIEditorPanel(_context, gameUIServices);
        _animator = new AnimatorEditorPanel(_context);
        if (options.EditFile is { } file)
        {
            var full = Path.IsPathRooted(file) ? file : Path.Combine(project.RootDirectory, file);
            if (full.EndsWith(UI.UILayoutSerializer.Extension, StringComparison.OrdinalIgnoreCase))
            {
                _context.OpenUILayoutRequest = full;
            }
            else if (full.EndsWith(AnimatorControllerDefinition.Extension, StringComparison.OrdinalIgnoreCase))
            {
                _context.OpenAnimatorRequest = full;
            }
            else if (full.EndsWith(".mnvtterrain", StringComparison.OrdinalIgnoreCase)
                && _context.Session.Assets.TryGetGuid(Path.GetRelativePath(_context.Session.Assets.AssetsDirectory, full).Replace('\\', '/'), out var terrain))
            {
                _context.OpenTerrainRequest = terrain;
            }
            else
            {
                _context.OpenScriptRequest = full;
            }
        }
        window.CloseRequested = OnCloseRequested;
        _nativeMenu = NativeMenuBar.TryCreate(window.Win32Handle, Menus());
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
        if (_pendingLayoutIni is { } ini)
        {
            // ImGui takes saved settings only between frames.
            _pendingLayoutIni = null;
            ImGui.LoadIniSettingsFromMemory(ini);
        }

        ImGuiInput.NewFrame(_context.Window, time.UnscaledDeltaTime);
        _context.ParticlePreview.Update(_context.Selected, _context.PlayMode.IsPlaying, time.UnscaledDeltaTime);
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

        _nativeMenu?.RunPending(); // items chosen in the native menu bar, inside this ImGui frame
        DrawWorkspace();
        _hierarchy.Draw();
        _inspector.Draw();
        _sceneView.Draw();
        _terrainPanel?.Update();
        _gameView.Draw();
        _projectPanel.Draw();
        _console.Draw();
        _profilerPanel.Draw();
        _mixerPanel.Draw();
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
        if (_context.OpenUILayoutRequest is { } layout)
        {
            _context.OpenUILayoutRequest = null;
            _uiEditor.Open(layout);
        }

        _uiEditor.Draw();
        if (_context.OpenAnimatorRequest is { } controller)
        {
            _context.OpenAnimatorRequest = null;
            _animator.Open(controller);
        }

        _animator.Draw();
        if (options.BuildProfile is { } buildProfile && _frames == 30 && !_build.IsBusy)
        {
            _build.Profile = buildProfile;
            _build.StartBuild(runWhenDone: false);
        }

        if (options.FocusWindow is { } focus && _frames == 10) // after the dock layout and Play Mode focus settle
        {
            ImGui.SetWindowFocus(focus);
        }
        if (_focusSceneFrames > 0)
        {
            _focusSceneFrames--;
            ImGui.SetWindowFocus(EditorWindows.Id("Scene"));
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
        _inspector.Render();
        using (Core.Diagnostics.Profiler.Sample("Editor.UIEditor"))
        {
            _uiEditor.Render(time.UnscaledDeltaTime);
        }

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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] // terrain assemblies load in 3D projects only
    private bool HandleTerrainUndo(bool shift)
    {
        if (_context.TerrainEditor.Target is null)
        {
            return false;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Y, false) || (shift && ImGui.IsKeyPressed(ImGuiKey.Z, false)))
        {
            _context.TerrainEditor.Redo();
            return true;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Z, false))
        {
            _context.TerrainEditor.Undo();
            return true;
        }

        return false;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] // terrain assemblies load in 3D projects only
    private void CreateTerrainTools()
    {
        _terrainPanel = new TerrainPanel(_context);
        _context.TerrainTools = _terrainPanel;
    }

    public void Unload()
    {
        // Which engine assemblies this session loaded: a project of one profile should never load the other's.
        var engine = string.Join(", ", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "").Where(n => n.StartsWith("MNVTEngine.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        var profileName = project.Profile?.ToString() ?? "unknown";
        EditorLog.EngineAssembliesLoaded(_logger, profileName, engine);
        if (_context is null)
        {
            return; // the graphics device could not be created
        }

        _nativeMenu?.Dispose();
        SaveLayout();
        _context.PlayMode.Stop();
        _scripts.Dispose();
        _uiEditor.Dispose();
        _inspector.Dispose();
        _sceneView.Dispose();
        _gameView.Dispose();
        _context.ImGui.Dispose();
        _context.GameUI.Dispose();
        _gameUIFont.Dispose();
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

    /// <summary>
    /// Picks up files added or changed outside the editor; logs asset problems when they change. Models import in the
    /// background (the editor keeps running); their results are applied here, every frame.
    /// </summary>
    private void RefreshAssetsPeriodically(FrameTime time)
    {
        foreach (var guid in _context.Session.Assets.CompleteBackgroundImports())
        {
            EditorLog.Imported(_logger, _context.Session.Assets.Get(guid)?.Path);
        }

        if (time.UnscaledTotalTime - _lastAssetRefresh < 2.0)
        {
            return;
        }

        _lastAssetRefresh = time.UnscaledTotalTime;
        var result = _context.Session.Assets.Refresh(background: true);
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
        const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus;
        ImGui.Begin("##Workspace", _nativeMenu is null ? Flags | ImGuiWindowFlags.MenuBar : Flags);
        ImGui.PopStyleVar(3);

        if (_nativeMenu is null)
        {
            ImGuiMenus.DrawBar(Menus());
        }

        DrawToolbar();

        var dockId = ImGui.GetID(DockSpaceName);
        var size = ImGui.GetContentRegionAvail();
        if (_resetLayout || DockBuilder.GetNode(dockId) == 0)
        {
            _resetLayout = false;
            EditorLayouts.Build(_layoutKind, dockId, size);
            _focusSceneFrames = 2; // the last panel drawn would otherwise be the selected tab
        }

        ImGui.DockSpace(dockId, size);
        ImGui.End();
    }

    /// <summary>The editor's menus: shown in the native menu bar on Windows, else as an ImGui menu bar.</summary>
    private IReadOnlyList<TopMenu> Menus() =>
    [
        new("File", () =>
        [
            new("New Scene", "Ctrl+N", () => GuardUnsaved(() => _context.Session.NewScene())),
            new("Open Scene...", "Ctrl+O", () => _openScenePicker = true),
            new("Save Scene", "Ctrl+S", () => Save(), Enabled: !_context.PlayMode.IsPlaying),
            new("Save Scene As...", Action: () => _openSaveAs = true, Enabled: !_context.PlayMode.IsPlaying),
            MenuEntry.Separator,
            new("Project Hub...", Action: OpenProjectHub),
            MenuEntry.Separator,
            new("Exit", "Alt+F4", () => GuardUnsaved(_context.Window.Close)),
        ]),
        new("Edit", () =>
        {
            var history = _context.Session.History;
            var editing = !_context.PlayMode.IsPlaying;
            return
            [
                new(history.UndoLabel is { } undo ? $"Undo {undo}" : "Undo", "Ctrl+Z", () => history.Undo(), editing && history.CanUndo),
                new(history.RedoLabel is { } redo ? $"Redo {redo}" : "Redo", "Ctrl+Y", () => history.Redo(), editing && history.CanRedo),
            ];
        }),
        new("GameObject", () =>
        [
            .. _hierarchy.CreateEntries(_context.Selected?.Transform),
            MenuEntry.Separator,
            new("Delete", "Del", () =>
            {
                if (_context.Selected is { } selected)
                {
                    _context.Delete(selected);
                }
            }, _context.Selected is not null),
        ]),
        new("Build", () =>
        {
            var idle = !_build.IsBusy;
            return
            [
                new("Build Settings...", Action: () => _showBuildSettings = true),
                MenuEntry.Separator,
                new($"Build ({_build.Profile}, {_build.EffectiveScriptingBackend.ToUpperInvariant()})", "Ctrl+B", () => StartBuild(run: false), idle),
                new($"Build and Run ({_build.Profile}, {_build.EffectiveScriptingBackend.ToUpperInvariant()})", "Ctrl+Shift+B", () => StartBuild(run: true), idle),
                new("Run Last Build", Action: () =>
                {
                    if (!_build.RunLastBuild())
                    {
                        EditorLog.Warning(_logger, $"Profile '{_build.Profile}' has not been built yet.");
                    }
                }, Enabled: idle),
                new("Validate Project", Action: () => GuardUnsaved(_build.StartValidation), Enabled: idle),
                MenuEntry.Separator,
                new("Open Build Folder", Action: () => OpenFolder(_build.OutputDirectory), Enabled: Directory.Exists(_build.OutputDirectory)),
            ];
        }),
        new("Window", () =>
        [
            new("Diagnostics Settings", Action: () => _diagnosticsWindow.Open = !_diagnosticsWindow.Open, Checked: _diagnosticsWindow.Open),
            new("Reset Layout", Action: () => _resetLayout = true),
        ]),
    ];

    private void DrawToolbar()
    {
        var play = _context.PlayMode;
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX((width * 0.5f) - (ImGui.GetFrameHeight() * 1.5f));

        // The button toggles Play Mode, so IsPlaying may differ after it: the scope remembers whether it pushed.
        using (Gui.StyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.45f, 0.7f, 1f, 1f), play.IsPlaying))
        {
            if (Gui.IconButton(Icons.Play, play.IsPlaying ? "Stop (Ctrl+P)" : "Play (Ctrl+P)", play.IsPlaying))
            {
                TogglePlay();
            }
        }

        ImGui.SameLine(0f, 2f);
        ImGui.BeginDisabled(!play.IsPlaying);
        if (Gui.IconButton(Icons.Pause, play.State == PlayState.Paused ? "Resume" : "Pause", play.State == PlayState.Paused))
        {
            play.TogglePause();
        }

        ImGui.SameLine(0f, 2f);
        ImGui.BeginDisabled(play.State != PlayState.Paused);
        if (Gui.IconButton(Icons.StepForward, "Step one frame"))
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

        ImGui.SameLine(ImGui.GetWindowWidth() - 420f);
        DrawLayoutMenu();
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
            if (Gui.IconButton(Icons.RefreshCw, "Recompile scripts"))
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

        // The Animator window has its own undo and save while it has focus.
        if (io.KeyCtrl && _animator.IsFocused)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.S, false))
            {
                _animator.Save();
                _lastAssetRefresh = double.MinValue;
            }
            else if (!io.WantTextInput && (ImGui.IsKeyPressed(ImGuiKey.Y, false) || (io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Z, false))))
            {
                _animator.Redo();
            }
            else if (!io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Z, false))
            {
                _animator.Undo();
            }

            return;
        }

        // The UI Editor has its own undo and save while it has focus.
        if (io.KeyCtrl && _uiEditor.IsFocused)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.S, false))
            {
                _uiEditor.Save();
                _lastAssetRefresh = double.MinValue;
            }
            else if (!io.WantTextInput && (ImGui.IsKeyPressed(ImGuiKey.Y, false) || (io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Z, false))))
            {
                _uiEditor.Redo();
            }
            else if (!io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Z, false))
            {
                _uiEditor.Undo();
            }

            return;
        }

        // While a terrain world is being edited (3D projects), undo and redo go to its history.
        if (io.KeyCtrl && !io.WantTextInput && _terrainPanel is not null && HandleTerrainUndo(io.KeyShift))
        {
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
                LogDiagnostic(_logger, diagnostic.Severity switch { DiagnosticSeverity.Error => LogLevel.Error, DiagnosticSeverity.Warning => LogLevel.Warning, _ => LogLevel.Information }, path, diagnostic.Path, diagnostic.Message);
            }

            LogOpened(_logger, path, result.Scene.GameObjects.Count);
        }
        catch (SerializationException ex)
        {
            LogOpenFailed(_logger, ex, path);
        }
    }

    /// <summary>Scene visibility is per user, like the layout: loaded once, saved on every change.</summary>
    private void LoadSceneVisibility(string file)
    {
        try
        {
            if (File.Exists(file) && !_context.Visibility.Load(File.ReadAllText(file)))
            {
                EditorLog.Warning(_context.Logger, "Scene visibility file is not valid; everything is visible.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EditorLog.ActionFailed(_context.Logger, ex, "Load scene visibility");
        }

        _context.Visibility.Changed += () =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, _context.Visibility.ToJson());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                EditorLog.ActionFailed(_context.Logger, ex, "Save scene visibility");
            }
        };
    }

    private string LayoutsFolder => Path.Combine(Path.GetDirectoryName(_iniPath)!, "Layouts");

    /// <summary>Unity's Layout menu: the built-in arrangements, saved layouts, Save Layout and Delete Layout.</summary>
    private void DrawLayoutMenu()
    {
        if (ImGui.Button($"{Icons.LayoutTemplate}  Layout {Icons.ChevronDown}"))
        {
            ImGui.OpenPopup("##layouts");
        }

        if (!ImGui.BeginPopup("##layouts"))
        {
            return;
        }

        foreach (var (kind, name) in EditorLayouts.BuiltIn)
        {
            if (ImGui.MenuItem(name))
            {
                (_layoutKind, _resetLayout) = (kind, true);
            }
        }

        var saved = EditorLayouts.Saved(LayoutsFolder);
        if (saved.Count > 0)
        {
            ImGui.Separator();
            foreach (var name in saved)
            {
                if (ImGui.MenuItem(name) && EditorLayouts.FileOf(LayoutsFolder, name) is { } file)
                {
                    try
                    {
                        _pendingLayoutIni = EditorWindows.MigrateLayout(File.ReadAllText(file));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        EditorLog.ActionFailed(_context.Logger, ex, "Load layout");
                    }
                }
            }
        }

        ImGui.Separator();
        if (ImGui.BeginMenu("Save Layout..."))
        {
            ImGui.SetNextItemWidth(180f);
            var submit = ImGui.InputTextWithHint("##layout_name", "Layout name", ref _newLayoutName, 64, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if ((ImGui.Button("Save") || submit) && EditorLayouts.FileOf(LayoutsFolder, _newLayoutName) is { } file)
            {
                try
                {
                    Directory.CreateDirectory(LayoutsFolder);
                    File.WriteAllText(file, ImGui.SaveIniSettingsToMemory());
                    _newLayoutName = "";
                    ImGui.CloseCurrentPopup();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    EditorLog.ActionFailed(_context.Logger, ex, "Save layout");
                }
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Delete Layout", saved.Count > 0))
        {
            foreach (var name in saved)
            {
                if (ImGui.MenuItem(name) && EditorLayouts.FileOf(LayoutsFolder, name) is { } file)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        EditorLog.ActionFailed(_context.Logger, ex, "Delete layout");
                    }
                }
            }

            ImGui.EndMenu();
        }

        if (ImGui.MenuItem("Reset All Layouts"))
        {
            (_layoutKind, _resetLayout) = (EditorLayout.Default, true);
        }

        ImGui.EndPopup();
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
        if (ImGui.Begin(EditorWindows.Title("Build Settings"), ref _showBuildSettings, ImGuiWindowFlags.NoDocking))
        {
            var profiles = project.EffectiveBuildProfiles;
            var names = profiles.Select(p => p.Name).ToArray();
            var index = Math.Max(0, Array.IndexOf(names, _build.Profile));
            if (ImGui.Combo("Profile", ref index, names, names.Length))
            {
                _build.Profile = names[index];
            }

            var profile = profiles[index];
            string[] backends = [$"Profile default ({profile.ScriptingBackend.ToUpperInvariant()})", "JIT (CoreCLR, IL assemblies)", "AOT (NativeAOT, one native executable)"];
            var backend = _build.ScriptingBackend switch { "jit" => 1, "aot" => 2, _ => 0 };
            if (ImGui.Combo("Scripting backend", ref backend, backends, backends.Length))
            {
                _build.ScriptingBackend = backend switch { 1 => "jit", 2 => "aot", _ => null };
            }

            if (ImGui.IsItemHovered())
            {
                Gui.Tooltip("JIT: IL on a private .NET runtime (about 80 MB). AOT: NativeAOT, no runtime (about 25 MB), needs the MSVC linker and takes about a minute longer. For this editor session; the project file is not changed.");
            }

            Gui.Muted($"{profile.Configuration}, {profile.Target}, {_build.EffectiveScriptingBackend.ToUpperInvariant()}, console window: {(profile.ShowsConsole ? "yes" : "no")}");
            Gui.Muted($"Output: {_build.OutputDirectory}");
            Gui.Muted("Profiles are defined in the project file (\"buildProfiles\"); see docs/build-system/building.md.");
            ImGui.Spacing();
            ImGui.BeginDisabled(_build.IsBusy);
            if (ImGui.Button($"{Icons.Hammer}  Build"))
            {
                StartBuild(run: false);
            }

            ImGui.SameLine();
            if (ImGui.Button($"{Icons.CirclePlay}  Build and Run"))
            {
                StartBuild(run: true);
            }

            ImGui.SameLine();
            if (ImGui.Button($"{Icons.ShieldCheck}  Validate"))
            {
                GuardUnsaved(_build.StartValidation);
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button($"{Icons.FolderOpen}  Open Folder") && Directory.Exists(_build.OutputDirectory))
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
