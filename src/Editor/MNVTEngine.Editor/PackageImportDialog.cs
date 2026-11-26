// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using ImGuiNET;
using MNVTEngine.Build;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor;

/// <summary>
/// File > Import Package... (docs/editor/packages.md): choose an editor package (<c>.mnvtbundle</c> of kind
/// <c>mnvt.package</c>), see what it adds, replaces and removes and that its code will run here, then import it.
/// </summary>
internal sealed class PackageImportDialog(EditorContext context, ScriptReloadController scripts) : IDisposable
{
    private const string Title = "Import package";
    private static readonly SysVector4 Red = new(1f, 0.45f, 0.4f, 1f);
    private static readonly SysVector4 Amber = new(1f, 0.78f, 0.35f, 1f);

    private bool _open;
    private string _path = "";
    private EditorPackageFile? _package;
    private EditorPackageImportPlan? _plan;
    private IReadOnlyList<string> _errors = [];
    private List<string> _found = [];

    public void Open()
    {
        _open = true;
        Close();
        _found = [.. Candidates()];
    }

    public void Draw()
    {
        if (_open)
        {
            ImGui.OpenPopup(Title);
            _open = false;
        }

        var open = true;
        ImGui.SetNextWindowSize(new SysVector2(640f, 0f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(Title, ref open))
        {
            return;
        }

        if (_plan is null)
        {
            DrawChoose();
        }
        else
        {
            DrawPlan(_plan);
        }

        if (!open)
        {
            Close();
        }

        ImGui.EndPopup();
    }

    public void Dispose() => Close();

    private void DrawChoose()
    {
        ImGui.TextUnformatted("Package file (.mnvtbundle):");
        ImGui.SetNextItemWidth(-90f);
        var submitted = ImGui.InputText("##package", ref _path, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var path = _path.Trim().Trim('"');
        ImGui.BeginDisabled(path.Length == 0);
        if ((ImGui.Button("Open", new SysVector2(80f, 0f)) || submitted) && path.Length > 0)
        {
            Load(path);
        }

        ImGui.EndDisabled();

        if (_found.Count > 0)
        {
            Gui.Muted("In Downloads and on the Desktop:");
            foreach (var file in _found)
            {
                if (ImGui.Selectable(file))
                {
                    _path = file;
                    Load(file);
                }
            }
        }

        DrawErrors();
        ImGui.Spacing();
        if (ImGui.Button("Cancel", new SysVector2(110f, 0f)))
        {
            Close();
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawPlan(EditorPackageImportPlan plan)
    {
        var manifest = plan.Manifest;
        ImGui.TextUnformatted($"{manifest.Name} {manifest.Version}" + (plan.InstalledVersion is { } installed ? $"   (this project has {installed}: it is replaced)" : ""));
        if (manifest.Description is { Length: > 0 } description)
        {
            Gui.TextWrapped(description);
        }

        if (plan.HasCode)
        {
            ImGui.Spacing();
            Gui.TextColored(Amber, "This package contains C# code. It is compiled into your project and runs in this editor with your rights.");
            Gui.TextColored(Amber, "Import it only if you trust where it came from.");
        }

        ImGui.Spacing();
        var replaced = plan.Files.Count(f => f.ReplacesOtherFile);
        ImGui.TextUnformatted($"{plan.Files.Count} files" + (replaced > 0 ? $", {replaced} replace files of yours" : "") + (plan.Removed.Count > 0 ? $", {plan.Removed.Count} old files removed" : ""));
        if (plan.CreatesScriptsProject)
        {
            Gui.Muted("Creates Scripts/<project>.Scripts.csproj for its code (set as scripts.project).");
        }

        if (plan.CreatesEditorProject)
        {
            Gui.Muted("Creates Editor/<project>.Editor.csproj for its editor code (set as scripts.editorProject).");
        }

        if (ImGui.BeginChild("files", new SysVector2(0f, 260f), ImGuiChildFlags.Borders))
        {
            foreach (var file in plan.Files)
            {
                if (file.ReplacesOtherFile)
                {
                    Gui.TextColored(Amber, "replaces  " + file.Target);
                }
                else
                {
                    ImGui.TextUnformatted("          " + file.Target);
                }
            }

            foreach (var removed in plan.Removed)
            {
                Gui.TextDisabled("removes   " + removed);
            }
        }

        ImGui.EndChild();
        _errors = plan.Problems.Count > 0 ? plan.Problems : _errors;
        DrawErrors();
        ImGui.Spacing();
        ImGui.BeginDisabled(!plan.CanImport);
        if (ImGui.Button("Import", new SysVector2(110f, 0f)) && Import())
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Back", new SysVector2(110f, 0f)))
        {
            Close();
            _found = [.. Candidates()];
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new SysVector2(110f, 0f)))
        {
            Close();
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawErrors()
    {
        foreach (var error in _errors)
        {
            Gui.TextColored(Red, error);
        }
    }

    private void Load(string file)
    {
        Close();
        try
        {
            _package = EditorPackageFile.Open(file);
            _plan = EditorPackageImporter.Plan(context.Session.Project, _package);
        }
        catch (EditorPackageException ex)
        {
            _errors = ex.Problems;
        }
        catch (IOException ex)
        {
            _errors = [ex.Message];
        }
        catch (UnauthorizedAccessException ex)
        {
            _errors = [ex.Message];
        }
    }

    private bool Import()
    {
        try
        {
            var plan = EditorPackageImporter.Import(context.Session.Project, _package!, EngineLocation.FindRoot());
            EditorLog.PackageImported(context.Logger, plan.Manifest.Name, plan.Manifest.Version, plan.Files.Count, plan.Removed.Count, plan.CreatesEditorProject);
            if (plan.CreatesScriptsProject || plan.CreatesEditorProject)
            {
                scripts.ReloadProjectSettings();
            }
            else
            {
                scripts.Rebuild();
            }

            Close();
            return true;
        }
        catch (Exception ex) when (ex is EditorPackageException or IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            EditorLog.ActionFailed(context.Logger, ex, "Importing the package");
            _errors = ex is EditorPackageException package ? package.Problems : [ex.Message];
            return false;
        }
    }

    private void Close()
    {
        _package?.Dispose();
        (_package, _plan, _errors) = (null, null, []);
    }

    private static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var folder in new[] { Path.Combine(home, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) }.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = [.. Directory.EnumerateFiles(folder, "*" + Bundles.ContentBundleFile.Extension).Take(50)];
            }
            catch (UnauthorizedAccessException)
            {
                continue; // a folder this account cannot list has nothing to offer
            }

            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }
}
