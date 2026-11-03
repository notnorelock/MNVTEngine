using MNVTEngine.Scripting.VM;

namespace MNVTEngine.Scripting.Bytecode;

/// <summary>
/// Stack-machine instruction set. Code is an <c>int[]</c>: an opcode followed by its operands (see <see cref="OpCodes.OperandCount"/>).
/// Jump targets are absolute instruction indices within the function.
/// </summary>
public enum OpCode
{
    Constant,        // k                  -> constants[k]
    Null,            //                    -> null
    True,
    False,
    Pop,             // v ->
    Dup,             // v -> v v
    Dup2,            // a b -> a b a b
    LoadLocal,       // slot               -> locals[slot]
    StoreLocal,      // slot          v ->
    LoadGlobal,      // slot
    StoreGlobal,     // slot
    Add,             // a b -> a+b (numbers, or string concatenation)
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Negate,
    Not,
    Equal,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    Jump,            // target
    JumpIfFalse,     // target         v ->            (pops)
    JumpIfFalseKeep, // target         v -> v | jump   ('and': keeps falsy value)
    JumpIfTrueKeep,  // target                          ('or': keeps truthy value)
    Call,            // function argc  args -> result
    CallNative,      // native argc    args -> result
    Return,          //                v ->            (ends the frame)
    MakeList,        // n              items -> list
    MakeMap,         // n              key value ... -> map
    MakeRange,       //                start end -> range
    GetIndex,        //                target index -> value
    SetIndex,        //                target index value ->
    GetMember,       // k              target -> value (k: constant name)
    SetMember,       // k              target value ->
    Wait,            //                seconds ->      (suspends)
    Yield,           //                                (resumes next tick)
    WaitEvent,       //                name -> payload (suspends until the event)
    Spawn,           // function argc  args ->         (starts a coroutine)
    IterStart,       // slot           iterable ->
    IterNext,        // slot target    -> value | jump when exhausted
    TryBegin,        // handler
    TryEnd,
    Throw,           //                value ->
}

public static class OpCodes
{
    public static int OperandCount(OpCode op) => op switch
    {
        OpCode.Constant or OpCode.LoadLocal or OpCode.StoreLocal or OpCode.LoadGlobal or OpCode.StoreGlobal
            or OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfFalseKeep or OpCode.JumpIfTrueKeep
            or OpCode.MakeList or OpCode.MakeMap or OpCode.GetMember or OpCode.SetMember
            or OpCode.IterStart or OpCode.TryBegin => 1,
        OpCode.Call or OpCode.CallNative or OpCode.Spawn or OpCode.IterNext => 2,
        _ => 0,
    };
}

public sealed record LineEntry(int Pc, int Line);

public sealed class CompiledFunction
{
    public required string Name { get; init; }

    public required int ParameterCount { get; init; }

    /// <summary>Total local slots including parameters.</summary>
    public required int LocalCount { get; init; }

    /// <summary>Name per local slot (hidden slots start with '$'), for debuggers.</summary>
    public required string[] LocalNames { get; init; }

    public required int[] Code { get; init; }

    /// <summary>Statement start positions, ascending by pc.</summary>
    public required LineEntry[] Lines { get; init; }

    public int LineAt(int pc)
    {
        var line = 0;
        foreach (var entry in Lines)
        {
            if (entry.Pc > pc)
            {
                break;
            }

            line = entry.Line;
        }

        return line;
    }
}

public sealed record GlobalInfo(string Name, bool IsConst);

/// <param name="Mission">Owning mission, or null for a top-level handler.</param>
public sealed record EventHandlerInfo(string EventName, int Function, string? Mission);

public sealed record ObjectiveInfo(string Id, string Description, bool Optional, bool Hidden, string[] Requires, int Body);

/// <param name="Hooks">start, complete, fail, cancel -> function index.</param>
/// <param name="Variables">Global slots holding this mission's variables.</param>
public sealed record MissionInfo(
    string Id,
    string Name,
    string Description,
    string Category,
    IReadOnlyDictionary<string, int> Hooks,
    ObjectiveInfo[] Objectives,
    int[] Variables);

/// <summary>Output of the compiler and unit of loading for the VM. Persisted in the binary format by <see cref="BytecodeSerializer"/>.</summary>
public sealed class CompiledScript
{
    public required string SourceName { get; init; }

    /// <summary>Null, bool, number and string constants only.</summary>
    public required ScriptValue[] Constants { get; init; }

    public required CompiledFunction[] Functions { get; init; }

    public required GlobalInfo[] Globals { get; init; }

    /// <summary>Runs global initializers; must not wait.</summary>
    public required int InitFunction { get; init; }

    /// <summary>Top-level <c>on start</c>, or -1.</summary>
    public required int StartFunction { get; init; }

    public required EventHandlerInfo[] EventHandlers { get; init; }

    public required MissionInfo[] Missions { get; init; }

    /// <summary>Host functions referenced by name (e.g. "log", "player.distance_to"); linked when a VM loads the script.</summary>
    public required string[] NativeNames { get; init; }

    public int FindFunction(string name) => Array.FindIndex(Functions, f => f.Name == name);

    public int FindGlobal(string name) => Array.FindIndex(Globals, g => g.Name == name);
}
