using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Serialization;
using EngineVector2 = MNVTEngine.Mathematics.Vector2;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// The Animator window: edits an animator controller (<c>.mnvtanimator</c>) as a graph, like Unity's Animator window.
/// <list type="bullet">
/// <item>Parameters on the left: add, rename, type, default, delete.</item>
/// <item>The graph: states as nodes (the default one orange, the playing one green during Play Mode), "Any State",
/// transitions as arrows. Drag nodes, middle-drag (or Alt+drag) to pan; right-click for Add State, Make Transition,
/// Set as Default and Delete.</item>
/// <item>The inspector on the right edits the selected state or transition and its conditions.</item>
/// </list>
/// Ctrl+S saves and Ctrl+Z/Y undo and redo while the window has focus.
/// </summary>
internal sealed class AnimatorEditorPanel(EditorContext context)
{
    private const float NodeWidth = 160f, NodeHeight = 46f;
    private static readonly SysVector4 ErrorColor = new(1f, 0.45f, 0.45f, 1f);
    private static readonly uint NodeColor = Color(0.24f, 0.26f, 0.30f), DefaultColor = Color(0.85f, 0.5f, 0.15f), AnyColor = Color(0.16f, 0.55f, 0.65f);
    private static readonly uint PlayingColor = Color(0.25f, 0.75f, 0.35f), SelectedColor = Color(0.95f, 0.95f, 1f), ArrowColor = Color(0.8f, 0.82f, 0.86f);
    private AnimatorControllerDocument? _document;
    private Guid? _asset;
    private AnimatorGraphState? _selectedState;
    private bool _anySelected;
    private AnimatorGraphTransition? _selectedTransition;
    private (bool Active, string? From) _connecting;
    private AnimatorGraphState? _dragging;
    private bool _draggingAny;
    private SysVector2 _pan = new(20f, 20f);
    private Guid? _clipModel;
    private IReadOnlyList<string> _clips = [];
    private string? _error;
    private int _focusFrames;
    private SysVector2 _contextAt;

    public bool IsFocused { get; private set; }

    public bool HasUnsavedChanges => _document?.IsDirty == true;

    public void Open(string fullPath)
    {
        try
        {
            _document = new AnimatorControllerDocument(fullPath);
            _asset = context.Session.Assets.TryGetGuid(Path.GetRelativePath(context.Session.Assets.AssetsDirectory, fullPath).Replace('\\', '/'), out var guid) ? guid : null;
            (_selectedState, _selectedTransition, _anySelected, _connecting, _error) = (null, null, false, default, null);
            Frame(_document);
        }
        catch (Exception ex) when (ex is SerializationException or IOException or UnauthorizedAccessException)
        {
            _error = $"{Path.GetFileName(fullPath)}: {ex.Message}";
            EditorLog.ActionFailed(context.Logger, ex, $"Open animator controller {fullPath}");
        }

        _focusFrames = 3;
    }

    /// <summary>Pans so the top-left node sits at the canvas's top-left corner (with a margin).</summary>
    private void Frame(AnimatorControllerDocument document)
    {
        var positions = document.States.Select(s => s.Position).Append(document.AnyStatePosition).ToList();
        _pan = new SysVector2(20f - positions.Min(p => p.X), 20f - positions.Min(p => p.Y));
    }

    public void Save()
    {
        try
        {
            _document?.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error = ex.Message;
            EditorLog.ActionFailed(context.Logger, ex, $"Save animator controller {_document?.FullPath}");
        }
    }

    public void Undo() => Reselect(() => _document?.Undo());

    public void Redo() => Reselect(() => _document?.Redo());

    /// <summary>Undo and redo rebuild the objects: the selection follows by name.</summary>
    private void Reselect(Action change)
    {
        var state = _selectedState?.Name;
        change();
        _selectedState = state is null ? null : _document?.FindState(state);
        _selectedTransition = null;
    }

    public void Draw()
    {
        if (_focusFrames > 0)
        {
            _focusFrames--;
            ImGui.SetWindowFocus(EditorWindows.Id("Animator"));
        }

        if (context.CenterDockId != 0)
        {
            ImGui.SetNextWindowDockID(context.CenterDockId, ImGuiCond.FirstUseEver);
        }

        ImGui.SetNextWindowSize(new SysVector2(1100f, 600f), ImGuiCond.FirstUseEver);
        var visible = ImGui.Begin(EditorWindows.Title("Animator"));
        IsFocused = visible && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (!visible)
        {
            ImGui.End();
            return;
        }

        if (_error is not null)
        {
            Gui.TextColored(ErrorColor, _error);
        }

        if (_document is not { } document)
        {
            Gui.Muted("Double-click a .mnvtanimator controller in the Project panel, or right-click a folder there: Create > Animator Controller.");
            ImGui.End();
            return;
        }

        DrawToolbar(document);
        var avail = ImGui.GetContentRegionAvail();
        const float parametersWidth = 230f, inspectorWidth = 320f;
        ImGui.BeginChild("##parameters", new SysVector2(parametersWidth, 0), ImGuiChildFlags.Borders);
        DrawParameters(document);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##graph", new SysVector2(Math.Max(120f, avail.X - parametersWidth - inspectorWidth - 16f), 0), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        DrawGraph(document);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##inspector", SysVector2.Zero, ImGuiChildFlags.Borders);
        DrawInspector(document);
        ImGui.EndChild();
        ImGui.End();

        // A drag or a typed value becomes one undo step once the interaction is over.
        if (!ImGui.IsAnyItemActive() && !ImGui.IsAnyMouseDown())
        {
            document.Commit();
        }
    }

    private void DrawToolbar(AnimatorControllerDocument document)
    {
        ImGui.TextUnformatted(Path.GetFileName(document.FullPath) + (document.IsDirty ? " *" : ""));
        ImGui.SameLine();
        if (Gui.IconButton(Icons.Save, "Save (Ctrl+S)"))
        {
            Save();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!document.CanUndo);
        if (Gui.IconButton(Icons.Undo2, "Undo (Ctrl+Z)"))
        {
            Undo();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!document.CanRedo);
        if (Gui.IconButton(Icons.Redo2, "Redo (Ctrl+Y)"))
        {
            Redo();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (Gui.IconButton(Icons.Scan, "Frame all states"))
        {
            Frame(document);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f);
        var assets = context.Session.Assets;
        var modelLabel = _clipModel is { } m && assets.TryGetPath(m, out var path) ? Path.GetFileName(path) : "Model for clip names...";
        if (ImGui.BeginCombo("##model", modelLabel))
        {
            foreach (var model in assets.Assets.Where(a => a.Type == AssetTypes.Model).OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (ImGui.Selectable(model.Path, model.Guid == _clipModel))
                {
                    _clipModel = model.Guid;
                    _clips = LoadClips(model.Guid);
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        var problems = document.Problems;
        if (problems.Count == 0)
        {
            Gui.Muted("Valid");
        }
        else
        {
            Gui.TextColored(ErrorColor, $"{problems.Count} problem(s)");
            if (ImGui.IsItemHovered())
            {
                Gui.Tooltip(string.Join("\n", problems));
            }
        }
    }

    private IReadOnlyList<string> LoadClips(Guid model)
    {
        try
        {
            return [.. new ModelWorkflow(context.Session.Assets, model).Load()?.Animations.Select(a => a.Name) ?? []];
        }
        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or ArgumentException)
        {
            _error = ex.Message;
            return [];
        }
    }

    private static void DrawParameters(AnimatorControllerDocument document)
    {
        ImGui.TextUnformatted("Parameters");
        foreach (var type in Enum.GetValues<AnimatorParameterType>())
        {
            if (ImGui.SmallButton($"+ {type}"))
            {
                document.AddParameter(type);
            }

            if (type != AnimatorParameterType.Bool)
            {
                ImGui.SameLine();
            }
        }

        ImGui.Separator();
        foreach (var parameter in document.Parameters.ToList())
        {
            ImGui.PushID(parameter.GetHashCode());
            var name = parameter.Name;
            ImGui.SetNextItemWidth(110f);
            if (ImGui.InputText("##name", ref name, 64, ImGuiInputTextFlags.EnterReturnsTrue) || (ImGui.IsItemDeactivatedAfterEdit() && name != parameter.Name))
            {
                document.RenameParameter(parameter, name);
            }

            ImGui.SameLine();
            Gui.Muted(parameter.Type.ToString().ToLowerInvariant());
            ImGui.SameLine();
            switch (parameter.Type)
            {
                case AnimatorParameterType.Bool:
                    var on = parameter.Default != 0f;
                    if (ImGui.Checkbox("##default", ref on))
                    {
                        parameter.Default = on ? 1f : 0f;
                    }

                    break;
                case AnimatorParameterType.Float or AnimatorParameterType.Int:
                    var value = parameter.Default;
                    ImGui.SetNextItemWidth(48f);
                    if (ImGui.DragFloat("##default", ref value, parameter.Type == AnimatorParameterType.Int ? 1f : 0.05f, 0f, 0f, parameter.Type == AnimatorParameterType.Int ? "%.0f" : "%.2f"))
                    {
                        parameter.Default = parameter.Type == AnimatorParameterType.Int ? MathF.Round(value) : value;
                    }

                    break;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(Icons.X))
            {
                document.RemoveParameter(parameter);
            }

            ImGui.PopID();
        }
    }

    private void DrawGraph(AnimatorControllerDocument document)
    {
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        ImGui.InvisibleButton("##canvas", SysVector2.Max(size, new SysVector2(1f, 1f)), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        SysVector2 Screen(EngineVector2 p) => origin + _pan + new SysVector2(p.X, p.Y);

        // Background grid.
        draw.AddRectFilled(origin, origin + size, Color(0.13f, 0.14f, 0.16f));
        for (var x = _pan.X % 32f; x < size.X; x += 32f)
        {
            draw.AddLine(origin + new SysVector2(x, 0f), origin + new SysVector2(x, size.Y), Color(0.17f, 0.18f, 0.2f));
        }

        for (var y = _pan.Y % 32f; y < size.Y; y += 32f)
        {
            draw.AddLine(origin + new SysVector2(0f, y), origin + new SysVector2(size.X, y), Color(0.17f, 0.18f, 0.2f));
        }

        draw.PushClipRect(origin, origin + size, true);
        var playing = PlayingStates();

        // Transitions under the nodes; a pair of opposite transitions is drawn side by side.
        AnimatorGraphTransition? hitTransition = null;
        foreach (var t in document.Transitions)
        {
            var from = t.From is null ? document.AnyStatePosition : document.FindState(t.From)?.Position;
            if (from is not { } a || document.FindState(t.To)?.Position is not { } b)
            {
                continue;
            }

            var start = Screen(a) + new SysVector2(NodeWidth / 2f, NodeHeight / 2f);
            var end = Screen(b) + new SysVector2(NodeWidth / 2f, NodeHeight / 2f);
            var color = ReferenceEquals(t, _selectedTransition) ? SelectedColor : ArrowColor;
            if (t.From == t.To)
            {
                var top = Screen(a) + new SysVector2(NodeWidth / 2f, -10f);
                draw.AddCircle(top, 12f, color, 16, 2f);
                if (hovered && SysVector2.Distance(mouse, top) is > 8f and < 16f)
                {
                    hitTransition = t;
                }

                continue;
            }

            var direction = SysVector2.Normalize(end - start);
            var side = new SysVector2(-direction.Y, direction.X) * (document.Transitions.Any(o => o.From == t.To && o.To == t.From) ? 6f : 0f);
            (start, end) = (start + side, end + side);
            draw.AddLine(start, end, color, 2f);
            var middle = (start + end) * 0.5f;
            var normal = new SysVector2(-direction.Y, direction.X);
            draw.AddTriangleFilled(middle + (direction * 8f), middle - (direction * 6f) + (normal * 6f), middle - (direction * 6f) - (normal * 6f), color);
            if (hovered && DistanceToSegment(mouse, start, end) < 6f)
            {
                hitTransition = t;
            }
        }

        // Nodes: "Any State", then the states (later ones on top).
        bool Hit(EngineVector2 position) => hovered && mouse.X >= Screen(position).X && mouse.Y >= Screen(position).Y && mouse.X <= Screen(position).X + NodeWidth && mouse.Y <= Screen(position).Y + NodeHeight;
        DrawNode(draw, Screen(document.AnyStatePosition), "Any State", "", AnyColor, _anySelected, null);
        var hitState = document.States.LastOrDefault(s => Hit(s.Position));
        var hitAny = hitState is null && Hit(document.AnyStatePosition);
        foreach (var state in document.States)
        {
            var border = playing.TryGetValue(state.Name, out var progress) ? PlayingColor : state.Name == document.DefaultState ? DefaultColor : NodeColor;
            DrawNode(draw, Screen(state.Position), state.Name, state.Clip.Length == 0 ? "(no clip)" : state.Clip, border, ReferenceEquals(state, _selectedState), playing.ContainsKey(state.Name) ? progress : null);
        }

        if (_connecting.Active)
        {
            var from = _connecting.From is null ? document.AnyStatePosition : document.FindState(_connecting.From)?.Position ?? document.AnyStatePosition;
            draw.AddLine(Screen(from) + new SysVector2(NodeWidth / 2f, NodeHeight / 2f), mouse, SelectedColor, 2f);
        }

        draw.PopClipRect();

        // Input: select, connect, drag, pan, context menus.
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyAlt)
        {
            if (_connecting.Active)
            {
                if (hitState is not null)
                {
                    _selectedTransition = document.AddTransition(_connecting.From, hitState.Name);
                    (_selectedState, _anySelected) = (null, false);
                }

                _connecting = default;
            }
            else
            {
                (_selectedState, _anySelected, _selectedTransition) = (hitState, hitAny, hitState is null && !hitAny ? hitTransition : null);
                (_dragging, _draggingAny) = (hitState, hitAny);
            }
        }

        if (ImGui.IsMouseDragging(ImGuiMouseButton.Left) && (_dragging is not null || _draggingAny) && !io.KeyAlt)
        {
            var delta = new EngineVector2(io.MouseDelta.X, io.MouseDelta.Y);
            if (_dragging is not null)
            {
                _dragging.Position += delta;
            }
            else
            {
                document.AnyStatePosition += delta;
            }
        }

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            (_dragging, _draggingAny) = (null, false);
        }

        if (hovered && (ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || (io.KeyAlt && ImGui.IsMouseDragging(ImGuiMouseButton.Left))))
        {
            _pan += io.MouseDelta;
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _connecting = default;
            _contextAt = mouse - origin - _pan;
            (_selectedState, _anySelected) = (hitState, hitAny);
            ImGui.OpenPopup(hitState is not null || hitAny ? "node" : "canvas");
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _connecting = default;
        }

        if (ImGui.BeginPopup("canvas"))
        {
            if (ImGui.MenuItem("Add State"))
            {
                _selectedState = document.AddState(new EngineVector2(_contextAt.X - (NodeWidth / 2f), _contextAt.Y - (NodeHeight / 2f)));
                _selectedState.Clip = (_clips.Count > 0 ? _clips[0] : "");
            }

            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("node"))
        {
            if (ImGui.MenuItem("Make Transition"))
            {
                _connecting = (true, _anySelected ? null : _selectedState?.Name);
            }

            if (_selectedState is { } state)
            {
                if (ImGui.MenuItem("Set as Default State", null, state.Name == document.DefaultState))
                {
                    document.DefaultState = state.Name;
                }

                if (ImGui.MenuItem("Delete"))
                {
                    document.RemoveState(state);
                    _selectedState = null;
                }
            }

            ImGui.EndPopup();
        }

        if (_selectedTransition is { } selected && IsFocused && ImGui.IsKeyPressed(ImGuiKey.Delete) && !io.WantTextInput)
        {
            document.Transitions.Remove(selected);
            _selectedTransition = null;
        }
    }

    private static void DrawNode(ImDrawListPtr draw, SysVector2 at, string title, string subtitle, uint border, bool selected, float? progress)
    {
        var end = at + new SysVector2(NodeWidth, NodeHeight);
        draw.AddRectFilled(at, end, Color(0.2f, 0.21f, 0.24f), 6f);
        draw.AddRect(at, end, selected ? SelectedColor : border, 6f, ImDrawFlags.None, selected ? 3f : 2f);
        draw.AddText(at + new SysVector2(10f, 6f), Color(0.95f, 0.95f, 0.95f), title);
        if (subtitle.Length > 0)
        {
            draw.AddText(at + new SysVector2(10f, 24f), Color(0.62f, 0.64f, 0.68f), subtitle);
        }

        if (progress is { } p)
        {
            draw.AddRectFilled(at + new SysVector2(6f, NodeHeight - 6f), at + new SysVector2(6f + ((NodeWidth - 12f) * (p % 1f)), NodeHeight - 3f), PlayingColor, 2f);
        }
    }

    private void DrawInspector(AnimatorControllerDocument document)
    {
        if (_selectedState is { } state && document.States.Contains(state))
        {
            ImGui.TextUnformatted("State");
            var name = state.Name;
            if (ImGui.InputText("Name", ref name, 64, ImGuiInputTextFlags.EnterReturnsTrue) || (ImGui.IsItemDeactivatedAfterEdit() && name != state.Name))
            {
                document.RenameState(state, name);
            }

            var clip = state.Clip;
            if (_clips.Count > 0)
            {
                if (ImGui.BeginCombo("Clip", clip.Length == 0 ? "(none)" : clip))
                {
                    foreach (var option in _clips)
                    {
                        if (ImGui.Selectable(option, option == clip))
                        {
                            state.Clip = option;
                        }
                    }

                    ImGui.EndCombo();
                }
            }
            else if (ImGui.InputText("Clip", ref clip, 128))
            {
                state.Clip = clip;
            }

            if (_clips.Count == 0 && ImGui.IsItemHovered())
            {
                Gui.Tooltip("Pick a model in the toolbar to choose from its clips.");
            }

            var speed = state.Speed;
            if (ImGui.DragFloat("Speed", ref speed, 0.05f, -10f, 10f, "%.2f"))
            {
                state.Speed = speed;
            }

            var loop = state.Loop;
            if (ImGui.Checkbox("Loop", ref loop))
            {
                state.Loop = loop;
            }

            ImGui.BeginDisabled(state.Name == document.DefaultState);
            if (ImGui.Button(state.Name == document.DefaultState ? "Default state" : "Set as default"))
            {
                document.DefaultState = state.Name;
            }

            ImGui.EndDisabled();
            ImGui.Separator();
            Gui.Muted("Transitions from this state:");
            foreach (var t in document.Transitions.Where(t => t.From == state.Name))
            {
                if (ImGui.Selectable($"-> {t.To}##{t.GetHashCode()}"))
                {
                    (_selectedTransition, _selectedState) = (t, null);
                }
            }

            return;
        }

        if (_selectedTransition is { } transition && document.Transitions.Contains(transition))
        {
            DrawTransition(document, transition);
            return;
        }

        Gui.TextWrapped(_anySelected
            ? "Any State: its transitions fire from every state (they need a condition). Right-click it: Make Transition."
            : "Select a state or a transition. Right-click the graph to add a state; right-click a state to make a transition.");
    }

    private void DrawTransition(AnimatorControllerDocument document, AnimatorGraphTransition transition)
    {
        ImGui.TextUnformatted($"{transition.From ?? "Any State"} -> {transition.To}");
        var duration = transition.Duration;
        if (ImGui.DragFloat("Fade (s)", ref duration, 0.01f, 0f, 10f, "%.2f"))
        {
            transition.Duration = Math.Max(0f, duration);
        }

        var hasExit = transition.ExitTime is not null;
        if (ImGui.Checkbox("Has exit time", ref hasExit))
        {
            transition.ExitTime = hasExit ? 1f : null;
        }

        if (transition.ExitTime is { } exit)
        {
            if (ImGui.DragFloat("Exit time", ref exit, 0.01f, 0f, 100f, "%.2f"))
            {
                transition.ExitTime = Math.Max(0f, exit);
            }

            if (ImGui.IsItemHovered())
            {
                Gui.Tooltip("In clip lengths: 1 is the end of the state's first cycle.");
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Conditions (all must hold)");
        foreach (var condition in transition.Conditions.ToList())
        {
            ImGui.PushID(condition.GetHashCode());
            ImGui.SetNextItemWidth(100f);
            if (ImGui.BeginCombo("##parameter", condition.Parameter.Length == 0 ? "(none)" : condition.Parameter))
            {
                foreach (var parameter in document.Parameters)
                {
                    if (ImGui.Selectable(parameter.Name, parameter.Name == condition.Parameter))
                    {
                        condition.Parameter = parameter.Name;
                        if (!AnimatorControllerDocument.ModesFor(parameter.Type).Contains(condition.Mode))
                        {
                            condition.Mode = AnimatorControllerDocument.DefaultMode(parameter.Type);
                        }
                    }
                }

                ImGui.EndCombo();
            }

            var type = document.Parameters.Find(p => p.Name == condition.Parameter)?.Type ?? AnimatorParameterType.Float;
            var modes = AnimatorControllerDocument.ModesFor(type);
            if (modes.Count > 1)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80f);
                if (ImGui.BeginCombo("##mode", ModeLabel(condition.Mode)))
                {
                    foreach (var mode in modes)
                    {
                        if (ImGui.Selectable(ModeLabel(mode), mode == condition.Mode))
                        {
                            condition.Mode = mode;
                        }
                    }

                    ImGui.EndCombo();
                }
            }

            if (type is AnimatorParameterType.Float or AnimatorParameterType.Int)
            {
                ImGui.SameLine();
                var threshold = condition.Threshold;
                ImGui.SetNextItemWidth(60f);
                if (ImGui.DragFloat("##threshold", ref threshold, 0.05f, 0f, 0f, "%.2f"))
                {
                    condition.Threshold = threshold;
                }
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(Icons.X))
            {
                transition.Conditions.Remove(condition);
            }

            ImGui.PopID();
        }

        if (ImGui.Button($"{Icons.Plus}  Condition"))
        {
            document.AddCondition(transition);
        }

        ImGui.Separator();
        if (ImGui.Button($"{Icons.Trash2}  Delete transition"))
        {
            document.Transitions.Remove(transition);
            _selectedTransition = null;
        }
    }

    private static string ModeLabel(AnimatorConditionMode mode) => mode switch
    {
        AnimatorConditionMode.Greater => ">",
        AnimatorConditionMode.Less => "<",
        AnimatorConditionMode.Equals => "==",
        AnimatorConditionMode.NotEquals => "!=",
        AnimatorConditionMode.IsTrue => "true",
        _ => "false",
    };

    /// <summary>During Play Mode: the states controllers of this asset are in, with how far they played (clip lengths).</summary>
    private Dictionary<string, float> PlayingStates()
    {
        var states = new Dictionary<string, float>(StringComparer.Ordinal);
        if (_asset is { } asset && context.PlayMode.PlayScene is { } scene)
        {
            foreach (var controller in scene.FindObjectsOfType<ModelAnimatorController>().Where(c => c.Controller.Guid == asset && c.CurrentState is not null))
            {
                states[controller.CurrentState!] = controller.NormalizedTime;
            }
        }

        return states;
    }

    private static float DistanceToSegment(SysVector2 p, SysVector2 a, SysVector2 b)
    {
        var ab = b - a;
        var t = Math.Clamp(SysVector2.Dot(p - a, ab) / Math.Max(1e-4f, ab.LengthSquared()), 0f, 1f);
        return SysVector2.Distance(p, a + (ab * t));
    }

    /// <summary>An opaque color packed for draw lists (ABGR), without an ImGui context (the colors are static).</summary>
    private static uint Color(float r, float g, float b) => 0xFF000000u | ((uint)(b * 255f) << 16) | ((uint)(g * 255f) << 8) | (uint)(r * 255f);
}
