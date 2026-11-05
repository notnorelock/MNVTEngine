using System.Globalization;
using System.Text;
using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Scripting.Syntax;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// Edits scripts and text assets inside the editor: tabs, Ctrl+S, live checking of .mnvts, C# compile errors per file
/// (saving a .cs file triggers the usual recompile and hot reload), clickable diagnostics that jump to their line, and
/// templates for new components and missions. "Open externally" hands the file to the system editor.
/// </summary>
internal sealed unsafe class ScriptEditorPanel(EditorContext context, ScriptReloadController scripts)
{
    private static readonly SysVector4 ErrorColor = new(1f, 0.45f, 0.45f, 1f);
    private static readonly SysVector4 WarningColor = new(1f, 0.8f, 0.35f, 1f);
    private static readonly TimeSpan CheckDelay = TimeSpan.FromMilliseconds(400);

    private readonly List<ScriptDocument> _documents = [];
    private readonly ImGuiInputTextCallback _callback = OnTextCallback;
    private ScriptDocument? _active;
    private ScriptDocument? _selectTab;
    private IReadOnlyList<Diagnostic> _diagnostics = [];
    private string _checkedText = "";
    private DateTime _textChangedAt;
    private string? _error;
    private string _newName = "";
    private int _focusFrames;
    private double _lastDiskCheck;

    // Shared with the static input callback (one text box is active at a time).
    private static int _cursorLine = 1;
    private static int _cursorColumn = 1;
    private static int? _jumpToByte;

    /// <summary>True while the Script Editor window has focus (Ctrl+S then saves the file, not the scene).</summary>
    public bool IsFocused { get; private set; }

    public bool HasUnsavedChanges => _documents.Any(d => d.IsDirty);

    public void Open(string fullPath)
    {
        var document = _documents.FirstOrDefault(d => string.Equals(d.FullPath, Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase));
        try
        {
            document ??= ScriptDocument.Open(fullPath);
        }
        catch (IOException ex)
        {
            _error = ex.Message;
            return;
        }

        if (!_documents.Contains(document))
        {
            _documents.Add(document);
        }

        _selectTab = document;
        _focusFrames = 3;
    }

    /// <summary>Saves the active document (Ctrl+S while focused).</summary>
    public void SaveActive()
    {
        if (_active is null)
        {
            return;
        }

        try
        {
            _active.Save();
            _error = null;
        }
        catch (IOException ex)
        {
            _error = ex.Message;
        }
    }

    public void Draw(double time)
    {
        if (time - _lastDiskCheck > 1.0)
        {
            _lastDiskCheck = time;
            foreach (var document in _documents)
            {
                if (document.CheckDisk())
                {
                    _error = $"{document.Name} changed on disk while it has unsaved edits here; saving overwrites the other change.";
                }
            }
        }

        if (_focusFrames > 0)
        {
            _focusFrames--;
            ImGui.SetWindowFocus(EditorWindows.Id("Script Editor"));
        }

        if (!ImGui.Begin(EditorWindows.Title("Script Editor")))
        {
            IsFocused = false;
            ImGui.End();
            return;
        }

        IsFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        DrawToolbar();
        if (_error is not null)
        {
            Gui.TextColored(ErrorColor, _error);
        }

        if (_documents.Count == 0)
        {
            Gui.Muted("Double-click a script or text asset in the Project panel, pick a C# script above, or create a new one.");
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("##scripts", ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.AutoSelectNewTabs))
        {
            foreach (var document in _documents.ToList())
            {
                var open = true;
                var flags = (document.IsDirty ? ImGuiTabItemFlags.UnsavedDocument : 0) | (ReferenceEquals(_selectTab, document) ? ImGuiTabItemFlags.SetSelected : 0);
                if (ImGui.BeginTabItem(document.Name + "##" + document.FullPath, ref open, flags))
                {
                    if (!ReferenceEquals(_active, document))
                    {
                        _active = document;
                        _checkedText = "";
                    }

                    DrawDocument(document);
                    ImGui.EndTabItem();
                }

                if (!open)
                {
                    Close(document);
                }
            }

            _selectTab = null;
            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private void DrawToolbar()
    {
        if (Gui.IconButton(Icons.Save, "Save (Ctrl+S)") && _active is not null)
        {
            SaveActive();
        }

        ImGui.SameLine();
        if (Gui.IconButton(Icons.ExternalLink, "Open in the external editor") && _active is not null)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_active.FullPath) { UseShellExecute = true })?.Dispose();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _error = ex.Message;
            }
        }

        ImGui.SameLine();
        if (ImGui.BeginCombo("##csharp", "C# scripts", ImGuiComboFlags.NoPreview | ImGuiComboFlags.HeightLarge))
        {
            foreach (var file in CSharpFiles())
            {
                if (ImGui.Selectable(Path.GetFileName(file)))
                {
                    Open(file);
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        Gui.Muted("C# scripts");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f);
        ImGui.InputTextWithHint("##newname", "NewName", ref _newName, 64);
        ImGui.SameLine();
        if (ImGui.Button($"{Icons.Plus}  C# component"))
        {
            Create(() => ScriptTemplates.CreateComponent(context.Session.Project, _newName.Trim()));
        }

        ImGui.SameLine();
        if (ImGui.Button($"{Icons.Plus}  Mission"))
        {
            Create(() => ScriptTemplates.CreateMission(context.Session.Project, _newName.Trim()));
        }
    }

    private void Create(Func<string> create)
    {
        try
        {
            Open(create());
            _newName = "";
            _error = null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _error = ex.Message;
        }
    }

    private void DrawDocument(ScriptDocument document)
    {
        var diagnostics = Diagnostics(document);
        var listHeight = diagnostics.Count == 0 ? 0f : Math.Min(6, diagnostics.Count) * ImGui.GetTextLineHeightWithSpacing() + 8f;
        var size = ImGui.GetContentRegionAvail() - new SysVector2(0, ImGui.GetFrameHeightWithSpacing() + listHeight);
        var text = document.Text;
        var capacity = (uint)(Encoding.UTF8.GetByteCount(text) + (64 * 1024));
        if (ImGui.InputTextMultiline("##text", ref text, capacity, size, ImGuiInputTextFlags.AllowTabInput | ImGuiInputTextFlags.CallbackAlways, _callback))
        {
            document.Text = text.ReplaceLineEndings("\n");
            _textChangedAt = DateTime.UtcNow;
        }

        var status = document.Kind switch
        {
            ScriptKind.Mission => "checked as you type",
            ScriptKind.CSharp => "compiled and hot-reloaded when saved",
            _ => "text",
        };
        Gui.Muted(string.Create(CultureInfo.InvariantCulture, $"Ln {_cursorLine}, Col {_cursorColumn}   {(document.IsDirty ? "unsaved" : "saved")}   {status}   {document.FullPath}"));

        if (diagnostics.Count > 0 && ImGui.BeginChild("##diagnostics", new SysVector2(0, listHeight), ImGuiChildFlags.Borders))
        {
            foreach (var (line, column, isError, message) in diagnostics)
            {
                ImGui.PushID($"{line}:{column}:{message}");
                using (Gui.StyleColor(ImGuiCol.Text, isError ? ErrorColor : WarningColor))
                {
                    if (ImGui.Selectable(string.Create(CultureInfo.InvariantCulture, $"{line}:{column}  {message}")))
                    {
                        _jumpToByte = ByteOffset(document.Text, line, column);
                        _focusFrames = 2;
                    }
                }

                ImGui.PopID();
            }
        }

        if (diagnostics.Count > 0)
        {
            ImGui.EndChild();
        }
    }

    /// <summary>.mnvts: compiled shortly after typing stops. C#: the last build's errors for this file.</summary>
    private List<(int Line, int Column, bool IsError, string Message)> Diagnostics(ScriptDocument document)
    {
        if (document.Kind == ScriptKind.Mission)
        {
            if (_checkedText != document.Text && DateTime.UtcNow - _textChangedAt > CheckDelay)
            {
                _checkedText = document.Text;
                _diagnostics = document.Check();
            }

            return [.. _diagnostics.Select(d => (d.Location.Line, d.Location.Column, d.Severity == DiagnosticSeverity.Error, $"{d.Code}: {d.Message}"))];
        }

        if (document.Kind == ScriptKind.CSharp)
        {
            return [.. scripts.Diagnostics
                .Where(d => d.File is { } file && string.Equals(Path.GetFullPath(file), document.FullPath, StringComparison.OrdinalIgnoreCase))
                .Select(d => (d.Line, d.Column, d.Severity == GameAssemblies.ScriptDiagnosticSeverity.Error, $"{d.Code}: {d.Message}"))];
        }

        return [];
    }

    private void Close(ScriptDocument document)
    {
        // shortcut: closing a tab with unsaved edits discards them; add a confirmation dialog when that bites.
        _documents.Remove(document);
        if (ReferenceEquals(_active, document))
        {
            _active = null;
        }
    }

    private IEnumerable<string> CSharpFiles()
    {
        var project = context.Session.Project;
        if (project.Scripts?.Project is not { Length: > 0 } relative)
        {
            return [];
        }

        var folder = Path.GetDirectoryName(project.ResolveProjectFile(relative))!;
        return Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>UTF-8 byte offset of a 1-based line and column (ImGui's text buffer is UTF-8).</summary>
    private static int ByteOffset(string text, int line, int column)
    {
        var index = 0;
        for (var l = 1; l < line && index < text.Length; l++)
        {
            var next = text.IndexOf('\n', index);
            index = next < 0 ? text.Length : next + 1;
        }

        index = Math.Min(text.Length, index + Math.Max(0, column - 1));
        return Encoding.UTF8.GetByteCount(text.AsSpan(0, index));
    }

    private static int OnTextCallback(ImGuiInputTextCallbackData* data)
    {
        if (_jumpToByte is { } target)
        {
            _jumpToByte = null;
            data->CursorPos = data->SelectionStart = data->SelectionEnd = Math.Min(target, data->BufTextLen);
        }

        // Line and column of the cursor, counted in the UTF-8 buffer.
        var line = 1;
        var column = 1;
        for (var i = 0; i < data->CursorPos && i < data->BufTextLen; i++)
        {
            var b = data->Buf[i];
            if (b == (byte)'\n')
            {
                line++;
                column = 1;
            }
            else if ((b & 0xC0) != 0x80)
            {
                column++;
            }
        }

        (_cursorLine, _cursorColumn) = (line, column);
        return 0;
    }
}
