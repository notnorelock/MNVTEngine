using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Diagnostics;
using MNVTEngine.Mathematics;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using DiagnosticMetrics = MNVTEngine.Diagnostics.Metrics;
using SysVector2 = System.Numerics.Vector2;

namespace MNVTEngine.DebugUI.Panels;

/// <summary>Every budget with its live value, usage, severity and peak; limits editable for this session.</summary>
public sealed class BudgetsPanel() : DebugPanel("Budgets")
{
    protected override SysVector2 DefaultSize => new(640, 420);

    protected override void Draw()
    {
        var budgets = Diagnostics.Budgets;
        var thresholds = budgets.Thresholds;
        Gui.TextColored(Dim, string.Create(CultureInfo.InvariantCulture,
            $"Thresholds: info {thresholds.Information * 100:0}%  warning {thresholds.Warning * 100:0}%  error {thresholds.Error * 100:0}%  critical {thresholds.Critical * 100:0}%   repeat every {budgets.RepeatInterval.TotalSeconds:0} s"));
        if (ImGui.Button("Reset peaks"))
        {
            budgets.ResetPeaks();
        }

        if (ImGui.BeginTable("##budgets", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit, new SysVector2(0, 240)))
        {
            foreach (var header in (string[])["Budget", "Subsystem", "Value", "Limit", "Used", "Peak", "State"])
            {
                ImGui.TableSetupColumn(header, header == "Limit" ? ImGuiTableColumnFlags.WidthFixed : ImGuiTableColumnFlags.None, header == "Limit" ? 120f : 0f);
            }

            ImGui.TableHeadersRow();
            foreach (var state in budgets.States)
            {
                var unit = DiagnosticMetrics.Find(state.Budget.Metric)?.Unit ?? MetricUnit.Count;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(state.Budget.Id);
                if (ImGui.IsItemHovered())
                {
                    Gui.Tooltip(state.Budget.Hint);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(state.Budget.Subsystem);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(DiagnosticMetrics.Format(state.Value, unit));
                ImGui.TableNextColumn();
                var limit = state.Budget.Limit;
                ImGui.SetNextItemWidth(110f);
                if (ImGuiBackend.Gui.InputNumber("##" + state.Budget.Id, ref limit, unit == MetricUnit.Bytes ? "0" : "0.##") && limit > 0)
                {
                    budgets.SetLimit(state.Budget.Id, limit);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(double.IsNaN(state.Ratio) ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{state.Ratio * 100:0.0}%"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"{state.PeakRatio * 100:0}%"));
                ImGui.TableNextColumn();
                Gui.TextColored(SeverityColor(state.Severity), state.Value.IsMeasured ? state.Severity.ToString() : state.Value.Status.ToString());
            }

            ImGui.EndTable();
        }

        ImGui.SeparatorText("Recent warnings");
        foreach (var warning in budgets.RecentWarnings.Reverse())
        {
            Gui.TextColored(SeverityColor(warning.Severity), string.Create(CultureInfo.InvariantCulture, $"[{warning.Severity.ToString().ToUpperInvariant()}] frame {warning.Frame}: {warning.Message}"));
        }
    }
}

/// <summary>Draw calls (game vs. debug UI), geometry, state switches, GPU resources and memory.</summary>
public sealed class RendererPanel() : DebugPanel("Renderer")
{
    protected override void Draw()
    {
        if (Diagnostics.Target?.Device is not { } device)
        {
            return;
        }

        Gui.TextWrapped(device.Description);
        var game = Diagnostics.SceneStatistics;
        var total = Diagnostics.TotalStatistics;
        if (ImGui.BeginTable("##frame", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Last frame");
            ImGui.TableSetupColumn("Game");
            ImGui.TableSetupColumn("With debug UI");
            ImGui.TableHeadersRow();
            Row("Draw calls", game.DrawCalls, total.DrawCalls);
            Row("Vertices uploaded", game.VerticesUploaded, total.VerticesUploaded);
            Row("Indices", game.Indices, total.Indices);
            Row("Triangles", game.Triangles, total.Triangles);
            Row("Lines", game.Lines, total.Lines);
            Row("Texture switches", game.TextureSwitches, total.TextureSwitches);
            Row("Shader switches", game.ShaderSwitches, total.ShaderSwitches);
            Row("Target switches", game.RenderTargetSwitches, total.RenderTargetSwitches);
            ImGui.EndTable();
        }

        MetricRow(DiagnosticMetrics.SpriteBatches);
        MetricRow(DiagnosticMetrics.Sprites);
        MetricRow(DiagnosticMetrics.SpritesCulled);
        ImGui.SeparatorText("GPU resources");
        var counts = device.Resources.Count();
        ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"Textures {counts.Textures} (render targets {counts.RenderTargets}), shaders {counts.Shaders}, buffers {counts.Buffers}"));
        ImGui.TextUnformatted($"Texture memory (estimated) {DiagnosticMetrics.FormatBytes(counts.TextureBytes)}");
        Diagnostics.GpuMemoryRequested = true;
        MetricRow(DiagnosticMetrics.GpuMemoryUsed);
        MetricRow(DiagnosticMetrics.GpuMemoryTotal);
        Gui.TextColored(Dim, $"GPU memory source: {Diagnostics.GpuMemorySource ?? "none"} (all processes; sampled every 5 s while shown: the query stalls the GPU)");
        MetricRow(DiagnosticMetrics.DebugDrawCommands);
        MetricRow(DiagnosticMetrics.DebugDrawDropped);
        Graph(DiagnosticMetrics.DrawCalls, "Draw calls", 50f);

        static void Row(string name, long game, long total)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(game.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(total.ToString("N0", CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>Entity hierarchy, component inspection (read-only, serialized members) and systems with their timings.</summary>
public sealed class EcsPanel() : DebugPanel("ECS")
{
    private Guid _selected;
    private string _filter = "";

    protected override SysVector2 DefaultSize => new(620, 460);

    /// <summary>Highlights the selected object in the game every frame the panel is open.</summary>
    protected override void Draw()
    {
        if (Scene is not { } scene)
        {
            Gui.TextColored(Dim, "No scene is running.");
            return;
        }

        MetricRow(DiagnosticMetrics.Entities);
        MetricRow(DiagnosticMetrics.Components);
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##filter", "Filter by name", ref _filter, 64);
        if (ImGui.BeginTable("##ecs", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV, new SysVector2(0, -1)))
        {
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("##tree"))
            {
                foreach (var root in scene.RootObjects)
                {
                    Tree(root);
                }

                ImGui.SeparatorText("Systems");
                var summary = Diagnostics.Profiler?.Summarize(60);
                foreach (var system in scene.Systems.All)
                {
                    var time = summary?.Samples.Where(s => s.Name == system.ProfileName).Sum(s => s.AverageMilliseconds);
                    Gui.Bullet(string.Create(CultureInfo.InvariantCulture, $"{system.GetType().Name} (order {system.Order}){(system.Enabled ? "" : " disabled")}{(time is > 0 ? $"  {time:0.000} ms" : "")}"));
                }
            }

            ImGui.EndChild();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("##inspector") && scene.FindById(_selected) is { } selected)
            {
                Inspect(selected);
            }

            ImGui.EndChild();
            ImGui.EndTable();
        }
    }

    private void Tree(GameObject gameObject)
    {
        var children = gameObject.Transform.Children;
        if (_filter.Length > 0 && !gameObject.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var child in children)
            {
                Tree(child.GameObject);
            }

            return;
        }

        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | (children.Count == 0 ? ImGuiTreeNodeFlags.Leaf : 0)
            | (gameObject.Id == _selected ? ImGuiTreeNodeFlags.Selected : 0);
        using (ImGuiBackend.Gui.StyleColor(ImGuiCol.Text, Dim, !gameObject.ActiveInHierarchy))
        {
            var open = ImGui.TreeNodeEx(gameObject.Id.ToString(), flags, gameObject.Name);
            if (ImGui.IsItemClicked())
            {
                _selected = gameObject.Id;
            }

            if (open)
            {
                foreach (var child in children)
                {
                    Tree(child.GameObject);
                }

                ImGui.TreePop();
            }
        }
    }

    private static void Inspect(GameObject gameObject)
    {
        Debug.DrawCircle(gameObject.Transform.Position, 0.35f, Color.Cyan, thickness: 2f);
        Debug.DrawLabel(gameObject.Name, gameObject.Transform.Position + new Vector2(0, 0.6f), Color.Cyan);
        ImGui.TextUnformatted(gameObject.Name);
        Gui.TextColored(Dim, $"{gameObject.Id}  {(gameObject.ActiveInHierarchy ? "active" : "inactive")}");
        ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"Position {gameObject.Transform.Position}  rotation {gameObject.Transform.Rotation:0.#}°  scale {gameObject.Transform.LossyScale}"));
        foreach (var component in gameObject.Components.Skip(1))
        {
            var type = component.GetType();
            if (!ImGui.CollapsingHeader($"{type.Name}{(component.Enabled ? "" : " (disabled)")}##{component.Id}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                continue;
            }

            foreach (var member in ComponentTypeInfo.Get(type).SerializableMembers)
            {
                object? value = member switch
                {
                    FieldInfo field => field.GetValue(component),
                    PropertyInfo property => property.GetValue(component),
                    _ => null,
                };
                Gui.TextColored(Dim, member.Name);
                ImGui.SameLine(ImGui.GetFontSize() * 9);
                ImGui.TextUnformatted(Describe(value));
            }
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => '"' + text + '"',
        float number => number.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        System.Collections.ICollection collection => $"{collection.Count} items",
        _ => value.ToString() ?? "",
    };
}

/// <summary>Heap, process, GPU and asset memory with history graphs and GC activity.</summary>
public sealed class MemoryPanel() : DebugPanel("Memory")
{
    protected override SysVector2 DefaultSize => new(460, 520);

    protected override void Draw()
    {
        Diagnostics.GpuMemoryRequested = true;
        Graph(DiagnosticMetrics.ManagedHeap, "Managed heap");
        Graph(DiagnosticMetrics.ProcessMemory, "Process");
        Graph(DiagnosticMetrics.AllocatedPerFrame, "Allocated / frame", 45f);
        foreach (var id in (string[])[DiagnosticMetrics.ManagedHeap, DiagnosticMetrics.ProcessMemory, DiagnosticMetrics.AllocatedPerFrame, DiagnosticMetrics.GpuMemoryUsed, DiagnosticMetrics.GpuMemoryTotal,
            DiagnosticMetrics.TextureMemory, DiagnosticMetrics.AudioMemory, DiagnosticMetrics.AssetMemory, DiagnosticMetrics.Gen0Collections, DiagnosticMetrics.Gen1Collections, DiagnosticMetrics.Gen2Collections, DiagnosticMetrics.CollectionsPerMinute])
        {
            MetricRow(id);
        }

        var info = GC.GetGCMemoryInfo();
        Gui.TextColored(Dim, $"GC heap {DiagnosticMetrics.FormatBytes(info.HeapSizeBytes)}, fragmented {DiagnosticMetrics.FormatBytes(info.FragmentedBytes)}, pause {info.PauseTimePercentage:0.##}% of time");
        if (ImGui.Button("Collect garbage now"))
        {
            Diagnostics.Commands.Execute("gc");
        }

        ImGui.SeparatorText("Memory budgets");
        foreach (var state in Diagnostics.Budgets.States.Where(s => s.Budget.Subsystem == "Memory"))
        {
            var unit = DiagnosticMetrics.Find(state.Budget.Metric)?.Unit ?? MetricUnit.Count;
            Gui.TextColored(SeverityColor(state.Severity), string.Create(CultureInfo.InvariantCulture,
                $"{state.Budget.Id,-20} {(double.IsNaN(state.Ratio) ? "n/a" : $"{state.Ratio * 100:0}%"),6}  of {DiagnosticMetrics.Format(MetricValue.Measured(state.Budget.Limit), unit)}"));
        }
    }
}

/// <summary>Loaded game assemblies, C# script components, .mnvts VMs (coroutines, missions) and recent script exceptions.</summary>
public sealed class ScriptingPanel() : DebugPanel("Scripting")
{
    protected override SysVector2 DefaultSize => new(560, 480);

    protected override void Draw()
    {
        MetricRow(DiagnosticMetrics.CSharpScripts);
        MetricRow(DiagnosticMetrics.ScriptVms);
        MetricRow(DiagnosticMetrics.ScriptFibers);
        var summary = Diagnostics.Profiler?.Summarize(60);
        foreach (var name in (string[])["Scripts.Update", "Scripts.FixedUpdate", "Scripts.LateUpdate", "Scripts.Missions"])
        {
            var time = summary?.Samples.Where(s => s.Name == name).Sum(s => s.AverageMilliseconds);
            Gui.TextColored(Dim, string.Create(CultureInfo.InvariantCulture, $"{name,-22} {(time is { } t ? $"{t:0.000} ms" : "n/a")}"));
        }

        if (Core.Diagnostics.Profiler.Detail < Core.Diagnostics.ProfilerDetail.Scripts)
        {
            Gui.TextColored(Dim, "Per-script timing: set the profiler detail to Scripts.");
        }

        if (ImGui.CollapsingHeader("Game assemblies"))
        {
            foreach (var context in AssemblyLoadContext.All)
            {
                var game = context.Assemblies.Where(a => RuntimeDiagnostics.IsGameCode(a.GetTypes().FirstOrDefault() ?? typeof(object))
                    && a.GetName().Name is { } n && !n.StartsWith("System", StringComparison.Ordinal) && !n.StartsWith("Microsoft", StringComparison.Ordinal)
                    && !n.StartsWith("Newtonsoft", StringComparison.Ordinal) && !n.StartsWith("Silk", StringComparison.Ordinal) && !n.StartsWith("ImGui", StringComparison.Ordinal)
                    && !n.StartsWith("Box2D", StringComparison.Ordinal) && !n.StartsWith("NVorbis", StringComparison.Ordinal) && !n.StartsWith("Stb", StringComparison.Ordinal)
                    && !n.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("netstandard", StringComparison.Ordinal)).ToList();
                foreach (var assembly in game)
                {
                    Gui.Bullet($"{assembly.GetName().Name} {assembly.GetName().Version}  ({context.Name ?? "default"}{(context.IsCollectible ? ", reloadable" : "")})");
                }
            }
        }

        if (Scene is not { } scene)
        {
            return;
        }

        if (ImGui.CollapsingHeader("C# script components", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var group in scene.GameObjects.SelectMany(g => g.Components).Where(c => RuntimeDiagnostics.IsGameCode(c.GetType())).GroupBy(c => c.GetType()))
            {
                var time = summary?.Samples.Where(s => s.Name.StartsWith(group.Key.Name + ".", StringComparison.Ordinal)).Sum(s => s.AverageMilliseconds);
                Gui.Bullet(string.Create(CultureInfo.InvariantCulture, $"{group.Key.FullName} x{group.Count()} ({group.Count(c => c.IsActiveAndEnabled)} running){(time is > 0 ? $"  {time:0.000} ms" : "")}"));
            }
        }

        if (ImGui.CollapsingHeader(".mnvts VMs", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var runner in scene.FindObjectsOfType<MissionRunner>())
            {
                if (!ImGui.TreeNode($"{runner.GameObject.Name}##{runner.Id}"))
                {
                    continue;
                }

                if (runner.Vm is not { } vm)
                {
                    Gui.TextColored(Dim, runner.Enabled ? "not started" : "disabled (see the log)");
                }
                else
                {
                    var byState = vm.Fibers.GroupBy(f => f.State).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}");
                    ImGui.TextUnformatted($"Coroutines: {vm.Fibers.Count} ({string.Join(", ", byState)})");
                    foreach (var fiber in vm.Fibers.Take(40))
                    {
                        Gui.TextColored(Dim, $"  {fiber.Name}: {fiber.State}");
                    }
                }

                foreach (var mission in runner.Missions?.Missions.Values ?? [])
                {
                    ImGui.TextUnformatted($"Mission {mission.Info.Id}: {mission.State}");
                    foreach (var objective in mission.Objectives.Values)
                    {
                        Gui.TextColored(Dim, $"    {objective.Info.Id}: {objective.State}");
                    }
                }

                ImGui.TreePop();
            }
        }

        if (ImGui.CollapsingHeader($"Script exceptions ({scene.ScriptExceptionCount})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var error in scene.RecentScriptErrors.Reverse())
            {
                Gui.TextColored(SeverityColor(BudgetSeverity.Error), $"{error.Time.ToLocalTime():HH:mm:ss} {error.Component}.{error.Callback} on '{error.GameObject}': {error.Exception.Message}");
                if (ImGui.IsItemHovered())
                {
                    Gui.Tooltip(error.Exception.ToString());
                }
            }
        }
    }
}

/// <summary>Bodies, colliders, contacts and trigger overlaps, physics step time; optional collider outlines in the game.</summary>
public sealed class PhysicsPanel() : DebugPanel("Physics")
{
    /// <summary>Draws collider outlines every frame while on (even with the window closed).</summary>
    public bool ShowColliders { get; set; }

    protected override void Draw()
    {
        var showColliders = ShowColliders;
        if (ImGui.Checkbox("Show colliders in the game", ref showColliders))
        {
            ShowColliders = showColliders;
        }

        Gui.TextColored(Dim, "green dynamic, blue kinematic, gray static, yellow trigger");
        MetricRow(DiagnosticMetrics.PhysicsBodies);
        MetricRow(DiagnosticMetrics.PhysicsColliders);
        if (Scene?.Systems.Get<PhysicsWorld2D>() is { } world)
        {
            ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"Contacts {world.ContactCount}, trigger overlaps {world.TriggerOverlapCount}, gravity {world.Gravity}"));
            var summary = Diagnostics.Profiler?.Summarize(60);
            var time = summary?.Samples.Where(s => s.Name == world.ProfileName).Sum(s => s.AverageMilliseconds);
            ImGui.TextUnformatted(time is { } t ? string.Create(CultureInfo.InvariantCulture, $"Physics step {t:0.000} ms / frame") : "Physics step: n/a (profiler off)");
        }
        else
        {
            Gui.TextColored(Dim, "This scene has no physics.");
        }
    }

    internal void DrawColliders()
    {
        if (!ShowColliders || Scene is not { } scene)
        {
            return;
        }

        foreach (var collider in scene.FindObjectsOfType<Collider2D>())
        {
            if (!collider.IsActiveAndEnabled)
            {
                continue;
            }

            var body = collider.GameObject.GetComponent<Rigidbody2D>();
            var color = collider.IsTrigger ? Color.Yellow
                : body is null || body.BodyType == RigidbodyType2D.Static ? Color.Gray
                : body.BodyType == RigidbodyType2D.Kinematic ? new Color(0.4f, 0.6f, 1f)
                : Color.Green;
            var transform = collider.Transform;
            var scale = transform.LossyScale;
            var center = transform.Position + Rotate(Vector2.Scale(collider.Offset, scale), transform.Rotation);
            switch (collider)
            {
                case BoxCollider2D box:
                    var half = Vector2.Scale(box.Size, scale) * 0.5f;
                    var corners = (Vector2[])[new(-half.X, -half.Y), new(half.X, -half.Y), new(half.X, half.Y), new(-half.X, half.Y)];
                    for (var i = 0; i < 4; i++)
                    {
                        Debug.DrawLine(center + Rotate(corners[i], transform.Rotation), center + Rotate(corners[(i + 1) % 4], transform.Rotation), color);
                    }

                    break;
                case CircleCollider2D circle:
                    Debug.DrawCircle(center, circle.Radius * MathF.Max(MathF.Abs(scale.X), MathF.Abs(scale.Y)), color);
                    break;
                default:
                    Debug.DrawPoint(center, color); // tilemap colliders: marked, not outlined
                    break;
            }
        }
    }

    private static Vector2 Rotate(Vector2 v, float degrees)
    {
        var radians = degrees * MathF.PI / 180f;
        var (sin, cos) = MathF.SinCos(radians);
        return new Vector2((v.X * cos) - (v.Y * sin), (v.X * sin) + (v.Y * cos));
    }
}

/// <summary>Loaded textures (sizes, memory), audio clips, asset database records and failures.</summary>
public sealed class AssetsPanel() : DebugPanel("Assets")
{
    private string _filter = "";

    protected override SysVector2 DefaultSize => new(600, 460);

    protected override void Draw()
    {
        MetricRow(DiagnosticMetrics.AssetsLoaded);
        MetricRow(DiagnosticMetrics.AssetsFailed);
        MetricRow(DiagnosticMetrics.TextureMemory);
        MetricRow(DiagnosticMetrics.AudioMemory);
        var target = Diagnostics.Target;
        if (Scene is { } scene && AudioSystem.For(scene) is { } audio)
        {
            ImGui.TextUnformatted($"Audio clips {audio.ClipCount} ({audio.FailedClipCount} failed), {DiagnosticMetrics.FormatBytes(audio.BufferBytes)} decoded");
        }

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##filter", "Filter by path", ref _filter, 128);
        if (target?.Resources is { } resources && ImGui.CollapsingHeader("Loaded textures", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var rows = resources.LoadedTextures
                .Select(t => (Path: target.Assets?.Get(t.Asset)?.Path ?? t.Asset.ToString(), t.Texture))
                .Where(t => _filter.Length == 0 || t.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => (long)t.Texture.Width * t.Texture.Height)
                .ToList();
            if (ImGui.BeginTable("##textures", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV, new SysVector2(0, 160)))
            {
                ImGui.TableSetupColumn("Texture", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Size");
                ImGui.TableSetupColumn("Memory");
                ImGui.TableHeadersRow();
                foreach (var (path, texture) in rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(path);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"{texture.Width}x{texture.Height}"));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(DiagnosticMetrics.FormatBytes((long)texture.Width * texture.Height * 4));
                }

                ImGui.EndTable();
            }
        }

        if (target?.Assets is { } assets && ImGui.CollapsingHeader($"Asset database ({assets.Assets.Count} assets{(assets.IsPackaged ? ", packaged" : "")})"))
        {
            foreach (var group in assets.Assets.GroupBy(a => a.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Gui.Bullet($"{group.Key}: {group.Count()} ({DiagnosticMetrics.FormatBytes(group.Sum(a => a.Size))})");
            }

            foreach (var failed in assets.Assets.Where(a => a.Status == Assets.AssetImportStatus.Failed))
            {
                Gui.TextColored(SeverityColor(BudgetSeverity.Error), $"{failed.Path}: {string.Join("; ", failed.Messages)}");
            }

            foreach (var diagnostic in assets.Diagnostics)
            {
                Gui.TextColored(SeverityColor(diagnostic.Severity == Serialization.DiagnosticSeverity.Error ? BudgetSeverity.Error : BudgetSeverity.Warning), $"{diagnostic.Path}: {diagnostic.Message}");
            }
        }
    }
}
