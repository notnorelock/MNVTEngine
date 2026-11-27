// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-27.

using ImGuiNET;
using Microsoft.Extensions.Logging;
using MNVTEngine.Build;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Extensions;
using MNVTEngine.Editor.Services;

namespace MNVTEngine.Editor;

/// <summary>
/// Runs a project's editor extensions (docs/editor/extensions.md): creates the windows and inspectors of each loaded
/// generation of editor code, draws the open windows, lists them in the Tools menu and adds inspector sections. An
/// extension that throws is logged and skipped until its code reloads, so it cannot take the editor down. Which windows
/// are open survives reloads and restarts (<c>Library/EditorExtensions.open</c>).
/// </summary>
internal sealed class ExtensionHost
{
    private readonly EditorContext _context;
    private readonly ImGuiEditorGui _gui = new();
    private readonly HashSet<string> _open;
    private readonly HashSet<object> _failed = [];
    private readonly string _openFile;
    private Services _services;
    private Dictionary<string, (EditorWindow Window, string? Menu)> _windows = [];
    private List<ComponentInspector> _inspectors = [];
    private List<SceneGizmos> _gizmos = [];

    public ExtensionHost(EditorContext context)
    {
        _context = context;
        _openFile = Path.Combine(context.Session.Project.RootDirectory, "Library", "EditorExtensions.open");
        _open = File.Exists(_openFile) ? [.. File.ReadAllLines(_openFile).Where(l => l.Length > 0)] : [];
        _services = new Services(this, "Editor");
    }

    /// <summary>Replaces the extensions with a new generation's (the old instances are closed and let go, so their code can unload).</summary>
    public void Load(ExtensionCatalog catalog, string name)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (var (window, _) in _windows.Values.Where(w => _open.Contains(Key(w.Window.GetType()))))
        {
            Run(window, "OnClose", window.OnClose);
        }

        _failed.Clear();
        _services = new Services(this, name);
        _windows = [];
        foreach (var (type, menu) in catalog.Windows)
        {
            if (Create<EditorWindow>(type) is { } window)
            {
                window.Attach(_services);
                _windows[Key(type)] = (window, menu);
                if (_open.Contains(Key(type)))
                {
                    Run(window, "OnOpen", window.OnOpen);
                }
            }
        }

        _inspectors = [];
        foreach (var type in catalog.Inspectors)
        {
            if (Create<ComponentInspector>(type) is { } inspector)
            {
                inspector.Attach(_services);
                _inspectors.Add(inspector);
            }
        }

        _gizmos = [];
        foreach (var type in catalog.Gizmos)
        {
            if (Create<SceneGizmos>(type) is { } gizmos)
            {
                gizmos.Attach(_services);
                _gizmos.Add(gizmos);
            }
        }
    }

    /// <summary>The Tools menu: one item per window with a menu path, in submenus by its slashes.</summary>
    public IReadOnlyList<MenuEntry> MenuEntries()
    {
        var entries = Tree(_windows.Values.Where(w => w.Menu is not null).Select(w => (w.Menu!.Split('/').Select(p => p.Trim()).ToArray(), w.Window)).ToList(), 0);
        return entries.Count > 0 ? entries : [new MenuEntry("No tools in this project", Enabled: false)];
    }

    /// <summary>Draws the open windows. Call once per editor frame, inside the ImGui frame.</summary>
    public void Draw()
    {
        foreach (var (key, (window, _)) in _windows)
        {
            if (!_open.Contains(key))
            {
                continue;
            }

            var open = true;
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(460, 520), ImGuiCond.FirstUseEver);
            if (ImGui.Begin($"{window.Title}###ext:{key}", ref open))
            {
                if (_failed.Contains(window))
                {
                    Gui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.4f, 1f), "This window failed; see the Console. It comes back when the editor code reloads.");
                }
                else
                {
                    Run(window, "OnGui", () => window.OnGui(_gui));
                }
            }

            ImGui.End();
            if (!open)
            {
                SetOpen(key, false);
            }
        }
    }

    /// <summary>The extension sections for <paramref name="component"/>, below its fields in the inspector.</summary>
    public void DrawInspectors(Component component)
    {
        foreach (var inspector in _inspectors.Where(i => i.ComponentType.IsInstanceOfType(component) && !_failed.Contains(i)))
        {
            ImGui.PushID(inspector.GetType().FullName);
            ImGui.Spacing();
            Run(inspector, "OnInspectorGui", () => inspector.OnInspectorGui(_gui, component));
            ImGui.PopID();
        }
    }

    /// <summary>The extensions' gizmos over the 2D Scene view.</summary>
    public void DrawGizmos(ISceneGizmoDrawer draw)
    {
        foreach (var gizmos in _gizmos.Where(g => !_failed.Contains(g)))
        {
            Run(gizmos, "OnDrawGizmos", () => gizmos.OnDrawGizmos(draw));
        }
    }

    private static string Key(Type type) => type.FullName ?? type.Name;

    private List<MenuEntry> Tree(List<(string[] Path, EditorWindow Window)> items, int depth)
    {
        var entries = new List<MenuEntry>();
        foreach (var group in items.GroupBy(i => i.Path[depth], StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var leaves = group.Where(i => i.Path.Length == depth + 1).ToList();
            var deeper = group.Where(i => i.Path.Length > depth + 1).ToList();
            foreach (var (_, window) in leaves)
            {
                var key = Key(window.GetType());
                entries.Add(new MenuEntry(group.Key, Action: () => SetOpen(key, true), Checked: _open.Contains(key)));
            }

            if (deeper.Count > 0)
            {
                entries.Add(MenuEntry.Submenu(group.Key, Tree(deeper, depth + 1)));
            }
        }

        return entries;
    }

    private void SetOpen(string key, bool open)
    {
        if (!_windows.TryGetValue(key, out var entry))
        {
            return;
        }

        if (open)
        {
            if (_open.Add(key))
            {
                Run(entry.Window, "OnOpen", entry.Window.OnOpen);
            }

            ImGui.SetWindowFocus($"{entry.Window.Title}###ext:{key}");
        }
        else if (_open.Remove(key))
        {
            Run(entry.Window, "OnClose", entry.Window.OnClose);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_openFile)!);
        File.WriteAllLines(_openFile, _open.Order(StringComparer.Ordinal));
    }

    private T? Create<T>(Type type)
        where T : class
    {
        try
        {
            return (T)Activator.CreateInstance(type)!;
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            EditorLog.ExtensionFailed(_context.Logger, ex.InnerException ?? ex, type.FullName ?? type.Name, "its constructor");
            return null;
        }
    }

    private void Run(object extension, string where, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _failed.Add(extension);
            EditorLog.ExtensionFailed(_context.Logger, ex, extension.GetType().FullName ?? extension.GetType().Name, where);
        }
    }

    /// <summary>The editor as one generation of one project's extensions sees it.</summary>
    private sealed class Services(ExtensionHost host, string name) : IEditorServices
    {
        private ProjectExtensionSettings? _settings;
        private EditorSecretStore? _secrets;

        private EditorContext Context => host._context;

        public string ProjectDirectory => Context.Session.Project.RootDirectory;

        public Scene Scene => Context.ActiveScene;

        public GameObject? Selected => Context.Selected;

        public IExtensionSettings Settings => _settings ??= Report(new ProjectExtensionSettings(ProjectDirectory, name), s => s.Problem);

        public IEditorSecrets Secrets => _secrets ??= Report(new EditorSecretStore(ProjectDirectory, name), s => s.Problem);

        public IReadOnlyList<EditorAsset> Assets(string type) =>
            [.. Context.Session.Assets.Assets.Where(a => a.Type == type).Select(a => new EditorAsset(a.Guid, a.Path, a.Type)).OrderBy(a => a.Path, StringComparer.Ordinal)];

        public GameObject? ReadPrefab(Guid prefab)
        {
            try
            {
                return Context.Session.Prefabs.Instantiate(new Scene(), new AssetRef(prefab), null);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public void OpenWindow<T>()
            where T : EditorWindow => host.SetOpen(Key(typeof(T)), true);

        public Task<EditorBundleResult> BuildBundleAsync(EditorBundleRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var project = Context.Session.Project;
            return Task.Run(() =>
            {
                var result = BundleBuilder.Build(project, new BundleRequest(request.Root, request.Kind, request.Name, null, request.Provided, request.Properties, request.Policy));
                return new EditorBundleResult(result.File,
                    [.. result.Diagnostics.Where(d => d.Severity == BuildSeverity.Error).Select(d => d.ToString())],
                    [.. result.Diagnostics.Where(d => d.Severity == BuildSeverity.Warning).Select(d => d.ToString())]);
            });
        }

        public void Log(EditorLogLevel level, string message) => EditorLog.Extension(Context.Logger, level switch
        {
            EditorLogLevel.Error => LogLevel.Error,
            EditorLogLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        }, name, message);

        private T Report<T>(T store, Func<T, string?> problem)
        {
            if (problem(store) is { } text)
            {
                Log(EditorLogLevel.Warning, text);
            }

            return store;
        }
    }
}
