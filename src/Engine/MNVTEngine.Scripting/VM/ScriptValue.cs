using System.Globalization;
using System.Text;

namespace MNVTEngine.Scripting.VM;

public enum ValueKind
{
    Null,
    Bool,
    Number,
    String,
    List,
    Map,
    Range,
    Function,

    /// <summary>Opaque host or VM object (iterators, native handles); not visible as data to scripts.</summary>
    Opaque,
}

/// <summary>Half-open numeric range produced by <c>a..b</c>.</summary>
public sealed record ScriptRange(double Start, double End);

/// <summary>
/// Dynamically typed script value. Numbers are doubles. Lists and maps are mutable reference types; everything else is
/// compared by value. <c>null</c> and <c>false</c> are falsy, everything else is truthy.
/// </summary>
public readonly struct ScriptValue : IEquatable<ScriptValue>
{
    private readonly double _number;
    private readonly object? _reference;

    private ScriptValue(ValueKind kind, double number, object? reference)
    {
        Kind = kind;
        _number = number;
        _reference = reference;
    }

    public ValueKind Kind { get; }

    public static ScriptValue Null => default;

    public static ScriptValue True => new(ValueKind.Bool, 1, null);

    public static ScriptValue False => new(ValueKind.Bool, 0, null);

    public bool IsNull => Kind == ValueKind.Null;

    public bool IsTruthy => Kind switch
    {
        ValueKind.Null => false,
        ValueKind.Bool => _number != 0,
        _ => true,
    };

    public string TypeName => Kind switch
    {
        ValueKind.Null => "null",
        ValueKind.Bool => "bool",
        ValueKind.Number => "number",
        ValueKind.String => "string",
        ValueKind.List => "list",
        ValueKind.Map => "map",
        ValueKind.Range => "range",
        ValueKind.Function => "function",
        _ => "object",
    };

    public static ScriptValue FromBool(bool value) => value ? True : False;

    public static ScriptValue FromNumber(double value) => new(ValueKind.Number, value, null);

    public static ScriptValue FromString(string value) => new(ValueKind.String, 0, value ?? throw new ArgumentNullException(nameof(value)));

    public static ScriptValue FromList(List<ScriptValue> value) => new(ValueKind.List, 0, value);

    public static ScriptValue FromMap(Dictionary<string, ScriptValue> value) => new(ValueKind.Map, 0, value);

    public static ScriptValue FromRange(double start, double end) => new(ValueKind.Range, 0, new ScriptRange(start, end));

    public static ScriptValue FromFunction(int index) => new(ValueKind.Function, index, null);

    public static ScriptValue FromOpaque(object value) => new(ValueKind.Opaque, 0, value);

    /// <summary>Converts host values: null, bool, numeric types, strings, ScriptValue; other objects become opaque handles.</summary>
    public static ScriptValue FromObject(object? value) => value switch
    {
        null => Null,
        ScriptValue v => v,
        bool b => FromBool(b),
        string s => FromString(s),
        double d => FromNumber(d),
        float f => FromNumber(f),
        int i => FromNumber(i),
        long l => FromNumber(l),
        _ => FromOpaque(value),
    };

    public bool AsBool(string what = "value") =>
        Kind == ValueKind.Bool ? _number != 0 : throw TypeError(what, "bool");

    public double AsNumber(string what = "value") =>
        Kind == ValueKind.Number ? _number : throw TypeError(what, "number");

    public string AsString(string what = "value") =>
        Kind == ValueKind.String ? (string)_reference! : throw TypeError(what, "string");

    public List<ScriptValue> AsList(string what = "value") =>
        Kind == ValueKind.List ? (List<ScriptValue>)_reference! : throw TypeError(what, "list");

    public Dictionary<string, ScriptValue> AsMap(string what = "value") =>
        Kind == ValueKind.Map ? (Dictionary<string, ScriptValue>)_reference! : throw TypeError(what, "map");

    public ScriptRange AsRange() => (ScriptRange)_reference!;

    public int AsFunction() => Kind == ValueKind.Function ? (int)_number : throw TypeError("value", "function");

    public object? AsOpaque() => Kind == ValueKind.Opaque ? _reference : null;

    /// <summary>Host-friendly conversion: null, bool, double, string, List&lt;object?&gt;, Dictionary&lt;string, object?&gt;.</summary>
    public object? ToObject() => Kind switch
    {
        ValueKind.Null => null,
        ValueKind.Bool => _number != 0,
        ValueKind.Number => _number,
        ValueKind.String => _reference,
        ValueKind.List => AsList().Select(v => v.ToObject()).ToList(),
        ValueKind.Map => AsMap().ToDictionary(p => p.Key, p => p.Value.ToObject()),
        _ => _reference,
    };

    public bool Equals(ScriptValue other) => Kind == other.Kind && Kind switch
    {
        ValueKind.Null => true,
        ValueKind.Bool or ValueKind.Number or ValueKind.Function => _number.Equals(other._number),
        ValueKind.String => string.Equals((string)_reference!, (string)other._reference!, StringComparison.Ordinal),
        ValueKind.Range => Equals(_reference, other._reference),
        _ => ReferenceEquals(_reference, other._reference),
    };

    public override bool Equals(object? obj) => obj is ScriptValue other && Equals(other);

    public override int GetHashCode() => Kind switch
    {
        ValueKind.Null => 0,
        ValueKind.Bool or ValueKind.Number or ValueKind.Function => _number.GetHashCode(),
        ValueKind.String => StringComparer.Ordinal.GetHashCode((string)_reference!),
        _ => _reference?.GetHashCode() ?? 0,
    };

    public static bool operator ==(ScriptValue left, ScriptValue right) => left.Equals(right);

    public static bool operator !=(ScriptValue left, ScriptValue right) => !left.Equals(right);

    /// <summary>Text as shown by <c>str()</c>, <c>log()</c> and string concatenation.</summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        Append(builder, this, depth: 0);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, ScriptValue value, int depth)
    {
        if (depth > 8)
        {
            builder.Append("...");
            return;
        }

        switch (value.Kind)
        {
            case ValueKind.Null:
                builder.Append("null");
                break;
            case ValueKind.Bool:
                builder.Append(value._number != 0 ? "true" : "false");
                break;
            case ValueKind.Number:
                builder.Append(value._number.ToString("0.###############", CultureInfo.InvariantCulture));
                break;
            case ValueKind.String:
                builder.Append((string)value._reference!);
                break;
            case ValueKind.List:
                builder.Append('[');
                var first = true;
                foreach (var item in value.AsList())
                {
                    builder.Append(first ? "" : ", ");
                    Append(builder, item, depth + 1);
                    first = false;
                }

                builder.Append(']');
                break;
            case ValueKind.Map:
                builder.Append('{');
                var firstEntry = true;
                foreach (var (key, item) in value.AsMap())
                {
                    builder.Append(firstEntry ? "" : ", ").Append(key).Append(": ");
                    Append(builder, item, depth + 1);
                    firstEntry = false;
                }

                builder.Append('}');
                break;
            case ValueKind.Range:
                var range = value.AsRange();
                builder.Append(CultureInfo.InvariantCulture, $"{range.Start}..{range.End}");
                break;
            default:
                builder.Append('<').Append(value.TypeName).Append('>');
                break;
        }
    }

    private ScriptRuntimeException TypeError(string what, string expected) =>
        new($"Expected {what} to be a {expected}, got {TypeName}.");
}

/// <summary>An error raised by script code or the runtime; catchable with <c>try/catch</c>.</summary>
public sealed class ScriptRuntimeException(string message, Exception? inner = null) : Exception(message, inner);
