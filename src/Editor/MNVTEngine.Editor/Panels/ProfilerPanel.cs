using System.Globalization;
using ImGuiNET;
using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Diagnostics;
using MNVTEngine.Editor.ImGuiBackend;
using SysVector2 = System.Numerics.Vector2;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// Frame times of the editor (and the Play Mode scene it runs), the time per profiled region averaged over the last
/// second, counters, allocations and collections. Pause freezes the view; Save writes a text report to Library/Profiles.
/// </summary>
internal sealed partial class ProfilerPanel(EditorContext context, FrameProfiler profiler)
{
    private const int WindowFrames = 60;
    private bool _paused;
    private ProfileSummary? _frozen;
    private float[] _plot = [];

    public void Draw()
    {
        if (!ImGui.Begin("Profiler"))
        {
            ImGui.End();
            return;
        }

        if (ImGui.Checkbox("Pause", ref _paused))
        {
            _frozen = _paused ? profiler.Summarize(WindowFrames) : null;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Save report"))
        {
            SaveReport();
        }

        var summary = _frozen ?? profiler.Summarize(WindowFrames);
        if (!_paused)
        {
            _plot = [.. profiler.Frames.Select(f => (float)f.Milliseconds)];
        }

        ImGui.SameLine();
        Gui.Muted(string.Create(CultureInfo.InvariantCulture, $"{summary.AverageFrameMilliseconds:0.00} ms ({summary.AverageFps:0} fps), p95 {summary.P95FrameMilliseconds:0.00} ms, {summary.AverageAllocatedKilobytes:0.0} KB/frame, GC {summary.Gen0Collections}/{summary.Gen1Collections}/{summary.Gen2Collections}"));
        if (_plot.Length > 1)
        {
            ImGui.PlotLines("##frames", ref _plot[0], _plot.Length, 0, "frame ms", 0f, Math.Max(33.4f, _plot.Max()), new SysVector2(ImGui.GetContentRegionAvail().X, 60f));
        }

        if (ImGui.BeginTable("##samples", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Region", ImGuiTableColumnFlags.WidthStretch, 3f);
            ImGui.TableSetupColumn("avg ms");
            ImGui.TableSetupColumn("max ms");
            ImGui.TableSetupColumn("calls");
            ImGui.TableHeadersRow();
            foreach (var sample in summary.Samples)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(new string(' ', sample.Depth * 3) + sample.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sample.AverageMilliseconds.ToString("0.000", CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sample.MaxMilliseconds.ToString("0.000", CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sample.AverageCalls.ToString("0.#", CultureInfo.InvariantCulture));
            }

            foreach (var (name, value) in summary.AverageCounters.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                Gui.Muted(name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(value.ToString("0.#", CultureInfo.InvariantCulture));
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }

    private void SaveReport()
    {
        var folder = Path.Combine(context.Session.Project.RootDirectory, "Library", "Profiles");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"profile-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            File.WriteAllText(path, profiler.FormatReport());
            LogSaved(context.Logger, path);
        }
        catch (IOException ex)
        {
            LogSaveFailed(context.Logger, ex, path);
        }
    }

    [LoggerMessage(EventId = 5300, Level = LogLevel.Information, Message = "Profile saved to {Path}")]
    private static partial void LogSaved(ILogger logger, string path);

    [LoggerMessage(EventId = 5301, Level = LogLevel.Error, Message = "Profile could not be saved to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}
