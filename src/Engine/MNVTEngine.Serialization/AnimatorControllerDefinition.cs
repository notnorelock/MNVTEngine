using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Serialization;

public enum AnimatorParameterType
{
    Float,
    Int,
    Bool,

    /// <summary>A bool that a transition using it resets when it fires (jump, attack).</summary>
    Trigger,
}

public enum AnimatorConditionMode
{
    Greater,
    Less,
    Equals,
    NotEquals,

    /// <summary>A bool or trigger is set.</summary>
    IsTrue,

    /// <summary>A bool is not set.</summary>
    IsFalse,
}

public sealed record AnimatorParameter(string Name, AnimatorParameterType Type, float Default);

/// <summary>A state plays one clip of the model.</summary>
/// <param name="Speed">Playback speed (negative plays backwards).</param>
public sealed record AnimatorState(string Name, string Clip, float Speed, bool Loop);

public sealed record AnimatorCondition(string Parameter, AnimatorConditionMode Mode, float Threshold);

/// <param name="From">The state it leaves; null for "any state" (<c>"*"</c>).</param>
/// <param name="Duration">Crossfade length in seconds (0 switches at once).</param>
/// <param name="ExitTime">Fires only once the state has played this far (in clip lengths: 1 is the end of the first cycle); null for any time.</param>
public sealed record AnimatorTransition(string? From, string To, IReadOnlyList<AnimatorCondition> Conditions, float Duration, float? ExitTime);

/// <summary>
/// An animator controller (<c>.mnvtanimator</c>): a state machine of model clips driven by parameters, like Unity's
/// Animator Controller. Shared by the asset importer (validation) and <c>ModelAnimatorController</c> (playback).
/// <code>
/// { "schemaVersion": 1,
///   "parameters": { "speed": { "type": "float" }, "grounded": { "type": "bool", "default": true }, "jump": { "type": "trigger" } },
///   "defaultState": "Idle",
///   "states": [ { "name": "Idle", "clip": "Idle" }, { "name": "Run", "clip": "Run", "speed": 1.2 }, { "name": "Jump", "clip": "Jump", "loop": false } ],
///   "transitions": [
///     { "from": "Idle", "to": "Run", "duration": 0.2, "conditions": [ { "parameter": "speed", "greater": 0.1 } ] },
///     { "from": "Run", "to": "Idle", "duration": 0.2, "conditions": [ { "parameter": "speed", "less": 0.1 } ] },
///     { "from": "*", "to": "Jump", "duration": 0.1, "conditions": [ { "parameter": "jump" } ] },
///     { "from": "Jump", "to": "Idle", "duration": 0.25, "exitTime": 1 } ] }
/// </code>
/// Conditions: <c>greater</c>, <c>less</c>, <c>equals</c>, <c>notEquals</c> (numbers) or <c>is</c> (bools); a trigger
/// takes none. A transition needs a condition or an exit time.
/// </summary>
public sealed class AnimatorControllerDefinition
{
    public const string Extension = ".mnvtanimator";
    public const string AnyState = "*";
    public const int CurrentSchemaVersion = 1;

    private AnimatorControllerDefinition(IReadOnlyList<AnimatorParameter> parameters, IReadOnlyList<AnimatorState> states, IReadOnlyList<AnimatorTransition> transitions, string defaultState) =>
        (Parameters, States, Transitions, DefaultState) = (parameters, states, transitions, defaultState);

    /// <summary>A controller built in code; <paramref name="defaultState"/> defaults to the first state.</summary>
    /// <exception cref="ArgumentException">The same problems <see cref="Parse"/> reports.</exception>
    public static AnimatorControllerDefinition Create(IReadOnlyList<AnimatorParameter> parameters, IReadOnlyList<AnimatorState> states, IReadOnlyList<AnimatorTransition> transitions, string? defaultState = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(transitions);
        var definition = new AnimatorControllerDefinition(parameters, states, transitions, defaultState ?? (states.Count > 0 ? states[0].Name : throw new ArgumentException("A controller needs a state.", nameof(states))));
        var problems = definition.Validate().ToList();
        return problems.Count == 0 ? definition : throw new ArgumentException(string.Join(" ", problems.Select(p => $"{p.Path}: {p.Message}")), nameof(transitions));
    }

    public IReadOnlyList<AnimatorParameter> Parameters { get; }

    public IReadOnlyList<AnimatorState> States { get; }

    /// <summary>In priority order: the first transition whose conditions hold fires (the state's own before "any state").</summary>
    public IReadOnlyList<AnimatorTransition> Transitions { get; }

    public string DefaultState { get; }

    public AnimatorState? FindState(string name) => States.FirstOrDefault(s => s.Name == name);

    public AnimatorParameter? FindParameter(string name) => Parameters.FirstOrDefault(p => p.Name == name);

    /// <exception cref="SerializationException">Malformed or invalid controller; <see cref="SerializationException.Diagnostics"/> lists every problem.</exception>
    public static AnimatorControllerDefinition Parse(string json, string sourceName)
    {
        var diagnostics = new List<SerializationDiagnostic>();
        void Error(string path, string message) => diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Error, path, message));

        JObject root;
        try
        {
            root = JObject.Parse(json);
            new JsonMigrationPipeline(CurrentSchemaVersion).Migrate(root, diagnostics);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new SerializationException(sourceName, ex.Message, diagnostics, ex);
        }

        var parameters = new List<AnimatorParameter>();
        if (root["parameters"] is JObject parameterTable)
        {
            foreach (var property in parameterTable.Properties())
            {
                var path = $"parameters.{property.Name}";
                var typeName = property.Value is JObject p ? p.Value<string>("type") : null;
                if (!Enum.TryParse<AnimatorParameterType>(typeName, ignoreCase: true, out var type))
                {
                    Error(path, "A parameter needs \"type\": \"float\", \"int\", \"bool\" or \"trigger\".");
                    continue;
                }

                var value = property.Value["default"];
                var fallback = value switch
                {
                    null => 0f,
                    { Type: JTokenType.Boolean } when type is AnimatorParameterType.Bool => value.Value<bool>() ? 1f : 0f,
                    { Type: JTokenType.Integer } when type is AnimatorParameterType.Int => value.Value<int>(),
                    { Type: JTokenType.Integer or JTokenType.Float } when type is AnimatorParameterType.Float => value.Value<float>(),
                    _ => float.NaN,
                };
                if (float.IsNaN(fallback))
                {
                    Error(path + ".default", $"The default of a {type.ToString().ToLowerInvariant()} parameter must be {(type is AnimatorParameterType.Bool ? "true or false" : "a number")}; triggers have none.");
                    continue;
                }

                parameters.Add(new AnimatorParameter(property.Name, type, fallback));
            }
        }
        else if (root["parameters"] is not null)
        {
            Error("parameters", "'parameters' must be an object of name: { \"type\": ... }.");
        }

        var states = new List<AnimatorState>();
        if (root["states"] is JArray stateList && stateList.Count > 0)
        {
            for (var i = 0; i < stateList.Count; i++)
            {
                var path = $"states[{i}]";
                if (stateList[i] is not JObject state || state.Value<string>("name") is not { Length: > 0 } name || name == AnyState || state.Value<string>("clip") is not { } clip)
                {
                    Error(path, "A state needs a \"name\" (not \"*\") and a \"clip\" (the model clip it plays).");
                    continue;
                }

                var speed = state["speed"] is { Type: JTokenType.Integer or JTokenType.Float } s ? s.Value<float>() : 1f;
                states.Add(new AnimatorState(name, clip, speed, state.Value<bool?>("loop") ?? true));
            }
        }
        else
        {
            Error("states", "'states' must be a non-empty array.");
        }

        var transitions = new List<AnimatorTransition>();
        if (root["transitions"] is JArray transitionList)
        {
            for (var i = 0; i < transitionList.Count; i++)
            {
                var path = $"transitions[{i}]";
                if (transitionList[i] is not JObject t || t.Value<string>("from") is not { } from || t.Value<string>("to") is not { } to)
                {
                    Error(path, "A transition needs \"from\" (a state or \"*\") and \"to\".");
                    continue;
                }

                var conditions = new List<AnimatorCondition>();
                if (t["conditions"] is JArray conditionList)
                {
                    for (var c = 0; c < conditionList.Count; c++)
                    {
                        if (ParseCondition(conditionList[c] as JObject) is { } condition)
                        {
                            conditions.Add(condition);
                        }
                        else
                        {
                            Error($"{path}.conditions[{c}]", "A condition is { \"parameter\": name } plus one of \"greater\", \"less\", \"equals\", \"notEquals\" (a number) or \"is\" (true or false); a trigger needs none.");
                        }
                    }
                }

                var duration = t["duration"] is { Type: JTokenType.Integer or JTokenType.Float } d ? d.Value<float>() : 0.25f;
                float? exitTime = t["exitTime"] is { Type: JTokenType.Integer or JTokenType.Float } e ? e.Value<float>() : null;
                transitions.Add(new AnimatorTransition(from == AnyState ? null : from, to, conditions, duration, exitTime));
            }
        }
        else if (root["transitions"] is not null)
        {
            Error("transitions", "'transitions' must be an array.");
        }

        var defaultState = root.Value<string>("defaultState") ?? (states.Count > 0 ? states[0].Name : "");
        if (!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            var definition = new AnimatorControllerDefinition(parameters, states, transitions, defaultState);
            diagnostics.AddRange(definition.Validate());
            if (diagnostics.All(d => d.Severity != DiagnosticSeverity.Error))
            {
                return definition;
            }
        }

        throw new SerializationException(sourceName, string.Join(" ", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message)), diagnostics);
    }

    private static AnimatorCondition? ParseCondition(JObject? condition)
    {
        if (condition?.Value<string>("parameter") is not { Length: > 0 } parameter)
        {
            return null;
        }

        foreach (var (key, mode) in new[] { ("greater", AnimatorConditionMode.Greater), ("less", AnimatorConditionMode.Less), ("equals", AnimatorConditionMode.Equals), ("notEquals", AnimatorConditionMode.NotEquals) })
        {
            if (condition[key] is { Type: JTokenType.Integer or JTokenType.Float } value)
            {
                return new AnimatorCondition(parameter, mode, value.Value<float>());
            }
        }

        return condition["is"] switch
        {
            { Type: JTokenType.Boolean } flag => new AnimatorCondition(parameter, flag.Value<bool>() ? AnimatorConditionMode.IsTrue : AnimatorConditionMode.IsFalse, 0f),
            null when condition.Count == 1 => new AnimatorCondition(parameter, AnimatorConditionMode.IsTrue, 0f), // a trigger
            _ => null,
        };
    }

    /// <summary>Cross-checks names, kinds and numbers: what a hand-written file most often gets wrong.</summary>
    private IEnumerable<SerializationDiagnostic> Validate()
    {
        SerializationDiagnostic Error(string path, string message) => new(DiagnosticSeverity.Error, path, message);
        foreach (var duplicate in States.GroupBy(s => s.Name).Where(g => g.Count() > 1))
        {
            yield return Error("states", $"Two states are named '{duplicate.Key}'.");
        }

        if (FindState(DefaultState) is null)
        {
            yield return Error("defaultState", $"The default state '{DefaultState}' is not a state.");
        }

        for (var i = 0; i < Transitions.Count; i++)
        {
            var t = Transitions[i];
            var path = $"transitions[{i}]";
            if (t.From is { } from && FindState(from) is null)
            {
                yield return Error(path + ".from", $"'{from}' is not a state.");
            }

            if (FindState(t.To) is null)
            {
                yield return Error(path + ".to", $"'{t.To}' is not a state.");
            }

            if (!(t.Duration >= 0f && float.IsFinite(t.Duration)))
            {
                yield return Error(path + ".duration", "'duration' must be 0 or more seconds.");
            }

            if (t.ExitTime is { } exit && !(exit >= 0f && float.IsFinite(exit)))
            {
                yield return Error(path + ".exitTime", "'exitTime' must be 0 or more (in clip lengths).");
            }

            if (t.Conditions.Count == 0 && t.ExitTime is null)
            {
                yield return Error(path, "A transition needs a condition or an exit time, or it fires every frame.");
            }

            foreach (var condition in t.Conditions)
            {
                if (FindParameter(condition.Parameter) is not { } parameter)
                {
                    yield return Error(path, $"The condition uses '{condition.Parameter}', which is not a parameter.");
                }
                else if ((parameter.Type is AnimatorParameterType.Bool or AnimatorParameterType.Trigger) != (condition.Mode is AnimatorConditionMode.IsTrue or AnimatorConditionMode.IsFalse)
                    || (parameter.Type is AnimatorParameterType.Trigger && condition.Mode is AnimatorConditionMode.IsFalse))
                {
                    yield return Error(path, $"'{condition.Parameter}' is a {parameter.Type.ToString().ToLowerInvariant()} parameter; {(parameter.Type is AnimatorParameterType.Bool ? "compare it with \"is\"" : parameter.Type is AnimatorParameterType.Trigger ? "a trigger condition is just the parameter" : "compare it with greater, less, equals or notEquals")}.");
                }
            }
        }

        foreach (var orphan in Transitions.Where(t => t.From is null && t.Conditions.Count == 0))
        {
            yield return Error("transitions", $"The \"any state\" transition to '{orphan.To}' needs a condition; with an exit time alone it would fire from every state.");
        }
    }
}
