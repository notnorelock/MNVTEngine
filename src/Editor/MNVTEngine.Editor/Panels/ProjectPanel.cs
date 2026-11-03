using ImGuiNET;
using MNVTEngine.Editor.ImGuiBackend;
using SysVector2 = System.Numerics.Vector2;

namespace MNVTEngine.Editor.Panels;

/// <summary>Browses the project's Assets folder. Assets drag onto inspector fields and the Scene view; double-click opens scenes.</summary>
internal sealed class ProjectPanel(EditorContext context)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private string _directory = "";
    private string? _selected;
    private string[] _folders = [];
    private string[] _files = [];
    private DateTime _lastScan = DateTime.MinValue;

    public void Draw()
    {
        if (ImGui.Begin("Project"))
        {
            Rescan(force: false);
            DrawBreadcrumb();
            ImGui.Separator();

            if (ImGui.BeginTable("##project", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupColumn("Folders", ImGuiTableColumnFlags.WidthFixed, 180f);
                ImGui.TableNextColumn();
                ImGui.BeginChild("##folders");
                DrawFolderTree(context.Session.Project.AssetsDirectory, "");
                ImGui.EndChild();

                ImGui.TableNextColumn();
                ImGui.BeginChild("##files");
                DrawEntries();
                ImGui.EndChild();
                ImGui.EndTable();
            }
        }

        ImGui.End();
    }

    private void DrawBreadcrumb()
    {
        if (ImGui.SmallButton("Assets"))
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

        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 60f);
        if (ImGui.SmallButton("Refresh"))
        {
            Rescan(force: true);
        }
    }

    private void DrawFolderTree(string fullPath, string relative)
    {
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
        string[] children;
        try
        {
            children = Directory.GetDirectories(fullPath);
        }
        catch (IOException)
        {
            children = [];
        }

        if (children.Length == 0)
        {
            flags |= ImGuiTreeNodeFlags.Leaf;
        }

        if (relative == _directory)
        {
            flags |= ImGuiTreeNodeFlags.Selected;
        }

        var open = ImGui.TreeNodeEx(relative.Length == 0 ? "Assets" : Path.GetFileName(fullPath), flags);
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
        {
            Navigate(relative);
        }

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
            if (ImGui.Selectable($"[dir] {folder}", false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                Navigate(Combine(folder));
            }
        }

        foreach (var file in _files)
        {
            var relative = Combine(file);
            var extension = Path.GetExtension(file).ToUpperInvariant();
            if (extension is ".PNG" or ".JPG" or ".JPEG" or ".BMP" or ".TGA")
            {
                var texture = context.Resources.GetTexture(relative);
                Gui.Image(context.ImGui.Register(texture), texture, new SysVector2(18f, 18f));
                ImGui.SameLine();
            }
            else
            {
                Gui.Muted(extension == ".MNVT" ? "[scene]" : "[file] ");
                ImGui.SameLine();
            }

            if (ImGui.Selectable(file, _selected == relative, ImGuiSelectableFlags.AllowDoubleClick))
            {
                _selected = relative;
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && extension == ".MNVT")
                {
                    context.OpenSceneRequest = relative;
                }
            }

            if (ImGui.BeginDragDropSource())
            {
                Gui.SetStringPayload(Gui.AssetPayload, relative);
                ImGui.TextUnformatted(relative);
                ImGui.EndDragDropSource();
            }
        }

        if (_folders.Length == 0 && _files.Length == 0)
        {
            Gui.Muted("Empty folder");
        }
    }

    private void Navigate(string relative)
    {
        _directory = relative;
        Rescan(force: true);
    }

    private void Rescan(bool force)
    {
        if (!force && DateTime.UtcNow - _lastScan < RefreshInterval)
        {
            return;
        }

        _lastScan = DateTime.UtcNow;
        var full = _directory.Length == 0 ? context.Session.Project.AssetsDirectory : context.Session.Project.ResolveAsset(_directory);
        if (!Directory.Exists(full))
        {
            _directory = "";
            full = context.Session.Project.AssetsDirectory;
        }

        _folders = [.. Directory.GetDirectories(full).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase)!];
        _files = [.. Directory.GetFiles(full).Select(Path.GetFileName).Where(f => !f!.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase)!];
    }

    private string Combine(string name) => _directory.Length == 0 ? name : $"{_directory}/{name}";
}
