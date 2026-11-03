using System.Globalization;

namespace MNVTEngine.CLI;

/// <summary>A parsed command: name, positional arguments, <c>--option value</c> pairs and flags. Unknown options are errors.</summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly List<string> _positional = [];

    private CommandLine(string name) => Name = name;

    public string Name { get; }

    public IReadOnlyList<string> Positional => _positional;

    /// <param name="optionalValues">Options whose value may be omitted (<c>--run</c> or <c>--run 60</c>).</param>
    /// <exception cref="FormatException">Unknown option, missing value or repeated option.</exception>
    public static CommandLine Parse(string[] args, string[] valueOptions, string[] flags, string[]? optionalValues = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        optionalValues ??= [];
        var command = new CommandLine(args[0]);
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                command._positional.Add(arg);
                continue;
            }

            string? value = null;
            if (valueOptions.Contains(arg))
            {
                value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : throw new FormatException($"Option {arg} needs a value.");
            }
            else if (optionalValues.Contains(arg))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    value = args[++i];
                }
            }
            else if (!flags.Contains(arg))
            {
                throw new FormatException($"Unknown option {arg} for '{command.Name}'.");
            }

            if (!command._options.TryAdd(arg, value))
            {
                throw new FormatException($"Option {arg} given twice.");
            }
        }

        return command;
    }

    public bool Has(string option) => _options.ContainsKey(option);

    public string? Value(string option) => _options.GetValueOrDefault(option);

    /// <exception cref="FormatException">The value is not a positive integer.</exception>
    public int Int(string option, int fallback) =>
        Value(option) is not { } text ? fallback
        : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value
        : throw new FormatException($"Option {option} needs a positive whole number, got '{text}'.");

    /// <summary>The single positional argument.</summary>
    /// <exception cref="FormatException">None or several were given.</exception>
    public string Single(string what) => _positional.Count == 1
        ? _positional[0]
        : throw new FormatException(_positional.Count == 0 ? $"'{Name}' needs a {what}." : $"'{Name}' takes one {what}, got {_positional.Count} arguments.");

    /// <summary>The given options in their original <c>--name value</c> form, for forwarding to another program.</summary>
    public IEnumerable<string> Passthrough(params string[] options)
    {
        foreach (var option in options.Where(Has))
        {
            yield return option;
            if (Value(option) is { } value)
            {
                yield return value;
            }
        }
    }
}
