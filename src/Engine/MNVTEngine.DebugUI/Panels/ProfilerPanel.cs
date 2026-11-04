using System.Globalization;
using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Core.Diagnostics;
using DiagnosticMetrics = MNVTEngine.Diagnostics.Metrics;
using SysVector2 = System.Numerics.Vector2;

namespace MNVTEngine.DebugUI.Panels;

/// <summary>
/// Frame times (rolling min/p50/p95/p99/max), history graphs, CPU samples per system, GPU scopes from timer queries,
/// profiling detail, snapshots (frozen summaries) and report files.
/// </summary>
public sealed class ProfilerPanel() : DebugPanel("Profiler")
{
    private static readonly string[] Windows = ["60", "120", "300", "600"];
    private static readonly string[] Details = Enum.GetNames<ProfilerDetail>();
    private int _window = 1;
    private ProfileSummary? _snapshot;
    private string _status = "";
    private ProfileSummary? _cached;
    private long _cachedAt;
    private int _cachedWindow;

    protected override SysVector2 DefaultSize => new(520, 560);

    protected override SysVector2 DefaultPosition(SysVector2 display, int index) => new(display.X - 540, 20);

    protected override void Draw()
    {
        if (Diagnostics.Profiler is not { } profiler)
        {
            Gui.TextWrapped("The profiler is off (diagnostics.enableProfiler in the project).");
            return;
        }

        ImGui.SetNextItemWidth(70);
        ImGui.Combo("frames", ref _window, Windows, Windows.Length);
        ImGui.SameLine();
        var detail = (int)Profiler.Detail;
        ImGui.SetNextItemWidth(110);
        if (ImGui.Combo("detail", ref detail, Details, Details.Length))
        {
            Profiler.Detail = (ProfilerDetail)detail;
        }

        ImGui.SameLine();
        if (ImGui.Button(_snapshot is null ? "Snapshot" : "Live"))
        {
            _snapshot = _snapshot is null ? profiler.Summarize(int.Parse(Windows[_window], CultureInfo.InvariantCulture)) : null;
        }

        ImGui.SameLine();
        if (ImGui.Button("Capture to file"))
        {
            _status = Diagnostics.Commands.Execute("profiler capture").Output;
        }

        if (_status.Length > 0)
        {
            Gui.TextColored(Dim, _status);
        }

        // Summaries walk hundreds of frames: refreshed 4 times per second, not every frame.
        if (_cached is null || Diagnostics.Frame - _cachedAt >= 15 || _cachedWindow != _window)
        {
            (_cached, _cachedAt, _cachedWindow) = (profiler.Summarize(int.Parse(Windows[_window], CultureInfo.InvariantCulture)), Diagnostics.Frame, _window);
        }

        var summary = _snapshot ?? _cached;
        ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"Frame {Diagnostics.Frame}{(_snapshot is null ? "" : "  (snapshot)")}"));
        ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture,
            $"CPU per frame: avg {summary.AverageFrameMilliseconds:0.00} ms   min {summary.MinFrameMilliseconds:0.00}   p50 {summary.P50FrameMilliseconds:0.00}   p95 {summary.P95FrameMilliseconds:0.00}   p99 {summary.P99FrameMilliseconds:0.00}   max {summary.MaxFrameMilliseconds:0.00}"));
        Gui.TextColored(Dim, string.Create(CultureInfo.InvariantCulture,
            $"allocated {summary.AverageAllocatedKilobytes:0.0} KB/frame   GC gen0 {summary.Gen0Collections}  gen1 {summary.Gen1Collections}  gen2 {summary.Gen2Collections}"));

        Graph(DiagnosticMetrics.FrameTime, "Frame");
        Graph(DiagnosticMetrics.CpuTime, "CPU", 45f);
        Graph(DiagnosticMetrics.GpuTime, "GPU", 45f);

        ImGui.SeparatorText("CPU samples");
        if (ImGui.BeginTable("##samples", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new SysVector2(0, 220)))
        {
            ImGui.TableSetupColumn("Sample", ImGuiTableColumnFlags.WidthStretch, 3f);
            ImGui.TableSetupColumn("avg ms");
            ImGui.TableSetupColumn("max ms");
            ImGui.TableSetupColumn("calls");
            ImGui.TableHeadersRow();
            foreach (var sample in summary.Samples)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(new string(' ', sample.Depth * 2) + sample.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sample.AverageMilliseconds.ToString("0.000", CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sample.MaxMilliseconds.ToString("0.000", CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sample.AverageCalls.ToString("0.#", CultureInfo.InvariantCulture));
            }

            ImGui.EndTable();
        }

        ImGui.SeparatorText("GPU");
        var gpu = Diagnostics.GpuTimer;
        if (Diagnostics.Target?.Device.GpuTimer is null)
        {
            Gui.TextColored(Dim, "GPU timing is not supported by this graphics backend.");
        }
        else if (gpu is null)
        {
            Gui.TextColored(Dim, "GPU timing is off (diagnostics.enableGpuTiming).");
        }
        else if (gpu.LastFrameIndex < 0)
        {
            Gui.TextColored(Dim, "Waiting for the first timer query results...");
        }
        else
        {
            ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"GPU frame {gpu.LastFrameMilliseconds:0.000} ms  (frame {gpu.LastFrameIndex}, {gpu.Latency} frames late, {gpu.DroppedFrames} dropped)"));
            foreach (var scope in gpu.LastScopes)
            {
                Gui.Bullet(string.Create(CultureInfo.InvariantCulture, $"{scope.Name}: {scope.Milliseconds:0.000} ms"));
            }
        }

        if (summary.AverageCounters.Count > 0)
        {
            ImGui.SeparatorText("Counters");
            foreach (var (name, value) in summary.AverageCounters.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                Gui.Bullet(string.Create(CultureInfo.InvariantCulture, $"{name}: {value:0.#}"));
            }
        }
    }
}
