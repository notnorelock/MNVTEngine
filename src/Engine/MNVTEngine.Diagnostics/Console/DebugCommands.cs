using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace MNVTEngine.Diagnostics;

public sealed record DebugCommandResult(bool Success, string Output)
{
    public static DebugCommandResult Ok(string output = "") => new(true, output);

    public static DebugCommandResult Fail(string output) => new(false, output);
}

/// <param name="Usage">Arguments, e.g. "&lt;budget&gt; &lt;limit&gt;".</param>
public sealed record DebugCommand(string Name, string Help, string Usage, Func<IReadOnlyList<string>, DebugCommandResult> Handler);

/// <summary>
/// Commands of the developer console. Only registered delegates run: there is no shell, process or file access unless
/// a registered command itself does it. Names are lowercase letters, digits, '.', '_' and '-'. Arguments are split on
/// spaces; double quotes group words. A failing handler returns its exception message instead of breaking the console.
/// </summary>
public sealed partial class DebugCommandRegistry
{
    private readonly SortedDictionary<string, DebugCommand> _commands = new(StringComparer.Ordinal);

    public DebugCommandRegistry() =>
        Register("help", "Lists commands, or shows one command's usage.", "[command]", args =>
        {
            if (args.Count > 0)
            {
                return TryGet(args[0], out var command)
                    ? DebugCommandResult.Ok($"{command.Name} {command.Usage}\n  {command.Help}")
                    : DebugCommandResult.Fail($"Unknown command '{args[0]}'.");
            }

            var text = new StringBuilder();
            foreach (var command in _commands.Values)
            {
                text.Append(command.Name).Append(' ').Append(command.Usage).Append(" - ").AppendLine(command.Help);
            }

            return DebugCommandResult.Ok(text.ToString().TrimEnd());
        });

    public IEnumerable<DebugCommand> Commands => _commands.Values;

    /// <exception cref="ArgumentException">Invalid or already registered name.</exception>
    public DebugCommandRegistry Register(string name, string help, string usage, Func<IReadOnlyList<string>, DebugCommandResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!ValidName().IsMatch(name ?? ""))
        {
            throw new ArgumentException($"'{name}' is not a valid command name (lowercase letters, digits, '.', '_', '-').", nameof(name));
        }

        if (!_commands.TryAdd(name!, new DebugCommand(name!, help ?? "", usage ?? "", handler)))
        {
            throw new ArgumentException($"Command '{name}' is already registered.", nameof(name));
        }

        return this;
    }

    public DebugCommandRegistry Register(string name, string help, Func<IReadOnlyList<string>, DebugCommandResult> handler) => Register(name, help, "", handler);

    public bool Unregister(string name) => name != "help" && _commands.Remove(name);

    public bool TryGet(string name, [NotNullWhen(true)] out DebugCommand? command) => _commands.TryGetValue(name.ToLowerInvariant(), out command);

    /// <summary>Names starting with <paramref name="prefix"/>, for tab completion.</summary>
    public IEnumerable<string> Complete(string prefix) => _commands.Keys.Where(k => k.StartsWith(prefix.ToLowerInvariant(), StringComparison.Ordinal));

    [SuppressMessage("Design", "CA1031", Justification = "A broken game command must not take the console down; the error is returned to the user.")]
    public DebugCommandResult Execute(string line)
    {
        var tokens = Tokenize(line ?? "");
        if (tokens.Count == 0)
        {
            return DebugCommandResult.Ok();
        }

        if (!TryGet(tokens[0], out var command))
        {
            return DebugCommandResult.Fail($"Unknown command '{tokens[0]}'. Type 'help' for the list.");
        }

        try
        {
            return command.Handler(tokens.GetRange(1, tokens.Count - 1));
        }
        catch (Exception ex)
        {
            return DebugCommandResult.Fail($"{command.Name} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static List<string> Tokenize(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,47}$")]
    private static partial Regex ValidName();
}
