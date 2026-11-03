using MNVTEngine.Scripting.VM;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Scripting.Missions;

public sealed class ObjectiveSaveEntry
{
    public string Id { get; set; } = "";

    public ObjectiveState State { get; set; }
}

public sealed class MissionSaveEntry
{
    public string Id { get; set; } = "";

    public MissionState State { get; set; }

    public string? FailReason { get; set; }

    public List<ObjectiveSaveEntry> Objectives { get; set; } = [];
}

/// <summary>Persistent mission state: script globals (including mission variables) and mission/objective states.</summary>
public sealed class MissionSaveData
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Dictionary<string, JToken?> Globals { get; set; } = [];

    public List<MissionSaveEntry> Missions { get; set; } = [];

    public static JsonMigrationPipeline Migrations { get; } = new(CurrentSchemaVersion);

    public string ToJson() => MnvtJson.Serialize(this);

    /// <exception cref="SerializationException">Malformed or unsupported save data.</exception>
    public static MissionSaveData FromJson(string json, string sourceName = "<save>")
    {
        var diagnostics = new List<SerializationDiagnostic>();
        try
        {
            var document = JObject.Parse(json);
            Migrations.Migrate(document, diagnostics);
            return document.ToObject<MissionSaveData>(MnvtJson.CreateSerializer()) ?? throw new InvalidDataException("Empty save data.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new SerializationException(sourceName, ex.Message, diagnostics, ex);
        }
    }
}

public sealed partial class MissionManager
{
    /// <summary>
    /// Captures globals and mission states. Running coroutines are not saved: on restore, active objective bodies restart
    /// from their beginning, so write bodies as conditions to (re)check ("wait until ...", "wait for event ...").
    /// </summary>
    public MissionSaveData Save(ICollection<SerializationDiagnostic>? diagnostics = null)
    {
        var data = new MissionSaveData();
        for (var slot = 0; slot < Script.Globals.Length; slot++)
        {
            var global = Script.Globals[slot];
            if (global.IsConst)
            {
                continue;
            }

            if (TryToJson(Vm.Globals[slot], out var token))
            {
                data.Globals[global.Name] = token;
            }
            else
            {
                diagnostics?.Add(new SerializationDiagnostic(DiagnosticSeverity.Warning, global.Name, $"Value of type {Vm.Globals[slot].TypeName} cannot be saved; it will restore as its initial value."));
            }
        }

        foreach (var mission in _missions.Values.Where(m => m.State != MissionState.Inactive))
        {
            data.Missions.Add(new MissionSaveEntry
            {
                Id = mission.Info.Id,
                State = mission.State,
                FailReason = mission.FailReason,
                Objectives = [.. mission.Objectives.Values.Select(o => new ObjectiveSaveEntry { Id = o.Info.Id, State = o.State })],
            });
        }

        return data;
    }

    /// <summary>
    /// Restores a save into a freshly initialized VM (call after <see cref="ScriptVM.Initialize"/>, before ticking). Saved
    /// names the current script no longer has are reported and skipped; 'on start' hooks do not run again.
    /// </summary>
    public IReadOnlyList<SerializationDiagnostic> Restore(MissionSaveData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var diagnostics = new List<SerializationDiagnostic>();
        foreach (var (name, token) in data.Globals)
        {
            var slot = Script.FindGlobal(name);
            if (slot < 0 || Script.Globals[slot].IsConst)
            {
                diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Warning, name, "Saved variable no longer exists in the script; value dropped."));
                continue;
            }

            Vm.Globals[slot] = FromJson(token);
        }

        foreach (var entry in data.Missions)
        {
            if (!_missions.TryGetValue(entry.Id, out var mission))
            {
                diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Warning, entry.Id, "Saved mission no longer exists in the script; skipped."));
                continue;
            }

            mission.State = entry.State;
            mission.FailReason = entry.FailReason;
            foreach (var saved in entry.Objectives)
            {
                if (mission.Objectives.TryGetValue(saved.Id, out var objective))
                {
                    objective.State = saved.State;
                }
                else
                {
                    diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Warning, $"{entry.Id}/{saved.Id}", "Saved objective no longer exists; skipped."));
                }
            }

            if (mission.State == MissionState.Active)
            {
                foreach (var objective in mission.Objectives.Values.Where(o => o.State == ObjectiveState.Active))
                {
                    Vm.Spawn(objective.Info.Body, [], new MissionContext(entry.Id, objective.Info.Id));
                }
            }
        }

        return diagnostics;
    }

    private static bool TryToJson(ScriptValue value, out JToken? token)
    {
        token = null;
        switch (value.Kind)
        {
            case ValueKind.Null:
                return true;
            case ValueKind.Bool:
                token = value.AsBool();
                return true;
            case ValueKind.Number:
                token = value.AsNumber();
                return true;
            case ValueKind.String:
                token = value.AsString();
                return true;
            case ValueKind.List:
                var array = new JArray();
                foreach (var item in value.AsList())
                {
                    if (!TryToJson(item, out var child))
                    {
                        return false;
                    }

                    array.Add(child ?? JValue.CreateNull());
                }

                token = array;
                return true;
            case ValueKind.Map:
                var obj = new JObject();
                foreach (var (key, item) in value.AsMap())
                {
                    if (!TryToJson(item, out var child))
                    {
                        return false;
                    }

                    obj[key] = child ?? JValue.CreateNull();
                }

                token = obj;
                return true;
            default:
                return false;
        }
    }

    private static ScriptValue FromJson(JToken? token) => token?.Type switch
    {
        null or JTokenType.Null => ScriptValue.Null,
        JTokenType.Boolean => ScriptValue.FromBool(token.Value<bool>()),
        JTokenType.Integer or JTokenType.Float => ScriptValue.FromNumber(token.Value<double>()),
        JTokenType.String => ScriptValue.FromString(token.Value<string>()!),
        JTokenType.Array => ScriptValue.FromList([.. token.Select(FromJson)]),
        JTokenType.Object => ScriptValue.FromMap(((JObject)token).Properties().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.Ordinal)),
        _ => throw new InvalidDataException($"Unsupported saved value type {token.Type} at '{token.Path}'."),
    };
}
