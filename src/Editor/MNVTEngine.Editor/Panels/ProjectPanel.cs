using ImGuiNET;
using MNVTEngine.Assets;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Serialization;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// Browses the Assets folder through the asset database. Select an asset to inspect it; drag assets onto fields,
/// the Scene view or folders (move); drag a GameObject from the Hierarchy here to create a prefab. Right-click
/// an asset to rename, reimport or delete it. Moves and renames keep the asset's GUID, so references stay valid.
/// </summary>
internal sealed class ProjectPanel(EditorContext context)
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);

    private string _directory = "";
    private string[] _folders = [];
    private string[] _files = [];
    private DateTime _lastScan = DateTime.MinValue;
    private Guid? _renameTarget;
    private string _renameText = "";
    private Guid? _deleteTarget;
    private bool _openRename;
    private bool _openDelete;

    private AssetDatabase Assets => context.Session.Assets;

    public void Draw()
    {
        if (ImGui.Begin(EditorWindows.Title("Project")))
        {
            Rescan(force: false);
            DrawBreadcrumb();
            ImGui.Separator();

            if (ImGui.BeginTable("##project", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupColumn("Folders", ImGuiTableColumnFlags.WidthFixed, 180f);
                ImGui.TableNextColumn();
                ImGui.BeginChild("##folders");
                DrawFolderTree(Assets.AssetsDirectory, "");
                ImGui.EndChild();

                ImGui.TableNextColumn();
                ImGui.BeginChild("##files");
                DrawEntries();
                DrawBackgroundDropZone();
                if (ImGui.BeginPopupContextWindow("##create", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
                {
                    if (ImGui.BeginMenu("Create"))
                    {
                        if (ImGui.MenuItem("Animator Controller"))
                        {
                            CreateAnimatorController();
                        }

                        if (ImGui.MenuItem("UI Layout"))
                        {
                            CreateUILayout();
                        }

                        if (ImGui.MenuItem("Material"))
                        {
                            CreateMaterial();
                        }

                        if (ImGui.MenuItem("Particle System"))
                        {
                            CreateParticleSystem();
                        }

                        ImGui.EndMenu();
                    }

                    ImGui.EndPopup();
                }

                ImGui.EndChild();
                ImGui.EndTable();
            }

            DrawPopups();
        }

        ImGui.End();
    }

    private void DrawBreadcrumb()
    {
        if (ImGui.SmallButton($"{Icons.Folder}  Assets"))
        {
            Navigate("");
        }

        var parts = _directory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("/");
            ImGui.SameLine();
            if (ImGui.SmallButton(parts[i]))
            {
                Navigate(string.Join('/', parts[..(i + 1)]));
            }
        }

        ImGui.SameLine();
        Gui.Muted($"   {Assets.Assets.Count} assets, {Assets.Diagnostics.Count} problems");
    }

    /// <summary>The icon of an asset type in lists (textures show their image instead).</summary>
    internal static string AssetIcon(string? type) => type switch
    {
        AssetTypes.Texture => Icons.FileImage,
        AssetTypes.Scene => Icons.Boxes,
        AssetTypes.Prefab => Icons.Package,
        AssetTypes.Audio => Icons.FileAudio,
        AssetTypes.Font => Icons.FileType,
        AssetTypes.Shader => Icons.FileCog,
        AssetTypes.Script => Icons.FileCode,
        AssetTypes.Json => Icons.FileJson,
        AssetTypes.Animation => Icons.Clapperboard,
        AssetTypes.Material => Icons.Palette,
        AssetTypes.Text => Icons.FileText,
        AssetTypes.Model => Icons.FileBox,
        AssetTypes.UILayout => Icons.LayoutTemplate,
        AssetTypes.AnimatorController => Icons.Workflow,
        _ => Icons.File,
    };

    private void DrawFolderTree(string fullPath, string relative)
    {
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
        var children = Directory.Exists(fullPath) ? Directory.GetDirectories(fullPath) : [];
        if (children.Length == 0)
        {
            flags |= ImGuiTreeNodeFlags.Leaf;
        }

        if (relative == _directory)
        {
            flags |= ImGuiTreeNodeFlags.Selected;
        }

        var name = relative.Length == 0 ? "Assets" : Path.GetFileName(fullPath);
        var open = ImGui.TreeNodeEx($"{(relative == _directory ? Icons.FolderOpen : Icons.Folder)}  {name}###{name}", flags);
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
        {
            Navigate(relative);
        }

        FolderDropTarget(relative);

        if (open)
        {
            foreach (var child in children.Order(StringComparer.OrdinalIgnoreCase))
            {
                DrawFolderTree(child, relative.Length == 0 ? Path.GetFileName(child) : $"{relative}/{Path.GetFileName(child)}");
            }

            ImGui.TreePop();
        }
    }

    private void DrawEntries()
    {
        foreach (var folder in _folders)
        {
            if (ImGui.Selectable($"{Icons.Folder}  {folder}", false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                Navigate(Combine(folder));
            }

            FolderDropTarget(Combine(folder));
        }

        foreach (var file in _files)
        {
            var relative = Combine(file);
            var record = Assets.TryGetGuid(relative, out var guid) ? Assets.Get(guid) : null;
            ImGui.PushID(relative);

            if (record?.Type == AssetTypes.Texture && record.Status == AssetImportStatus.Imported)
            {
                var texture = context.Resources.GetTexture(new AssetRef(guid));
                Gui.Image(context.ImGui.Register(texture), texture, new SysVector2(18f, 18f));
            }
            else
            {
                Gui.Muted(AssetIcon(record?.Type));
                if (ImGui.IsItemHovered())
                {
                    Gui.Tooltip(record?.Type ?? "Not imported");
                }
            }

            ImGui.SameLine();
            var failed = record?.Status == AssetImportStatus.Failed;
            var selected = record is not null && context.Session.Selection.Asset == guid;
            using (Gui.StyleColor(ImGuiCol.Text, new SysVector4(1f, 0.45f, 0.45f, 1f), failed))
            {
                if (ImGui.Selectable(file, selected, ImGuiSelectableFlags.AllowDoubleClick) && record is not null)
                {
                    context.Session.Selection.SelectAsset(guid);
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && record.Type == AssetTypes.Scene)
                    {
                        context.OpenSceneRequest = relative;
                    }
                    else if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && record.Type == AssetTypes.UILayout)
                    {
                        context.OpenUILayoutRequest = context.Session.Assets.FullPath(guid);
                    }
                    else if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && record.Type == AssetTypes.Terrain)
                    {
                        context.OpenTerrainRequest = guid;
                    }
                    else if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && record.Type == AssetTypes.AnimatorController)
                    {
                        context.OpenAnimatorRequest = context.Session.Assets.FullPath(guid);
                    }
                    else if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && record.Type is AssetTypes.Script or AssetTypes.Text or AssetTypes.Json
                        or AssetTypes.Shader or AssetTypes.Animation or AssetTypes.Material)
                    {
                        context.OpenScriptRequest = context.Session.Assets.FullPath(guid); // text assets open in the Script Editor
                    }
                }
            }

            if (failed)
            {
                if (ImGui.IsItemHovered())
                {
                    Gui.Tooltip(string.Join('\n', record!.Messages));
                }
            }

            if (ImGui.BeginDragDropSource())
            {
                Gui.SetStringPayload(Gui.AssetPayload, relative);
                ImGui.TextUnformatted(relative);
                ImGui.EndDragDropSource();
            }

            if (record is not null && ImGui.BeginPopupContextItem())
            {
                if (ImGui.MenuItem("Rename..."))
                {
                    _renameTarget = guid;
                    _renameText = Path.GetFileName(record.Path);
                    _openRename = true;
                }

                if (ImGui.MenuItem("Reimport"))
                {
                    Assets.Reimport(guid);
                }

                if (ImGui.MenuItem("Delete..."))
                {
                    _deleteTarget = guid;
                    _openDelete = true;
                }

                ImGui.EndPopup();
            }

            ImGui.PopID();
        }

        if (_folders.Length == 0 && _files.Length == 0)
        {
            Gui.Muted("Empty folder. Drag a GameObject here to create a prefab.");
        }
    }

    /// <summary>The rest of the file list: drop a GameObject to save it as a prefab, or an asset to move it here.</summary>
    private void DrawBackgroundDropZone()
    {
        var avail = ImGui.GetContentRegionAvail();
        ImGui.InvisibleButton("##project_drop", new SysVector2(Math.Max(avail.X, 1f), Math.Max(avail.Y, 30f)));
        if (!ImGui.BeginDragDropTarget())
        {
            return;
        }

        if (Gui.AcceptGuidPayload() is { } id && context.ActiveScene.FindById(id) is { } gameObject)
        {
            CreatePrefab(gameObject);
        }

        if (Gui.AcceptStringPayload(Gui.AssetPayload) is { } path)
        {
            MoveInto(path, _directory);
        }

        ImGui.EndDragDropTarget();
    }

    private void FolderDropTarget(string folder)
    {
        if (!ImGui.BeginDragDropTarget())
        {
            return;
        }

        if (Gui.AcceptStringPayload(Gui.AssetPayload) is { } path)
        {
            MoveInto(path, folder);
        }

        ImGui.EndDragDropTarget();
    }

    private void DrawPopups()
    {
        if (_openRename)
        {
            ImGui.OpenPopup("Rename asset");
            _openRename = false;
        }

        if (_openDelete)
        {
            ImGui.OpenPopup("Delete asset");
            _openDelete = false;
        }

        var open = true;
        if (ImGui.BeginPopupModal("Rename asset", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.SetNextItemWidth(300f);
            ImGui.InputText("##name", ref _renameText, 128);
            if (ImGui.Button("Rename", new SysVector2(100f, 0f)) && _renameTarget is { } guid && Assets.Get(guid) is { } record)
            {
                var folder = Path.GetDirectoryName(record.Path)?.Replace('\\', '/') ?? "";
                var target = folder.Length == 0 ? _renameText : $"{folder}/{_renameText}";
                Try($"Rename {record.Path}", () => Assets.Move(guid, target));
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new SysVector2(100f, 0f)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        open = true;
        if (ImGui.BeginPopupModal("Delete asset", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (_deleteTarget is { } guid && Assets.Get(guid) is { } record)
            {
                ImGui.TextUnformatted($"Delete '{record.Path}'? This cannot be undone.");
                var dependents = Assets.GetDependents(guid);
                if (dependents.Count > 0)
                {
                    Gui.TextColored(new SysVector4(1f, 0.7f, 0.3f, 1f), $"Referenced by {dependents.Count} asset(s); those references will break:");
                    foreach (var dependent in dependents)
                    {
                        Gui.Muted("  " + dependent.Path);
                    }
                }

                if (ImGui.Button("Delete", new SysVector2(100f, 0f)))
                {
                    Try($"Delete {record.Path}", () => Assets.Delete(guid));
                    context.Session.Selection.Clear();
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();
            }

            if (ImGui.Button("Cancel", new SysVector2(100f, 0f)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    /// <summary>A new particle system asset (default settings) in the current folder, selected.</summary>
    private void CreateParticleSystem()
    {
        var path = Combine("New Particle System" + Particles.ParticleSystemAsset.Extension);
        for (var i = 1; File.Exists(Path.Combine(Assets.AssetsDirectory, path)); i++)
        {
            path = Combine($"New Particle System {i}{Particles.ParticleSystemAsset.Extension}");
        }

        Try($"Create particle system {path}", () => context.Session.Selection.SelectAsset(Assets.WriteText(path, Particles.ParticleSystemAsset.Serialize(new Particles.ParticleSystemSettings()))));
        Rescan(force: true);
    }

    /// <summary>A new Standard material in the current folder, selected so the inspector shows it.</summary>
    private void CreateMaterial()
    {
        var path = Combine("New Material.mnvtmat");
        for (var i = 1; File.Exists(Path.Combine(Assets.AssetsDirectory, path)); i++)
        {
            path = Combine($"New Material {i}.mnvtmat");
        }

        Try($"Create material {path}", () => context.Session.Selection.SelectAsset(Assets.WriteText(path, Services.MaterialDocument.NewStandardMaterialJson())));
        Rescan(force: true);
    }

    /// <summary>A new animator controller with one state in the current folder, opened in the Animator window.</summary>
    private void CreateAnimatorController()
    {
        var path = Combine("New Animator.mnvtanimator");
        for (var i = 1; File.Exists(Path.Combine(Assets.AssetsDirectory, path)); i++)
        {
            path = Combine($"New Animator {i}.mnvtanimator");
        }

        var full = Path.Combine(Assets.AssetsDirectory, path);
        Try($"Create animator controller {path}", () =>
        {
            Services.AnimatorControllerDocument.CreateNew(full);
            Assets.Refresh();
            if (Assets.TryGetGuid(path, out var guid))
            {
                context.Session.Selection.SelectAsset(guid);
            }

            context.OpenAnimatorRequest = full;
        });
        Rescan(force: true);
    }

    /// <summary>A new full-screen layout in the current folder, opened in the UI Editor.</summary>
    private void CreateUILayout()
    {
        var path = Combine("New UI.mnvtui");
        for (var i = 1; File.Exists(Path.Combine(Assets.AssetsDirectory, path)); i++)
        {
            path = Combine($"New UI {i}.mnvtui");
        }

        var full = Path.Combine(Assets.AssetsDirectory, path);
        Try($"Create UI layout {path}", () =>
        {
            Services.UILayoutDocument.CreateNew(full);
            Assets.Refresh();
            if (Assets.TryGetGuid(path, out var guid))
            {
                context.Session.Selection.SelectAsset(guid);
            }

            context.OpenUILayoutRequest = full;
        });
        Rescan(force: true);
    }

    private void CreatePrefab(GameObject gameObject)
    {
        var baseName = string.Concat(gameObject.Name.Split(Path.GetInvalidFileNameChars()));
        var path = Combine(baseName + ".mnvtprefab");
        for (var i = 1; File.Exists(Path.Combine(Assets.AssetsDirectory, path)); i++)
        {
            path = Combine($"{baseName} {i}.mnvtprefab");
        }

        Try($"Create prefab {path}", () =>
        {
            context.Session.Selection.SelectAsset(context.Session.CreatePrefab(gameObject, path));
            EditorLog.PrefabCreated(context.Logger, path, gameObject.Name);
        });
        Rescan(force: true);
    }

    private void MoveInto(string assetPath, string folder)
    {
        if (!Assets.TryGetGuid(assetPath, out var guid))
        {
            return;
        }

        var target = folder.Length == 0 ? Path.GetFileName(assetPath) : $"{folder}/{Path.GetFileName(assetPath)}";
        if (!string.Equals(target, assetPath, StringComparison.OrdinalIgnoreCase))
        {
            Try($"Move {assetPath}", () => Assets.Move(guid, target));
            Rescan(force: true);
        }
    }

    private void Try(string action, Action operation)
    {
        try
        {
            operation();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            EditorLog.ActionFailed(context.Logger, ex, action);
        }
    }

    private void Navigate(string relative)
    {
        _directory = relative;
        Rescan(force: true);
    }

    private void Rescan(bool force)
    {
        if (!force && DateTime.UtcNow - _lastScan < RescanInterval)
        {
            return;
        }

        _lastScan = DateTime.UtcNow;
        var full = _directory.Length == 0 ? Assets.AssetsDirectory : AssetPaths.Resolve(Assets.AssetsDirectory, _directory);
        if (!Directory.Exists(full))
        {
            _directory = "";
            full = Assets.AssetsDirectory;
        }

        _folders = [.. Directory.GetDirectories(full).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase)!];
        _files = [.. Directory.GetFiles(full).Select(Path.GetFileName)
            .Where(f => !f!.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(AssetDatabase.MetaExtension, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)!];
    }

    private string Combine(string name) => _directory.Length == 0 ? name : $"{_directory}/{name}";
}
