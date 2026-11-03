using System.Globalization;

namespace MNVTEngine.Scripting.VM;

/// <summary>Functions every VM provides, matching <see cref="Compiler.ScriptApi.Builtins"/>. They touch nothing outside the VM.</summary>
public static class Builtins
{
    public static IEnumerable<NativeFunction> Create() =>
    [
        new("len", c => ScriptValue.FromNumber(c[0].Kind switch
        {
            ValueKind.List => c[0].AsList().Count,
            ValueKind.Map => c[0].AsMap().Count,
            ValueKind.String => c[0].AsString().Length,
            _ => throw new ScriptRuntimeException($"len() needs a list, map or string, got {c[0].TypeName}."),
        })),
        new("str", c => ScriptValue.FromString(c[0].ToString())),
        new("num", c => c[0].Kind switch
        {
            ValueKind.Number => c[0],
            ValueKind.Bool => ScriptValue.FromNumber(c[0].AsBool() ? 1 : 0),
            ValueKind.String => double.TryParse(c[0].AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? ScriptValue.FromNumber(n) : ScriptValue.Null,
            _ => ScriptValue.Null,
        }),
        new("push", c =>
        {
            c[0].AsList("argument 1 of push").Add(c[1]);
            return c[0];
        }),
        new("keys", c => ScriptValue.FromList([.. c[0].AsMap("argument 1 of keys").Keys.Select(ScriptValue.FromString)])),
        new("contains", c => ScriptValue.FromBool(c[0].Kind switch
        {
            ValueKind.List => c[0].AsList().Contains(c[1]),
            ValueKind.Map => c[0].AsMap().ContainsKey(c[1].AsString("map key")),
            ValueKind.String => c[0].AsString().Contains(c[1].AsString("substring"), StringComparison.Ordinal),
            _ => throw new ScriptRuntimeException($"contains() needs a list, map or string, got {c[0].TypeName}."),
        })),
        new("abs", c => ScriptValue.FromNumber(Math.Abs(c.Number(0)))),
        new("min", c => ScriptValue.FromNumber(Math.Min(c.Number(0), c.Number(1)))),
        new("max", c => ScriptValue.FromNumber(Math.Max(c.Number(0), c.Number(1)))),
        new("floor", c => ScriptValue.FromNumber(Math.Floor(c.Number(0)))),
        new("ceil", c => ScriptValue.FromNumber(Math.Ceiling(c.Number(0)))),
        new("round", c => ScriptValue.FromNumber(Math.Round(c.Number(0), MidpointRounding.AwayFromZero))),
        new("sqrt", c => c.Number(0) >= 0 ? ScriptValue.FromNumber(Math.Sqrt(c.Number(0))) : throw new ScriptRuntimeException("sqrt() of a negative number.")),
        new("clamp", c => ScriptValue.FromNumber(Math.Clamp(c.Number(0), c.Number(1), Math.Max(c.Number(1), c.Number(2))))),
        new("random", c => ScriptValue.FromNumber(c.Count switch
        {
            0 => Random.Shared.NextDouble(),
            1 => Random.Shared.NextDouble() * c.Number(0),
            _ => c.Number(0) + (Random.Shared.NextDouble() * (c.Number(1) - c.Number(0))),
        })),
        new("type_of", c => ScriptValue.FromString(c[0].TypeName)),
    ];
}
