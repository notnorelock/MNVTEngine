using System.Collections.Concurrent;
using System.Globalization;
using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using MNVTEngine.Diagnostics;
using MNVTEngine.Scenes;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.DebugUI.Panels;

/// <summary>
/// Developer console: the game's log (severity, logger, search filters; timestamps; stack traces; copy) interleaved with
/// command input and output. Commands come only from the diagnostics command registry ('help' lists them); Up/Down
/// recall history, Tab completes.
/// </summary>
public sealed unsafe class ConsolePanel : DebugPanel, IDisposable
{
    private const int MaxLines = 5000;
    private readonly ConcurrentQueue<LogEntry> _incoming = new();
    private readonly List<Line> _lines = [];
    private readonly List<string> _history = [];
    private readonly HashSet<string> _categories = new(StringComparer.Ordinal);
    private readonly ImGuiInputTextCallback _callback;
    private InMemoryLoggerProvider? _logs;
    private string _input = "";
    private string _search = "";
    private string _category = "";
    private bool _showDebug;
    private bool _showInfo = true;
    private bool _showWarnings = true;
    private bool _showErrors = true;
    private bool _timestamps = true;
    private bool _focusInput;
    private int _historyIndex = -1;
    private int _lastCount;

    public ConsolePanel()
        : base("Console") => _callback = OnInput;

    private sealed record Line(DateTimeOffset Time, LogLevel Level, string Category, string Text, string? Source, Exception? Exception, bool IsCommand = false);

    protected override SysVector2 DefaultSize => new(720, 300);

    protected override SysVector2 DefaultPosition(SysVector2 display, int index) => new((display.X - 720) / 2, display.Y - 320);

    protected override void OnAttached()
    {
        _logs = Overlay.Context.Logs;
        if (_logs is not null)
        {
            foreach (var entry in _logs.Snapshot())
            {
                _incoming.Enqueue(entry);
            }

            _logs.EntryAdded += _incoming.Enqueue;
        }
    }

    public void Dispose()
    {
        if (_logs is not null)
        {
            _logs.EntryAdded -= _incoming.Enqueue;
        }
    }

    public void Clear()
    {
        _lines.Clear();
        _categories.Clear();
    }

    /// <summary>Runs a console line as if typed (also echoed and recorded in the history).</summary>
    public DebugCommandResult Execute(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        Add(new Line(DateTimeOffset.Now, LogLevel.Information, "console", "> " + line, null, null, IsCommand: true));
        if (_history.Count == 0 || _history[^1] != line)
        {
            _history.Add(line);
        }

        var result = Diagnostics.Commands.Execute(line);
        if (result.Output.Length > 0)
        {
            Add(new Line(DateTimeOffset.Now, result.Success ? LogLevel.Information : LogLevel.Warning, "console", result.Output, null, null, IsCommand: true));
        }

        return result;
    }

    protected override void Draw()
    {
        Drain();
        if (ImGui.SmallButton("Clear"))
        {
            Clear();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Copy visible"))
        {
            ImGui.SetClipboardText(string.Join('\n', _lines.Where(Visible).Select(Format)));
        }

        ImGui.SameLine();
        ImGui.Checkbox("Debug", ref _showDebug);
        ImGui.SameLine();
        ImGui.Checkbox("Info", ref _showInfo);
        ImGui.SameLine();
        ImGui.Checkbox("Warn", ref _showWarnings);
        ImGui.SameLine();
        ImGui.Checkbox("Error", ref _showErrors);
        ImGui.SameLine();
        ImGui.Checkbox("Time", ref _timestamps);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo("##category", _category.Length == 0 ? "All loggers" : _category))
        {
            if (ImGui.Selectable("All loggers", _category.Length == 0))
            {
                _category = "";
            }

            foreach (var category in _categories.Order(StringComparer.Ordinal))
            {
                if (ImGui.Selectable(category, category == _category))
                {
                    _category = category;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search", ref _search, 128);

        var inputHeight = ImGui.GetFrameHeightWithSpacing() + 4;
        if (ImGui.BeginChild("##lines", new SysVector2(0, -inputHeight), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
        {
            var atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f;
            for (var i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                if (!Visible(line))
                {
                    continue;
                }

                ImGui.PushID(i);
                using (ImGuiBackend.Gui.StyleColor(ImGuiCol.Text, Color(line)))
                {
                    ImGui.Selectable(Format(line), false, ImGuiSelectableFlags.AllowOverlap);
                }

                if (ImGui.IsItemHovered() && (line.Source is not null || line.Exception is not null))
                {
                    Gui.Tooltip($"{line.Category}{(line.Source is null ? "" : "\n" + line.Source)}{(line.Exception is null ? "" : "\n\n" + line.Exception)}");
                }

                if (ImGui.BeginPopupContextItem("##line"))
                {
                    if (ImGui.MenuItem("Copy"))
                    {
                        ImGui.SetClipboardText(Format(line) + (line.Exception is null ? "" : "\n" + line.Exception));
                    }

                    if (line.Exception is not null && ImGui.MenuItem("Copy stack trace"))
                    {
                        ImGui.SetClipboardText(line.Exception.ToString());
                    }

                    ImGui.EndPopup();
                }

                ImGui.PopID();
            }

            if (_lines.Count != _lastCount && atBottom)
            {
                ImGui.SetScrollHereY(1f);
            }

            _lastCount = _lines.Count;
        }

        ImGui.EndChild();

        if (_focusInput || ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
            _focusInput = false;
        }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##command", "Command ('help' lists them)", ref _input, 512,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackHistory | ImGuiInputTextFlags.CallbackCompletion, _callback))
        {
            var line = _input.Trim();
            _input = "";
            _historyIndex = -1;
            if (line.Length > 0)
            {
                Execute(line);
            }

            _focusInput = true;
        }
    }

    private void Drain()
    {
        while (_incoming.TryDequeue(out var entry))
        {
            var source = entry.Source is { } site
                ? site.File is { Length: > 0 } file ? string.Create(CultureInfo.InvariantCulture, $"{site} ({Path.GetFileName(file)}:{site.Line})") : site.ToString()
                : null;
            Add(new Line(entry.Timestamp.ToLocalTime(), entry.Level, entry.Category, entry.Message, source, entry.Exception));
        }
    }

    private void Add(Line line)
    {
        _lines.Add(line);
        _categories.Add(line.Category);
        if (_lines.Count > MaxLines)
        {
            _lines.RemoveRange(0, _lines.Count - MaxLines);
        }
    }

    private bool Visible(Line line)
    {
        var level = line.IsCommand || line.Level switch
        {
            <= LogLevel.Debug => _showDebug,
            LogLevel.Information => _showInfo,
            LogLevel.Warning => _showWarnings,
            _ => _showErrors,
        };
        return level
            && (_category.Length == 0 || line.IsCommand || line.Category == _category)
            && (_search.Length == 0 || line.Text.Contains(_search, StringComparison.OrdinalIgnoreCase) || line.Category.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || (line.Source?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private string Format(Line line)
    {
        var time = _timestamps ? line.Time.ToString("HH:mm:ss.fff ", CultureInfo.InvariantCulture) : "";
        return line.IsCommand ? time + line.Text : $"{time}{LogFormat.Level(line.Level)} [{line.Category}] {line.Text}";
    }

    private static SysVector4 Color(Line line) => line.IsCommand
        ? new SysVector4(0.6f, 0.85f, 1f, 1f)
        : line.Level switch
        {
            <= LogLevel.Debug => new SysVector4(0.55f, 0.57f, 0.62f, 1f),
            LogLevel.Information => new SysVector4(0.85f, 0.87f, 0.9f, 1f),
            LogLevel.Warning => new SysVector4(1f, 0.78f, 0.35f, 1f),
            _ => new SysVector4(1f, 0.42f, 0.42f, 1f),
        };

    private int OnInput(ImGuiInputTextCallbackData* data)
    {
        var text = new ImGuiInputTextCallbackDataPtr(data);
        if (data->EventFlag == ImGuiInputTextFlags.CallbackHistory && _history.Count > 0)
        {
            _historyIndex = data->EventKey == ImGuiKey.UpArrow
                ? (_historyIndex < 0 ? _history.Count - 1 : Math.Max(0, _historyIndex - 1))
                : (_historyIndex < 0 ? -1 : _historyIndex + 1 >= _history.Count ? -1 : _historyIndex + 1);
            text.DeleteChars(0, text.BufTextLen);
            text.InsertChars(0, _historyIndex < 0 ? "" : _history[_historyIndex]);
        }
        else if (data->EventFlag == ImGuiInputTextFlags.CallbackCompletion)
        {
            var typed = System.Text.Encoding.UTF8.GetString(data->Buf, data->BufTextLen);
            var matches = Diagnostics.Commands.Complete(typed.Trim()).ToList();
            if (matches.Count == 1)
            {
                text.DeleteChars(0, text.BufTextLen);
                text.InsertChars(0, matches[0] + " ");
            }
            else if (matches.Count > 1)
            {
                Add(new Line(DateTimeOffset.Now, LogLevel.Information, "console", string.Join("  ", matches), null, null, IsCommand: true));
            }
        }

        return 0;
    }
}
