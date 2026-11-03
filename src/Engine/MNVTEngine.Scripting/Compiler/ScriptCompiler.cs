using MNVTEngine.Scripting.Bytecode;
using MNVTEngine.Scripting.Syntax;
using MNVTEngine.Scripting.VM;

namespace MNVTEngine.Scripting.Compiler;

public sealed record CompileOptions
{
    /// <summary>Host functions available to scripts (in addition to <see cref="ScriptApi.Builtins"/>).</summary>
    public ScriptApi Api { get; init; } = new();

    /// <summary>
    /// Treat calls to undeclared <c>module.function</c> names as warnings (resolved when the VM links the script) instead
    /// of errors. Used when compiling without the game's own API, e.g. at asset import.
    /// </summary>
    public bool AllowUnknownFunctions { get; init; }
}

public sealed record CompileResult(CompiledScript? Script, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Succeeded => Script is not null;

    public IEnumerable<Diagnostic> Errors => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>Compiles <c>.mnvts</c> source to <see cref="CompiledScript"/> bytecode.</summary>
public static class ScriptCompiler
{
    public static CompileResult Compile(string text, string sourceName, CompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sourceName);
        options ??= new CompileOptions();
        var diagnostics = new DiagnosticBag(sourceName);
        var unit = Parser.Parse(text, diagnostics);
        if (diagnostics.HasErrors)
        {
            return new CompileResult(null, diagnostics.Items);
        }

        var api = new ScriptApi().Merge(ScriptApi.Builtins).Merge(options.Api);
        var script = new CodeGenerator(sourceName, api, options.AllowUnknownFunctions, diagnostics).Generate(unit);
        if (diagnostics.HasErrors)
        {
            return new CompileResult(null, diagnostics.Items);
        }

        BytecodeSerializer.Verify(script); // the compiler must never emit what the loader would reject
        return new CompileResult(script, diagnostics.Items);
    }
}
