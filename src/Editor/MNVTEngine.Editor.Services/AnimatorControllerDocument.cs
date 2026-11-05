using System.Globalization;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Editor.Services;

public sealed class AnimatorGraphParameter
{
    public string Name { get; set; } = "";

    public AnimatorParameterType Type { get; set; }

    /// <summary>The default value: a number, or 0/1 for bools.</summary>
    public float Default { get; set; }
}

public sealed class AnimatorGraphState
{
    public string Name { get; set; } = "";

    public string Clip { get; set; } = "";

    public float Speed { get; set; } = 1f;

    public bool Loop { get; set; } = true;

    /// <summary>Where the node is in the graph editor (not used at runtime).</summary>
    public Vector2 Position { get; set; }
}

public sealed class AnimatorGraphCondition
{
    public string Parameter { get; set; } = "";

    public AnimatorConditionMode Mode { get; set; }

    public float Threshold { get; set; }
}

public sealed class AnimatorGraphTransition
{
    /// <summary>The state it leaves; null for "any state".</summary>
    public string? From { get; set; }

    public string To { get; set; } = "";

    public float Duration { get; set; } = 0.25f;

    public float? ExitTime { get; set; }

    public List<AnimatorGraphCondition> Conditions { get; } = [];
}

/// <summary>
/// An animator controller (<c>.mnvtanimator</c>) open in the Animator window: parameters, states with graph positions,
/// transitions with conditions, structural edits that keep names consistent, undo/redo by snapshots, validation with
/// the runtime's parser, and saving. UI-free and testable; the editor panel draws it.
/// <para>
/// Loading is tolerant (a file with mistakes opens, and <see cref="Problems"/> lists them). Node positions are saved
/// under <c>"editor"</c>, which the runtime ignores.
/// </para>
/// </summary>
public sealed class AnimatorControllerDocument
{
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private string _committed;
    private string _saved;

    /// <exception cref="SerializationException">The file is not JSON.</exception>
    public AnimatorControllerDocument(string fullPath)
    {
        FullPath = fullPath;
        Load(File.ReadAllText(fullPath));
        _committed = _saved = Serialize();
    }

    public string FullPath { get; }

    public List<AnimatorGraphParameter> Parameters { get; } = [];

    public List<AnimatorGraphState> States { get; } = [];

    public List<AnimatorGraphTransition> Transitions { get; } = [];

    public string DefaultState { get; set; } = "";

    /// <summary>Where the "any state" node is drawn.</summary>
    public Vector2 AnyStatePosition { get; set; } = new(40f, 40f);

    public bool IsDirty => _committed != _saved;

    public bool CanUndo => _undo.Count > 0 || Serialize() != _committed;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Writes a new controller with one state.</summary>
    public static void CreateNew(string fullPath) => File.WriteAllText(fullPath, """
        {
          "schemaVersion": 1,
          "parameters": {},
          "defaultState": "Idle",
          "states": [ { "name": "Idle", "clip": "" } ],
          "transitions": [],
          "editor": { "positions": { "Idle": [ 260, 60 ], "*": [ 40, 60 ] } }
        }
        """);

    /// <summary>The mistakes the runtime would report (empty: the controller is valid).</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            try
            {
                AnimatorControllerDefinition.Parse(Serialize(), Path.GetFileName(FullPath));
                return [];
            }
            catch (SerializationException ex)
            {
                return [.. ex.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Path}: {d.Message}").DefaultIfEmpty(ex.Message)];
            }
        }
    }

    public AnimatorGraphState? FindState(string name) => States.Find(s => s.Name == name);

    /// <summary>Adds a state at <paramref name="position"/>; the first state becomes the default.</summary>
    public AnimatorGraphState AddState(Vector2 position, string name = "State")
    {
        var state = new AnimatorGraphState { Name = Unique(name, States.Select(s => s.Name)), Position = position };
        States.Add(state);
        if (States.Count == 1 || FindState(DefaultState) is null)
        {
            DefaultState = state.Name;
        }

        return state;
    }

    /// <summary>Removes a state and every transition to or from it; another state becomes the default if needed.</summary>
    public void RemoveState(AnimatorGraphState state)
    {
        States.Remove(state);
        Transitions.RemoveAll(t => t.From == state.Name || t.To == state.Name);
        if (DefaultState == state.Name)
        {
            DefaultState = States.Count > 0 ? States[0].Name : "";
        }
    }

    /// <summary>Renames a state and the transitions and default that use its name; returns the name it got (made unique).</summary>
    public string RenameState(AnimatorGraphState state, string name)
    {
        var old = state.Name;
        name = name.Trim();
        if (name.Length == 0 || name == AnimatorControllerDefinition.AnyState || name == old)
        {
            return old;
        }

        name = Unique(name, States.Where(s => s != state).Select(s => s.Name));
        state.Name = name;
        foreach (var t in Transitions)
        {
            t.From = t.From == old ? name : t.From;
            t.To = t.To == old ? name : t.To;
        }

        DefaultState = DefaultState == old ? name : DefaultState;
        return name;
    }

    /// <summary>A transition from a state (or any state, <paramref name="from"/> null) that fires at the end of the clip until conditions are added.</summary>
    public AnimatorGraphTransition AddTransition(string? from, string to)
    {
        var transition = new AnimatorGraphTransition { From = from, To = to, ExitTime = from is null ? null : 1f };
        Transitions.Add(transition);
        return transition;
    }

    public AnimatorGraphParameter AddParameter(AnimatorParameterType type)
    {
        var parameter = new AnimatorGraphParameter { Name = Unique(type.ToString(), Parameters.Select(p => p.Name)), Type = type };
        Parameters.Add(parameter);
        return parameter;
    }

    /// <summary>Removes a parameter and the conditions that use it.</summary>
    public void RemoveParameter(AnimatorGraphParameter parameter)
    {
        Parameters.Remove(parameter);
        foreach (var t in Transitions)
        {
            t.Conditions.RemoveAll(c => c.Parameter == parameter.Name);
        }
    }

    public string RenameParameter(AnimatorGraphParameter parameter, string name)
    {
        var old = parameter.Name;
        name = name.Trim();
        if (name.Length == 0 || name == old)
        {
            return old;
        }

        name = Unique(name, Parameters.Where(p => p != parameter).Select(p => p.Name));
        parameter.Name = name;
        foreach (var c in Transitions.SelectMany(t => t.Conditions).Where(c => c.Parameter == old))
        {
            c.Parameter = name;
        }

        return name;
    }

    /// <summary>A condition on the first parameter, with the comparison its type needs.</summary>
    public AnimatorGraphCondition AddCondition(AnimatorGraphTransition transition)
    {
        var parameter = Parameters.FirstOrDefault();
        var condition = new AnimatorGraphCondition { Parameter = parameter?.Name ?? "", Mode = DefaultMode(parameter?.Type ?? AnimatorParameterType.Float) };
        transition.Conditions.Add(condition);
        return condition;
    }

    /// <summary>The comparisons a parameter type allows (a trigger only "is set").</summary>
    public static IReadOnlyList<AnimatorConditionMode> ModesFor(AnimatorParameterType type) => type switch
    {
        AnimatorParameterType.Bool => [AnimatorConditionMode.IsTrue, AnimatorConditionMode.IsFalse],
        AnimatorParameterType.Trigger => [AnimatorConditionMode.IsTrue],
        _ => [AnimatorConditionMode.Greater, AnimatorConditionMode.Less, AnimatorConditionMode.Equals, AnimatorConditionMode.NotEquals],
    };

    public static AnimatorConditionMode DefaultMode(AnimatorParameterType type) => ModesFor(type)[0];

    /// <summary>Records one undo step if the controller changed since the last commit. Call when no edit is in progress.</summary>
    public void Commit()
    {
        var json = Serialize();
        if (json == _committed)
        {
            return;
        }

        _undo.Add(_committed);
        _redo.Clear();
        _committed = json;
    }

    public void Undo()
    {
        Commit();
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Add(_committed);
        _committed = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Load(_committed);
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Add(_committed);
        _committed = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Load(_committed);
    }

    public void Save()
    {
        Commit();
        File.WriteAllText(FullPath, _committed);
        _saved = _committed;
    }

    /// <summary>The controller in its file format (the runtime's schema plus the editor's node positions).</summary>
    public string Serialize()
    {
        static JToken Number(float value) => value == MathF.Floor(value) && MathF.Abs(value) < 1e7f ? new JValue((long)value) : new JValue(Math.Round(value, 4));
        var parameters = new JObject();
        foreach (var p in Parameters)
        {
            var entry = new JObject { ["type"] = p.Type.ToString().ToLowerInvariant() };
            if (p.Type == AnimatorParameterType.Bool && p.Default != 0f)
            {
                entry["default"] = true;
            }
            else if (p.Type is AnimatorParameterType.Float or AnimatorParameterType.Int && p.Default != 0f)
            {
                entry["default"] = Number(p.Type == AnimatorParameterType.Int ? MathF.Round(p.Default) : p.Default);
            }

            parameters[p.Name] = entry;
        }

        var states = new JArray();
        var positions = new JObject { [AnimatorControllerDefinition.AnyState] = new JArray(Number(AnyStatePosition.X), Number(AnyStatePosition.Y)) };
        foreach (var s in States)
        {
            var entry = new JObject { ["name"] = s.Name, ["clip"] = s.Clip };
            if (s.Speed != 1f)
            {
                entry["speed"] = Number(s.Speed);
            }

            if (!s.Loop)
            {
                entry["loop"] = false;
            }

            states.Add(entry);
            positions[s.Name] = new JArray(Number(MathF.Round(s.Position.X)), Number(MathF.Round(s.Position.Y)));
        }

        var transitions = new JArray();
        foreach (var t in Transitions)
        {
            var entry = new JObject { ["from"] = t.From ?? AnimatorControllerDefinition.AnyState, ["to"] = t.To, ["duration"] = Number(t.Duration) };
            if (t.ExitTime is { } exit)
            {
                entry["exitTime"] = Number(exit);
            }

            if (t.Conditions.Count > 0)
            {
                entry["conditions"] = new JArray(t.Conditions.Select(c => ConditionJson(c, Parameters.Find(p => p.Name == c.Parameter)?.Type)));
            }

            transitions.Add(entry);
        }

        var root = new JObject
        {
            ["schemaVersion"] = AnimatorControllerDefinition.CurrentSchemaVersion,
            ["parameters"] = parameters,
            ["defaultState"] = DefaultState,
            ["states"] = states,
            ["transitions"] = transitions,
            ["editor"] = new JObject { ["positions"] = positions },
        };
        return root.ToString(Formatting.Indented) + Environment.NewLine;

        static JObject ConditionJson(AnimatorGraphCondition c, AnimatorParameterType? type)
        {
            var json = new JObject { ["parameter"] = c.Parameter };
            switch (c.Mode)
            {
                case AnimatorConditionMode.IsTrue when type == AnimatorParameterType.Trigger:
                    break; // a trigger is just the parameter
                case AnimatorConditionMode.IsTrue or AnimatorConditionMode.IsFalse:
                    json["is"] = c.Mode == AnimatorConditionMode.IsTrue;
                    break;
                default:
                    json[char.ToLowerInvariant(c.Mode.ToString()[0]) + c.Mode.ToString()[1..]] = Number(c.Threshold);
                    break;
            }

            return json;
        }
    }

    private void Load(string json)
    {
        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SerializationException(Path.GetFileName(FullPath), ex.Message, [], ex);
        }

        Parameters.Clear();
        States.Clear();
        Transitions.Clear();
        if (root["parameters"] is JObject parameters)
        {
            foreach (var property in parameters.Properties())
            {
                var type = Enum.TryParse<AnimatorParameterType>(property.Value["type"]?.ToString(), ignoreCase: true, out var t) ? t : AnimatorParameterType.Float;
                var value = property.Value["default"];
                var fallback = value?.Type switch
                {
                    JTokenType.Boolean => value.Value<bool>() ? 1f : 0f,
                    JTokenType.Integer or JTokenType.Float => value.Value<float>(),
                    _ => 0f,
                };
                Parameters.Add(new AnimatorGraphParameter { Name = property.Name, Type = type, Default = fallback });
            }
        }

        var positions = root["editor"]?["positions"] as JObject;
        Vector2? Position(string name) => positions?[name] is JArray { Count: 2 } p && p[0].Type is JTokenType.Integer or JTokenType.Float && p[1].Type is JTokenType.Integer or JTokenType.Float
            ? new Vector2(p[0].Value<float>(), p[1].Value<float>())
            : null;
        AnyStatePosition = Position(AnimatorControllerDefinition.AnyState) ?? new Vector2(40f, 40f);
        foreach (var (state, index) in (root["states"] as JArray ?? []).OfType<JObject>().Select((s, i) => (s, i)))
        {
            var name = state.Value<string>("name") ?? $"State {index}";
            States.Add(new AnimatorGraphState
            {
                Name = name,
                Clip = state.Value<string>("clip") ?? "",
                Speed = state["speed"]?.Type is JTokenType.Integer or JTokenType.Float ? state.Value<float>("speed") : 1f,
                Loop = state.Value<bool?>("loop") ?? true,
                Position = Position(name) ?? new Vector2(260f + (index % 3 * 200f), 60f + (index / 3 * 110f)), // laid out in a grid when none is saved
            });
        }

        foreach (var transition in (root["transitions"] as JArray ?? []).OfType<JObject>())
        {
            var from = transition.Value<string>("from");
            var t = new AnimatorGraphTransition
            {
                From = from is null or AnimatorControllerDefinition.AnyState ? null : from,
                To = transition.Value<string>("to") ?? "",
                Duration = transition["duration"]?.Type is JTokenType.Integer or JTokenType.Float ? transition.Value<float>("duration") : 0.25f,
                ExitTime = transition["exitTime"]?.Type is JTokenType.Integer or JTokenType.Float ? transition.Value<float>("exitTime") : null,
            };
            foreach (var c in (transition["conditions"] as JArray ?? []).OfType<JObject>())
            {
                var parameter = c.Value<string>("parameter") ?? "";
                var condition = new AnimatorGraphCondition { Parameter = parameter, Mode = AnimatorConditionMode.IsTrue };
                foreach (var mode in new[] { AnimatorConditionMode.Greater, AnimatorConditionMode.Less, AnimatorConditionMode.Equals, AnimatorConditionMode.NotEquals })
                {
                    var key = char.ToLowerInvariant(mode.ToString()[0]) + mode.ToString()[1..];
                    if (c[key]?.Type is JTokenType.Integer or JTokenType.Float)
                    {
                        (condition.Mode, condition.Threshold) = (mode, c.Value<float>(key));
                    }
                }

                if (c["is"]?.Type == JTokenType.Boolean)
                {
                    condition.Mode = c.Value<bool>("is") ? AnimatorConditionMode.IsTrue : AnimatorConditionMode.IsFalse;
                }

                t.Conditions.Add(condition);
            }

            Transitions.Add(t);
        }

        DefaultState = root.Value<string>("defaultState") ?? (States.Count > 0 ? States[0].Name : "");
    }

    private static string Unique(string name, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(name))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{name} {i}");
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
