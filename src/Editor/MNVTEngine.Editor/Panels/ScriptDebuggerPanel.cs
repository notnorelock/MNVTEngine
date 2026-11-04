using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// State of <c>.mnvts</c> scripts in Play Mode: where execution stopped (with call stack and locals), globals, mission
/// and objective states, and live coroutines. F5 continues, F10 steps. Breakpoints are set by clicking lines in a script
/// asset's inspector.
/// </summary>
internal sealed class ScriptDebuggerPanel(EditorContext context)
{
    private static readonly SysVector4 StopColor = new(1f, 0.8f, 0.3f, 1f);
    private ScriptBreak? _shown;
    private int _focusFrames;

    public void Draw()
    {
        var debugger = context.ScriptDebugger;
        if (debugger.Break is { } stop && !ReferenceEquals(stop, _shown))
        {
            _focusFrames = 6; // bring the panel forward once per stop; outlasts the Game view's focus on entering Play Mode
        }

        _shown = debugger.Break;
        if (!ImGui.Begin("Script Debugger"))
        {
            ImGui.End();
            return;
        }

        if (debugger.Break is { } current)
        {
            var asset = context.Session.Assets.Get(current.Script)?.Path ?? current.Script.ToString();
            Gui.TextColored(StopColor, $"Stopped at {asset}:{current.Line} in '{current.Fiber.Name}'");
            if (ImGui.Button("Continue (F5)"))
            {
                debugger.Continue();
            }

            ImGui.SameLine();
            if (ImGui.Button("Step (F10)"))
            {
                debugger.Step();
            }

            if (ImGui.CollapsingHeader("Call stack", ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (var frame in current.Fiber.GetStackTrace())
                {
                    Gui.Bullet($"at {frame.Function} ({PathOf(frame.Source)}:{frame.Line})");
                }
            }

            if (ImGui.CollapsingHeader("Locals", ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (var (name, value) in current.Fiber.GetLocals())
                {
                    ImGui.TextUnformatted($"{name} = {value}");
                }
            }
        }
        else if (context.PlayMode.IsPlaying)
        {
            Gui.Muted("Running. Click a line number in a script's inspector to set a breakpoint.");
        }
        else
        {
            Gui.Muted("Enter Play Mode to debug scripts. Breakpoints:");
            foreach (var (script, line) in debugger.Breakpoints.OrderBy(b => b.Line))
            {
                Gui.Bullet($"{context.Session.Assets.Get(script)?.Path ?? script.ToString()}:{line}");
            }
        }

        if (context.PlayMode.PlayScene is { } scene)
        {
            foreach (var runner in scene.FindObjectsOfType<MissionRunner>())
            {
                DrawRunner(runner);
            }
        }

        ImGui.End();
    }

    /// <summary>Call after every other panel has drawn, so a new stop wins over other focus requests.</summary>
    public void ApplyFocus()
    {
        if (_focusFrames > 0)
        {
            _focusFrames--;
            ImGui.SetWindowFocus("Script Debugger");
        }
    }

    /// <summary>Scripts compile with their asset reference as source name; show the asset path instead.</summary>
    private string PathOf(string source) =>
        AssetRef.TryParse(source, out var asset) && context.Session.Assets.Get(asset.Guid) is { } record ? record.Path : source;

    private void DrawRunner(MissionRunner runner)
    {
        if (runner.Vm is not { } vm || !ImGui.CollapsingHeader($"{runner.GameObject.Name}: {PathOf(runner.Script.ToString())}", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        ImGui.PushID(runner.Id.ToString());
        if (ImGui.TreeNode("Globals"))
        {
            foreach (var global in vm.Script.Globals)
            {
                ImGui.TextUnformatted($"{global.Name} = {vm.GetGlobal(global.Name)}");
            }

            ImGui.TreePop();
        }

        if (runner.Missions is { } missions && ImGui.TreeNodeEx("Missions", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var (id, mission) in missions.Missions)
            {
                if (ImGui.TreeNodeEx($"{id}: {mission.State}", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    foreach (var (objective, state) in mission.Objectives)
                    {
                        Gui.Bullet($"{objective}: {state.State}");
                    }

                    ImGui.TreePop();
                }
            }

            ImGui.TreePop();
        }

        if (ImGui.TreeNode($"Coroutines ({vm.Fibers.Count})"))
        {
            foreach (var fiber in vm.Fibers)
            {
                var stack = fiber.GetStackTrace();
                var top = stack.Count > 0 ? stack[0] : null;
                Gui.Bullet($"{fiber.Name}: {fiber.State}{(top is null ? "" : $" at line {top.Line}")}");
            }

            ImGui.TreePop();
        }

        ImGui.PopID();
    }
}
