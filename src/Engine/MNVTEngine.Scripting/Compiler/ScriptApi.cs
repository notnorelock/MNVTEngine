namespace MNVTEngine.Scripting.Compiler;

/// <param name="Name">"func" for global functions or "module.func".</param>
public sealed record NativeSignature(string Name, int MinArgs, int MaxArgs, string Summary)
{
    public string? Module => Name.Contains('.', StringComparison.Ordinal) ? Name[..Name.IndexOf('.', StringComparison.Ordinal)] : null;
}

/// <summary>
/// Declares the host functions scripts may call. The compiler checks names and argument counts against it; the VM links
/// the same names to implementations. Scripts can reach nothing that is not declared and provided: there is no file,
/// process, reflection or native access.
/// </summary>
public sealed class ScriptApi
{
    private readonly Dictionary<string, NativeSignature> _functions = new(StringComparer.Ordinal);

    public IEnumerable<NativeSignature> Functions => _functions.Values;

    public IEnumerable<string> Modules => _functions.Values.Select(f => f.Module).OfType<string>().Distinct();

    /// <summary>Always-available functions implemented by the VM itself (<see cref="VM.Builtins"/>).</summary>
    public static ScriptApi Builtins { get; } = new ScriptApi()
        .Add("len", 1, 1, "Number of items in a list, map or string.")
        .Add("str", 1, 1, "Text form of a value.")
        .Add("num", 1, 1, "Converts a string or bool to a number (null if not convertible).")
        .Add("push", 2, 2, "Appends a value to a list.")
        .Add("keys", 1, 1, "List of a map's keys.")
        .Add("contains", 2, 2, "True if a list holds the value, a map the key, or a string the substring.")
        .Add("abs", 1, 1, "Absolute value.")
        .Add("min", 2, 2, "Smaller of two numbers.")
        .Add("max", 2, 2, "Larger of two numbers.")
        .Add("floor", 1, 1, "Rounds down.")
        .Add("ceil", 1, 1, "Rounds up.")
        .Add("round", 1, 1, "Rounds to the nearest integer.")
        .Add("sqrt", 1, 1, "Square root.")
        .Add("clamp", 3, 3, "clamp(value, min, max).")
        .Add("random", 0, 2, "random() in [0,1); random(max); random(min, max).")
        .Add("type_of", 1, 1, "Type name: null, bool, number, string, list, map, range.");

    public ScriptApi Add(string name, int minArgs, int maxArgs, string summary)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegative(minArgs);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxArgs, minArgs);
        _functions[name] = new NativeSignature(name, minArgs, maxArgs, summary);
        return this;
    }

    public ScriptApi Merge(ScriptApi other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var function in other.Functions)
        {
            _functions[function.Name] = function;
        }

        return this;
    }

    public bool TryGet(string name, out NativeSignature signature) => _functions.TryGetValue(name, out signature!);

    public bool IsModule(string name) => _functions.Keys.Any(k => k.StartsWith(name + ".", StringComparison.Ordinal));
}
