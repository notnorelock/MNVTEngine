using MNVTEngine.Mathematics;
using MNVTEngine.Scripting.VM;

namespace MNVTEngine;

/// <summary>
/// Implementations of <see cref="Scripting.Missions.StandardApi.Scene"/>. Scripts address objects by name and get plain
/// values back (numbers, strings, {x, y} maps), never engine objects, so they cannot reach beyond this API.
/// </summary>
public static class SceneNatives
{
    public static IEnumerable<NativeFunction> Create(Scene scene, Func<string> playerName)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(playerName);

        GameObject Find(NativeCall c, int index) =>
            scene.Find(c.String(index)) is { IsDestroyed: false } found ? found : throw new ScriptRuntimeException($"No object named '{c.String(index)}' in the scene.");

        GameObject Player() =>
            scene.Find(playerName()) ?? throw new ScriptRuntimeException($"No player object named '{playerName()}' in the scene.");

        return
        [
            new("player.position", _ => Position(Player().Transform.Position)),
            new("player.distance_to", c => ScriptValue.FromNumber(Vector2.Distance(Player().Transform.Position, Find(c, 0).Transform.Position))),
            new("entity.exists", c => ScriptValue.FromBool(scene.Find(c.String(0)) is { IsDestroyed: false, ActiveInHierarchy: true })),
            new("entity.position", c => Position(Find(c, 0).Transform.Position)),
            new("entity.distance", c => ScriptValue.FromNumber(Vector2.Distance(Find(c, 0).Transform.Position, Find(c, 1).Transform.Position))),
            new("entity.set_active", c => SetActive(Find(c, 0), c.Bool(1))),
            new("entity.destroy", c =>
            {
                Find(c, 0).Destroy();
                return ScriptValue.Null;
            }),
            new("marker.show", c => SetActive(Find(c, 0), true)),
            new("marker.hide", c => SetActive(Find(c, 0), false)),
            new("scene.load", c =>
            {
                (scene.Loader ?? throw new ScriptRuntimeException("This scene cannot load other scenes.")).RequestLoad(c.String(0));
                return ScriptValue.Null;
            }),
            new("dialogue.start", c =>
            {
                c.Vm.RaiseEvent("dialogue_started", ScriptValue.FromMap(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
                {
                    ["id"] = ScriptValue.FromString(c.String(0)),
                    ["data"] = c[1],
                }));
                return ScriptValue.Null;
            }),
            new("input.key_down", c => ScriptValue.FromBool(scene.Input?.IsKeyDown(ParseKey(c.String(0))) ?? false)),
            new("input.key_pressed", c => ScriptValue.FromBool(scene.Input?.WasKeyPressed(ParseKey(c.String(0))) ?? false)),
        ];
    }

    private static ScriptValue Position(Vector2 position) => ScriptValue.FromMap(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
    {
        ["x"] = ScriptValue.FromNumber(position.X),
        ["y"] = ScriptValue.FromNumber(position.Y),
    });

    private static ScriptValue SetActive(GameObject target, bool active)
    {
        target.SetActive(active);
        return ScriptValue.Null;
    }

    private static Key ParseKey(string name) =>
        Enum.TryParse<Key>(name, ignoreCase: true, out var key) && Enum.IsDefined(key) ? key : throw new ScriptRuntimeException($"Unknown key '{name}'.");
}
