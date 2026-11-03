using System.Text;
using MNVTEngine.Scripting.VM;

namespace MNVTEngine.Scripting.Bytecode;

/// <summary>
/// Binary <c>.mnvb</c> format: "MNVB" magic, format version, then the script tables. Reading always runs
/// <see cref="Verify"/>, so a corrupt or hand-made file cannot make the VM index out of bounds or jump outside code.
/// </summary>
public static class BytecodeSerializer
{
    public const int FormatVersion = 1;
    private static ReadOnlySpan<byte> Magic => "MNVB"u8;

    public static byte[] Write(CompiledScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(script.SourceName);

            w.Write(script.Constants.Length);
            foreach (var constant in script.Constants)
            {
                w.Write((byte)constant.Kind);
                switch (constant.Kind)
                {
                    case ValueKind.Null:
                        break;
                    case ValueKind.Bool:
                        w.Write(constant.AsBool());
                        break;
                    case ValueKind.Number:
                        w.Write(constant.AsNumber());
                        break;
                    case ValueKind.String:
                        w.Write(constant.AsString());
                        break;
                    default:
                        throw new InvalidOperationException($"Constant of kind {constant.Kind} cannot be serialized.");
                }
            }

            WriteStrings(w, script.NativeNames);
            w.Write(script.Globals.Length);
            foreach (var global in script.Globals)
            {
                w.Write(global.Name);
                w.Write(global.IsConst);
            }

            w.Write(script.Functions.Length);
            foreach (var f in script.Functions)
            {
                w.Write(f.Name);
                w.Write(f.ParameterCount);
                w.Write(f.LocalCount);
                WriteStrings(w, f.LocalNames);
                w.Write(f.Code.Length);
                foreach (var word in f.Code)
                {
                    w.Write(word);
                }

                w.Write(f.Lines.Length);
                foreach (var line in f.Lines)
                {
                    w.Write(line.Pc);
                    w.Write(line.Line);
                }
            }

            w.Write(script.InitFunction);
            w.Write(script.StartFunction);
            w.Write(script.EventHandlers.Length);
            foreach (var handler in script.EventHandlers)
            {
                w.Write(handler.EventName);
                w.Write(handler.Function);
                w.Write(handler.Mission ?? "");
            }

            w.Write(script.Missions.Length);
            foreach (var mission in script.Missions)
            {
                w.Write(mission.Id);
                w.Write(mission.Name);
                w.Write(mission.Description);
                w.Write(mission.Category);
                w.Write(mission.Hooks.Count);
                foreach (var (hook, function) in mission.Hooks.OrderBy(h => h.Key, StringComparer.Ordinal))
                {
                    w.Write(hook);
                    w.Write(function);
                }

                w.Write(mission.Objectives.Length);
                foreach (var o in mission.Objectives)
                {
                    w.Write(o.Id);
                    w.Write(o.Description);
                    w.Write(o.Optional);
                    w.Write(o.Hidden);
                    WriteStrings(w, o.Requires);
                    w.Write(o.Body);
                }

                w.Write(mission.Variables.Length);
                foreach (var variable in mission.Variables)
                {
                    w.Write(variable);
                }
            }
        }

        return stream.ToArray();
    }

    /// <exception cref="InvalidDataException">Not a valid bytecode file of a supported version.</exception>
    public static CompiledScript Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic))
            {
                throw new InvalidDataException("Not an MNVTEngine bytecode file.");
            }

            var version = r.ReadInt32();
            if (version != FormatVersion)
            {
                throw new InvalidDataException($"Bytecode format {version} is not supported (expected {FormatVersion}); recompile the script.");
            }

            var source = r.ReadString();
            var constants = new ScriptValue[Count(r)];
            for (var i = 0; i < constants.Length; i++)
            {
                constants[i] = (ValueKind)r.ReadByte() switch
                {
                    ValueKind.Null => ScriptValue.Null,
                    ValueKind.Bool => ScriptValue.FromBool(r.ReadBoolean()),
                    ValueKind.Number => ScriptValue.FromNumber(r.ReadDouble()),
                    ValueKind.String => ScriptValue.FromString(r.ReadString()),
                    var kind => throw new InvalidDataException($"Invalid constant kind {kind}."),
                };
            }

            var natives = ReadStrings(r);
            var globals = new GlobalInfo[Count(r)];
            for (var i = 0; i < globals.Length; i++)
            {
                globals[i] = new GlobalInfo(r.ReadString(), r.ReadBoolean());
            }

            var functions = new CompiledFunction[Count(r)];
            for (var i = 0; i < functions.Length; i++)
            {
                var name = r.ReadString();
                var parameters = r.ReadInt32();
                var locals = r.ReadInt32();
                var localNames = ReadStrings(r);
                var code = new int[Count(r)];
                for (var c = 0; c < code.Length; c++)
                {
                    code[c] = r.ReadInt32();
                }

                var lines = new LineEntry[Count(r)];
                for (var l = 0; l < lines.Length; l++)
                {
                    lines[l] = new LineEntry(r.ReadInt32(), r.ReadInt32());
                }

                functions[i] = new CompiledFunction { Name = name, ParameterCount = parameters, LocalCount = locals, LocalNames = localNames, Code = code, Lines = lines };
            }

            var init = r.ReadInt32();
            var start = r.ReadInt32();
            var handlers = new EventHandlerInfo[Count(r)];
            for (var i = 0; i < handlers.Length; i++)
            {
                var eventName = r.ReadString();
                var function = r.ReadInt32();
                var mission = r.ReadString();
                handlers[i] = new EventHandlerInfo(eventName, function, mission.Length == 0 ? null : mission);
            }

            var missions = new MissionInfo[Count(r)];
            for (var i = 0; i < missions.Length; i++)
            {
                var id = r.ReadString();
                var title = r.ReadString();
                var description = r.ReadString();
                var category = r.ReadString();
                var hooks = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var h = Count(r); h > 0; h--)
                {
                    hooks[r.ReadString()] = r.ReadInt32();
                }

                var objectives = new ObjectiveInfo[Count(r)];
                for (var o = 0; o < objectives.Length; o++)
                {
                    objectives[o] = new ObjectiveInfo(r.ReadString(), r.ReadString(), r.ReadBoolean(), r.ReadBoolean(), ReadStrings(r), r.ReadInt32());
                }

                var variables = new int[Count(r)];
                for (var v = 0; v < variables.Length; v++)
                {
                    variables[v] = r.ReadInt32();
                }

                missions[i] = new MissionInfo(id, title, description, category, hooks, objectives, variables);
            }

            var script = new CompiledScript
            {
                SourceName = source, Constants = constants, Functions = functions, Globals = globals, InitFunction = init,
                StartFunction = start, EventHandlers = handlers, Missions = missions, NativeNames = natives,
            };
            Verify(script);
            return script;
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Bytecode file is truncated.", ex);
        }
    }

    /// <summary>Checks every index and jump target so the VM never trusts unchecked data.</summary>
    /// <exception cref="InvalidDataException">The script is malformed.</exception>
    public static void Verify(CompiledScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidDataException($"Invalid bytecode in '{script.SourceName}': {message}");
            }
        }

        bool IsFunction(int index) => index >= 0 && index < script.Functions.Length;
        Check(IsFunction(script.InitFunction), "init function index out of range.");
        Check(script.StartFunction == -1 || IsFunction(script.StartFunction), "start function index out of range.");
        Check(script.EventHandlers.All(h => IsFunction(h.Function)), "event handler function out of range.");
        foreach (var mission in script.Missions)
        {
            Check(mission.Hooks.Values.All(IsFunction), $"hook of mission '{mission.Id}' out of range.");
            Check(mission.Objectives.All(o => IsFunction(o.Body)), $"objective body of mission '{mission.Id}' out of range.");
            Check(mission.Variables.All(v => v >= 0 && v < script.Globals.Length), $"variable of mission '{mission.Id}' out of range.");
        }

        foreach (var f in script.Functions)
        {
            Check(f.ParameterCount >= 0 && f.LocalCount >= f.ParameterCount && f.LocalNames.Length == f.LocalCount, $"'{f.Name}' has inconsistent local counts.");
            var pc = 0;
            var starts = new HashSet<int>();
            while (pc < f.Code.Length)
            {
                starts.Add(pc);
                Check(Enum.IsDefined((OpCode)f.Code[pc]), $"'{f.Name}' has unknown opcode {f.Code[pc]} at {pc}.");
                var op = (OpCode)f.Code[pc];
                var operands = OpCodes.OperandCount(op);
                Check(pc + operands < f.Code.Length, $"'{f.Name}' ends inside an instruction.");
                var a = operands > 0 ? f.Code[pc + 1] : 0;
                var b = operands > 1 ? f.Code[pc + 2] : 0;
                switch (op)
                {
                    case OpCode.Constant or OpCode.GetMember or OpCode.SetMember:
                        Check(a >= 0 && a < script.Constants.Length, $"constant index {a} out of range in '{f.Name}'.");
                        break;
                    case OpCode.LoadLocal or OpCode.StoreLocal or OpCode.IterStart:
                        Check(a >= 0 && a < f.LocalCount, $"local slot {a} out of range in '{f.Name}'.");
                        break;
                    case OpCode.IterNext:
                        Check(a >= 0 && a < f.LocalCount, $"local slot {a} out of range in '{f.Name}'.");
                        break;
                    case OpCode.LoadGlobal or OpCode.StoreGlobal:
                        Check(a >= 0 && a < script.Globals.Length, $"global slot {a} out of range in '{f.Name}'.");
                        break;
                    case OpCode.Call or OpCode.Spawn:
                        Check(IsFunction(a) && b == script.Functions[a].ParameterCount, $"bad call target {a}/{b} in '{f.Name}'.");
                        break;
                    case OpCode.CallNative:
                        Check(a >= 0 && a < script.NativeNames.Length && b >= 0, $"native index {a} out of range in '{f.Name}'.");
                        break;
                    case OpCode.MakeList or OpCode.MakeMap:
                        Check(a >= 0, $"negative count in '{f.Name}'.");
                        break;
                }

                pc += 1 + operands;
            }

            // Jumps must land on an instruction boundary (or the end).
            pc = 0;
            while (pc < f.Code.Length)
            {
                var op = (OpCode)f.Code[pc];
                var target = op switch
                {
                    OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfFalseKeep or OpCode.JumpIfTrueKeep or OpCode.TryBegin => f.Code[pc + 1],
                    OpCode.IterNext => f.Code[pc + 2],
                    _ => -2,
                };
                Check(target == -2 || target == f.Code.Length || starts.Contains(target), $"jump to {target} is not an instruction boundary in '{f.Name}'.");
                pc += 1 + OpCodes.OperandCount(op);
            }
        }
    }

    private static int Count(BinaryReader r)
    {
        var count = r.ReadInt32();
        return count is >= 0 and <= 10_000_000 ? count : throw new InvalidDataException($"Invalid table size {count}.");
    }

    private static void WriteStrings(BinaryWriter w, string[] values)
    {
        w.Write(values.Length);
        foreach (var value in values)
        {
            w.Write(value);
        }
    }

    private static string[] ReadStrings(BinaryReader r)
    {
        var values = new string[Count(r)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = r.ReadString();
        }

        return values;
    }
}
