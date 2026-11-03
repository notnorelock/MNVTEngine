using MNVTEngine.Scripting.Bytecode;

namespace MNVTEngine.Scripting.VM;

public sealed partial class ScriptVM
{
    /// <summary>Runs one fiber until it suspends, ends, fails, or exhausts its instruction budget.</summary>
    private void Run(ScriptFiber fiber)
    {
        var budget = Limits.InstructionsPerSlice;
        while (fiber.State == FiberState.Ready)
        {
            if (budget-- <= 0)
            {
                Fault(fiber, $"Execution limit exceeded: {Limits.InstructionsPerSlice} instructions without waiting (infinite loop?).", null);
                return;
            }

            try
            {
                if (!Step(fiber))
                {
                    return;
                }
            }
            catch (ScriptRuntimeException ex)
            {
                Raise(fiber, ScriptValue.FromString(ex.Message), ex.InnerException);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or InvalidProgramException))
            {
                // A host function failed: script code can catch it like any error; uncaught, it faults the fiber with the cause.
                Raise(fiber, ScriptValue.FromString($"Internal error: {ex.Message}"), ex);
            }
        }
    }

    /// <summary>Executes one instruction. Returns false when the fiber must stop running for now.</summary>
    private bool Step(ScriptFiber fiber)
    {
        var frameIndex = fiber.Frames.Count - 1;
        var frame = fiber.Frames[frameIndex];
        var code = Script.Functions[frame.Function].Code;
        var pc = frame.Pc;

        if (pc >= code.Length)
        {
            throw new ScriptRuntimeException("Execution ran past the end of a function.");
        }

        if (Debugger is { } debugger && StatementLine(frame.Function, pc) is { } line && fiber.DebuggerResumePoint != (frameIndex, pc))
        {
            if (debugger.OnStatement(fiber, line) == DebugAction.Pause)
            {
                fiber.DebuggerResumePoint = (frameIndex, pc);
                fiber.State = FiberState.Paused;
                return false;
            }
        }

        fiber.DebuggerResumePoint = null;
        var op = (OpCode)code[pc];
        var a = OpCodes.OperandCount(op) > 0 ? code[pc + 1] : 0;
        var b = OpCodes.OperandCount(op) > 1 ? code[pc + 2] : 0;
        frame.Pc = pc + 1 + OpCodes.OperandCount(op);
        fiber.Frames[frameIndex] = frame;
        var stack = fiber.Stack;

        switch (op)
        {
            case OpCode.Constant: Push(fiber, Script.Constants[a]); break;
            case OpCode.Null: Push(fiber, ScriptValue.Null); break;
            case OpCode.True: Push(fiber, ScriptValue.True); break;
            case OpCode.False: Push(fiber, ScriptValue.False); break;
            case OpCode.Pop: fiber.Sp--; break;
            case OpCode.Dup: Push(fiber, stack[fiber.Sp - 1]); break;
            case OpCode.Dup2:
                var first = stack[fiber.Sp - 2];
                var second = stack[fiber.Sp - 1];
                Push(fiber, first);
                Push(fiber, second);
                break;
            case OpCode.LoadLocal: Push(fiber, fiber.Stack[frame.Base + a]); break;
            case OpCode.StoreLocal: fiber.Stack[frame.Base + a] = Pop(fiber); break;
            case OpCode.LoadGlobal: Push(fiber, Globals[a]); break;
            case OpCode.StoreGlobal: Globals[a] = Pop(fiber); break;

            case OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Modulo:
            case OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual:
                var right = Pop(fiber);
                var left = Pop(fiber);
                Push(fiber, Operators.Binary(op, left, right));
                break;
            case OpCode.Equal: Push(fiber, ScriptValue.FromBool(Pop(fiber) == Pop(fiber))); break;
            case OpCode.NotEqual: Push(fiber, ScriptValue.FromBool(Pop(fiber) != Pop(fiber))); break;
            case OpCode.Negate: Push(fiber, ScriptValue.FromNumber(-Pop(fiber).AsNumber("operand of '-'"))); break;
            case OpCode.Not: Push(fiber, ScriptValue.FromBool(!Pop(fiber).IsTruthy)); break;

            case OpCode.Jump: SetPc(fiber, a); break;
            case OpCode.JumpIfFalse:
                if (!Pop(fiber).IsTruthy)
                {
                    SetPc(fiber, a);
                }

                break;
            case OpCode.JumpIfFalseKeep or OpCode.JumpIfTrueKeep:
                if (stack[fiber.Sp - 1].IsTruthy == (op == OpCode.JumpIfTrueKeep))
                {
                    SetPc(fiber, a);
                }
                else
                {
                    fiber.Sp--;
                }

                break;

            case OpCode.Call: EnterFunction(fiber, a, b); break;
            case OpCode.CallNative:
                var arguments = new ScriptValue[b];
                Array.Copy(fiber.Stack, fiber.Sp - b, arguments, 0, b);
                var result = _natives[a](new NativeCall(this, fiber, Script.NativeNames[a], arguments));
                fiber.Sp -= b;
                if (!fiber.IsAlive)
                {
                    return false; // the host function cancelled this fiber (e.g. mission completed)
                }

                Push(fiber, result);
                break;
            case OpCode.Return:
                var returned = Pop(fiber);
                fiber.Sp = frame.Base;
                fiber.Frames.RemoveAt(frameIndex);
                fiber.Handlers.RemoveAll(h => h.Frame >= frameIndex);
                if (fiber.Frames.Count == 0)
                {
                    fiber.Result = returned;
                    fiber.State = FiberState.Finished;
                    return false;
                }

                Push(fiber, returned);
                break;

            case OpCode.MakeList:
                var list = new List<ScriptValue>(a);
                for (var i = fiber.Sp - a; i < fiber.Sp; i++)
                {
                    list.Add(fiber.Stack[i]);
                }

                fiber.Sp -= a;
                Push(fiber, ScriptValue.FromList(list));
                break;
            case OpCode.MakeMap:
                var map = new Dictionary<string, ScriptValue>(a, StringComparer.Ordinal);
                for (var i = fiber.Sp - (2 * a); i < fiber.Sp; i += 2)
                {
                    map[fiber.Stack[i].AsString("map key")] = fiber.Stack[i + 1];
                }

                fiber.Sp -= 2 * a;
                Push(fiber, ScriptValue.FromMap(map));
                break;
            case OpCode.MakeRange:
                var end = Pop(fiber).AsNumber("range end");
                Push(fiber, ScriptValue.FromRange(Pop(fiber).AsNumber("range start"), end));
                break;
            case OpCode.GetIndex:
                var index = Pop(fiber);
                Push(fiber, Operators.GetIndex(Pop(fiber), index));
                break;
            case OpCode.SetIndex:
                var value = Pop(fiber);
                var key = Pop(fiber);
                Operators.SetIndex(Pop(fiber), key, value);
                break;
            case OpCode.GetMember:
                Push(fiber, Operators.GetMember(Pop(fiber), Script.Constants[a].AsString()));
                break;
            case OpCode.SetMember:
                var memberValue = Pop(fiber);
                Operators.SetMember(Pop(fiber), Script.Constants[a].AsString(), memberValue);
                break;

            case OpCode.Wait:
                var seconds = Pop(fiber).AsNumber("wait time");
                if (seconds <= 0)
                {
                    fiber.WakeTick = TickCount + 1;
                }
                else
                {
                    fiber.WakeTime = Time + seconds;
                    fiber.State = FiberState.Sleeping;
                }

                return false;
            case OpCode.Yield:
                fiber.WakeTick = TickCount + 1;
                return false;
            case OpCode.WaitEvent:
                fiber.WaitingEvent = Pop(fiber).AsString("event name");
                fiber.State = FiberState.WaitingForEvent;
                return false;
            case OpCode.Spawn:
                var spawnArguments = new ScriptValue[b];
                Array.Copy(fiber.Stack, fiber.Sp - b, spawnArguments, 0, b);
                fiber.Sp -= b;
                if (_fibers.Count(f => f.IsAlive) >= Limits.MaxFibers)
                {
                    throw new ScriptRuntimeException($"Coroutine limit of {Limits.MaxFibers} reached.");
                }

                CreateFiber(a, spawnArguments, fiber.Context);
                break;

            case OpCode.IterStart:
                fiber.Stack[frame.Base + a] = ScriptValue.FromOpaque(Iterator.Create(Pop(fiber)));
                break;
            case OpCode.IterNext:
                var iterator = (Iterator)fiber.Stack[frame.Base + a].AsOpaque()!;
                if (iterator.MoveNext(out var item))
                {
                    Push(fiber, item);
                }
                else
                {
                    SetPc(fiber, b);
                }

                break;

            case OpCode.TryBegin:
                fiber.Handlers.Add(new ScriptFiber.TryHandler(frameIndex, a, fiber.Sp));
                break;
            case OpCode.TryEnd:
                fiber.Handlers.RemoveAt(fiber.Handlers.Count - 1);
                break;
            case OpCode.Throw:
                Raise(fiber, Pop(fiber), null);
                break;
            default:
                throw new ScriptRuntimeException($"Invalid opcode {op}.");
        }

        return true;
    }

    private static void SetPc(ScriptFiber fiber, int pc)
    {
        var frame = fiber.Frames[^1];
        frame.Pc = pc;
        fiber.Frames[^1] = frame;
    }

    private sealed class Iterator
    {
        private readonly List<ScriptValue>? _items;
        private readonly ScriptRange? _range;
        private double _next;
        private int _index;

        private Iterator(List<ScriptValue>? items, ScriptRange? range)
        {
            _items = items;
            _range = range;
            _next = range?.Start ?? 0;
        }

        public static Iterator Create(ScriptValue iterable) => iterable.Kind switch
        {
            ValueKind.List => new Iterator(iterable.AsList(), null),
            ValueKind.Map => new Iterator([.. iterable.AsMap().Keys.Select(ScriptValue.FromString)], null),
            ValueKind.Range => new Iterator(null, iterable.AsRange()),
            _ => throw new ScriptRuntimeException($"Cannot loop over a {iterable.TypeName}; use a list, map or range."),
        };

        public bool MoveNext(out ScriptValue value)
        {
            if (_range is { } range)
            {
                value = ScriptValue.FromNumber(_next);
                _next++;
                return value.AsNumber() < range.End;
            }

            // Lists are iterated live: items appended during the loop are visited too.
            var hasNext = _index < _items!.Count;
            value = hasNext ? _items[_index++] : ScriptValue.Null;
            return hasNext;
        }
    }
}

internal static class Operators
{
    public static ScriptValue Binary(OpCode op, ScriptValue left, ScriptValue right)
    {
        if (op == OpCode.Add && (left.Kind == ValueKind.String || right.Kind == ValueKind.String))
        {
            return ScriptValue.FromString(left.ToString() + right.ToString());
        }

        if (op is OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual && left.Kind == ValueKind.String && right.Kind == ValueKind.String)
        {
            var comparison = string.CompareOrdinal(left.AsString(), right.AsString());
            return ScriptValue.FromBool(Compare(op, comparison, 0));
        }

        if (left.Kind != ValueKind.Number || right.Kind != ValueKind.Number)
        {
            throw new ScriptRuntimeException($"Cannot apply {Symbol(op)} to {left.TypeName} and {right.TypeName}.");
        }

        double x = left.AsNumber(), y = right.AsNumber();
        return op switch
        {
            OpCode.Add => ScriptValue.FromNumber(x + y),
            OpCode.Subtract => ScriptValue.FromNumber(x - y),
            OpCode.Multiply => ScriptValue.FromNumber(x * y),
            OpCode.Divide => y == 0 ? throw new ScriptRuntimeException("Division by zero.") : ScriptValue.FromNumber(x / y),
            OpCode.Modulo => y == 0 ? throw new ScriptRuntimeException("Division by zero.") : ScriptValue.FromNumber(x % y),
            _ => ScriptValue.FromBool(Compare(op, x, y)),
        };
    }

    public static ScriptValue GetIndex(ScriptValue target, ScriptValue index) => target.Kind switch
    {
        ValueKind.List => target.AsList()[ListIndex(target.AsList(), index)],
        ValueKind.Map => target.AsMap().GetValueOrDefault(index.AsString("map key")),
        ValueKind.String => ScriptValue.FromString(target.AsString()[StringIndex(target.AsString(), index)].ToString()),
        _ => throw new ScriptRuntimeException($"Cannot index a {target.TypeName}."),
    };

    public static void SetIndex(ScriptValue target, ScriptValue index, ScriptValue value)
    {
        switch (target.Kind)
        {
            case ValueKind.List:
                target.AsList()[ListIndex(target.AsList(), index)] = value;
                break;
            case ValueKind.Map:
                target.AsMap()[index.AsString("map key")] = value;
                break;
            default:
                throw new ScriptRuntimeException($"Cannot assign an element of a {target.TypeName}.");
        }
    }

    public static ScriptValue GetMember(ScriptValue target, string member) =>
        target.Kind == ValueKind.Map
            ? target.AsMap().GetValueOrDefault(member)
            : throw new ScriptRuntimeException($"Cannot read '.{member}' of a {target.TypeName}.");

    public static void SetMember(ScriptValue target, string member, ScriptValue value)
    {
        if (target.Kind != ValueKind.Map)
        {
            throw new ScriptRuntimeException($"Cannot set '.{member}' on a {target.TypeName}.");
        }

        target.AsMap()[member] = value;
    }

    private static int ListIndex(List<ScriptValue> list, ScriptValue index)
    {
        var number = index.AsNumber("list index");
        if (number != Math.Floor(number) || number < 0 || number >= list.Count)
        {
            throw new ScriptRuntimeException($"List index {index} is out of range (count {list.Count}).");
        }

        return (int)number;
    }

    private static int StringIndex(string text, ScriptValue index)
    {
        var number = index.AsNumber("string index");
        return number == Math.Floor(number) && number >= 0 && number < text.Length
            ? (int)number
            : throw new ScriptRuntimeException($"String index {index} is out of range (length {text.Length}).");
    }

    private static bool Compare(OpCode op, double x, double y) => op switch
    {
        OpCode.Less => x < y,
        OpCode.LessEqual => x <= y,
        OpCode.Greater => x > y,
        _ => x >= y,
    };

    private static string Symbol(OpCode op) => op switch
    {
        OpCode.Add => "'+'",
        OpCode.Subtract => "'-'",
        OpCode.Multiply => "'*'",
        OpCode.Divide => "'/'",
        OpCode.Modulo => "'%'",
        OpCode.Less => "'<'",
        OpCode.LessEqual => "'<='",
        OpCode.Greater => "'>'",
        _ => "'>='",
    };
}
