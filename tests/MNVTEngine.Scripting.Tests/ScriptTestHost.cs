using MNVTEngine.Scripting.Compiler;
using MNVTEngine.Scripting.VM;

namespace MNVTEngine.Scripting.Tests;

/// <summary>Compiles a script with a <c>log(x)</c> host function that records output, and runs it.</summary>
internal sealed class ScriptTestHost
{
    public ScriptTestHost(string source, ScriptLimits? limits = null, IEnumerable<NativeFunction>? natives = null, ScriptApi? api = null)
    {
        var result = ScriptCompiler.Compile(source, "test.mnvts", new CompileOptions { Api = (api ?? new ScriptApi()).Add("log", 1, 1, "test log") });
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Vm = new ScriptVM(result.Script!, [new NativeFunction("log", c =>
        {
            Log.Add(c[0].ToString());
            return ScriptValue.Null;
        }), .. natives ?? []], limits);
        Vm.FiberFaulted += f => Faults.Add(f.Error!);
        Vm.Initialize();
    }

    public ScriptVM Vm { get; }

    public List<string> Log { get; } = [];

    public List<ScriptError> Faults { get; } = [];

    public ScriptTestHost Tick(double seconds = 0.1, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            Vm.Tick(seconds);
        }

        return this;
    }

    public static List<string> RunToEnd(string source, int ticks = 5) => new ScriptTestHost(source).Tick(0.1, ticks).Log;

    public static IReadOnlyList<Syntax.Diagnostic> Errors(string source, CompileOptions? options = null) =>
        [.. ScriptCompiler.Compile(source, "test.mnvts", options).Errors];
}
