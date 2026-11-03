using MNVTEngine.Scripting.Bytecode;
using MNVTEngine.Scripting.Syntax;
using MNVTEngine.Scripting.VM;

namespace MNVTEngine.Scripting.Compiler;

internal sealed partial class CodeGenerator
{
    private void EmitBlock(FunctionBuilder f, BlockStatement block)
    {
        f.Scopes.Push(new Dictionary<string, (int, bool)>(StringComparer.Ordinal));
        foreach (var statement in block.Statements)
        {
            EmitStatement(f, statement);
        }

        f.Scopes.Pop();
    }

    private void EmitStatement(FunctionBuilder f, Statement statement)
    {
        f.MarkLine(statement.Location.Line);
        switch (statement)
        {
            case BlockStatement block:
                EmitBlock(f, block);
                break;
            case VarStatement v:
                if (f.Scopes.Peek().ContainsKey(v.Name))
                {
                    diagnostics.Error("MNV3002", $"'{v.Name}' is already declared in this block.", v.Location);
                }

                EmitExpression(f, v.Initializer); // before declaring, so 'var x = x' reads an outer x
                f.Op(OpCode.StoreLocal, f.Declare(v.Name, v.IsConst));
                break;
            case AssignStatement a:
                EmitAssignment(f, a);
                break;
            case IfStatement i:
                EmitExpression(f, i.Condition);
                var toElse = f.Jump(OpCode.JumpIfFalse);
                EmitBlock(f, i.Then);
                if (i.Else is null)
                {
                    f.Patch(toElse);
                }
                else
                {
                    var toEnd = f.Jump(OpCode.Jump);
                    f.Patch(toElse);
                    EmitStatement(f, i.Else);
                    f.Patch(toEnd);
                }

                break;
            case WhileStatement w:
                var loopStart = f.Position;
                EmitExpression(f, w.Condition);
                var exit = f.Jump(OpCode.JumpIfFalse);
                EmitLoopBody(f, w.Body, loopStart);
                f.Op(OpCode.Jump, loopStart);
                f.Patch(exit);
                PatchBreaks(f);
                break;
            case ForStatement loop:
                EmitFor(f, loop);
                break;
            case BreakStatement or ContinueStatement:
                EmitLoopExit(f, statement);
                break;
            case ReturnStatement r:
                if (r.Value is null)
                {
                    f.Op(OpCode.Null);
                }
                else
                {
                    EmitExpression(f, r.Value);
                }

                f.Op(OpCode.Return);
                break;
            case ExpressionStatement e:
                EmitExpression(f, e.Expression);
                f.Op(OpCode.Pop);
                break;
            case WaitSecondsStatement wait:
                if (wait.Seconds is LiteralExpression { Value: double seconds } && seconds < 0
                    || wait.Seconds is UnaryExpression { Operator: TokenKind.Minus, Operand: LiteralExpression { Value: double magnitude } } && magnitude > 0)
                {
                    diagnostics.Error("MNV3022", "Wait time cannot be negative.", wait.Location);
                }

                EmitExpression(f, wait.Seconds);
                f.Op(OpCode.Wait);
                break;
            case WaitUntilStatement until:
                // L: cond; if false -> yield and retry next tick
                var check = f.Position;
                EmitExpression(f, until.Condition);
                var notYet = f.Jump(OpCode.JumpIfFalse);
                var done = f.Jump(OpCode.Jump);
                f.Patch(notYet);
                f.Op(OpCode.Yield);
                f.Op(OpCode.Jump, check);
                f.Patch(done);
                break;
            case WaitEventStatement waitEvent:
                EmitExpression(f, waitEvent.EventName);
                f.Op(OpCode.WaitEvent);
                if (waitEvent.Binding is null)
                {
                    f.Op(OpCode.Pop);
                }
                else
                {
                    f.Op(OpCode.StoreLocal, f.Declare(waitEvent.Binding, isConst: false));
                }

                break;
            case TryStatement t:
                EmitTry(f, t);
                break;
            case ThrowStatement th:
                EmitExpression(f, th.Value);
                f.Op(OpCode.Throw);
                break;
            case SpawnStatement s:
                EmitSpawn(f, s);
                break;
            case TimerStatement timer:
                EmitTimer(f, timer);
                break;
        }
    }

    private void EmitAssignment(FunctionBuilder f, AssignStatement a)
    {
        var compound = a.Operator switch
        {
            TokenKind.PlusAssign => OpCode.Add,
            TokenKind.MinusAssign => OpCode.Subtract,
            TokenKind.StarAssign => OpCode.Multiply,
            TokenKind.SlashAssign => OpCode.Divide,
            _ => (OpCode?)null,
        };

        switch (a.Target)
        {
            case NameExpression name:
                var (load, store, slot) = ResolveVariable(f, name, forWrite: true);
                if (slot < 0)
                {
                    return;
                }

                if (compound is { } op)
                {
                    f.Op(load, slot);
                    EmitExpression(f, a.Value);
                    f.Op(op);
                }
                else
                {
                    EmitExpression(f, a.Value);
                }

                f.Op(store, slot);
                break;
            case MemberExpression member:
                EmitExpression(f, member.Target);
                var key = Constant(ScriptValue.FromString(member.Member));
                if (compound is { } memberOp)
                {
                    f.Op(OpCode.Dup);
                    f.Op(OpCode.GetMember, key);
                    EmitExpression(f, a.Value);
                    f.Op(memberOp);
                }
                else
                {
                    EmitExpression(f, a.Value);
                }

                f.Op(OpCode.SetMember, key);
                break;
            case IndexExpression index:
                EmitExpression(f, index.Target);
                EmitExpression(f, index.Index);
                if (compound is { } indexOp)
                {
                    f.Op(OpCode.Dup2);
                    f.Op(OpCode.GetIndex);
                    EmitExpression(f, a.Value);
                    f.Op(indexOp);
                }
                else
                {
                    EmitExpression(f, a.Value);
                }

                f.Op(OpCode.SetIndex);
                break;
        }
    }

    private void EmitFor(FunctionBuilder f, ForStatement loop)
    {
        f.Scopes.Push(new Dictionary<string, (int, bool)>(StringComparer.Ordinal));
        var iterator = f.Hidden("iterator");
        EmitExpression(f, loop.Iterable);
        f.Op(OpCode.IterStart, iterator);
        var variable = f.Declare(loop.Variable, isConst: false);
        var start = f.Position;
        f.Op(OpCode.IterNext, iterator, -1);
        var exitOperand = f.Position - 1;
        f.Op(OpCode.StoreLocal, variable);
        EmitLoopBody(f, loop.Body, start);
        f.Op(OpCode.Jump, start);
        f.Patch(exitOperand);
        PatchBreaks(f);
        f.Scopes.Pop();
    }

    private void EmitLoopBody(FunctionBuilder f, BlockStatement body, int continueTarget)
    {
        f.Loops.Push(new LoopContext(continueTarget, f.TryDepth));
        EmitBlock(f, body);
    }

    private static void PatchBreaks(FunctionBuilder f)
    {
        foreach (var jump in f.Loops.Pop().Breaks)
        {
            f.Patch(jump);
        }
    }

    private void EmitLoopExit(FunctionBuilder f, Statement statement)
    {
        if (f.Loops.Count == 0)
        {
            diagnostics.Error("MNV3016", $"'{(statement is BreakStatement ? "break" : "continue")}' is only allowed inside a loop.", statement.Location);
            return;
        }

        var loop = f.Loops.Peek();
        for (var i = loop.TryDepth; i < f.TryDepth; i++)
        {
            f.Op(OpCode.TryEnd); // leaving try blocks inside the loop
        }

        if (statement is BreakStatement)
        {
            loop.Breaks.Add(f.Jump(OpCode.Jump));
        }
        else
        {
            f.Op(OpCode.Jump, loop.ContinueTarget);
        }
    }

    private void EmitTry(FunctionBuilder f, TryStatement t)
    {
        var handler = f.Jump(OpCode.TryBegin);
        f.TryDepth++;
        EmitBlock(f, t.Body);
        f.TryDepth--;
        f.Op(OpCode.TryEnd);
        var end = f.Jump(OpCode.Jump);
        f.Patch(handler);
        f.Scopes.Push(new Dictionary<string, (int, bool)>(StringComparer.Ordinal));
        if (t.CatchVariable is null)
        {
            f.Op(OpCode.Pop);
        }
        else
        {
            f.Op(OpCode.StoreLocal, f.Declare(t.CatchVariable, isConst: false));
        }

        EmitBlock(f, t.Handler);
        f.Scopes.Pop();
        f.Patch(end);
    }

    private void EmitSpawn(FunctionBuilder f, SpawnStatement s)
    {
        if (s.Call.Callee is not NameExpression name || ResolveFunction(f, name.Name) is not { } target)
        {
            diagnostics.Error("MNV3017", "'spawn' needs a script function declared with 'func'.", s.Location);
            return;
        }

        if (!CheckArity(target, s.Call))
        {
            return;
        }

        foreach (var argument in s.Call.Arguments)
        {
            EmitExpression(f, argument);
        }

        f.Op(OpCode.Spawn, target, s.Call.Arguments.Count);
    }

    /// <summary>
    /// <c>after s { body }</c> / <c>every s { body }</c>: the body becomes its own function, and a small driver function
    /// (wait, call, repeat) is spawned with the interval evaluated now.
    /// </summary>
    private void EmitTimer(FunctionBuilder f, TimerStatement timer)
    {
        var bodyIndex = NewFunction($"{f.Name}: {(timer.Repeat ? "every" : "after")} (line {timer.Location.Line})", [], f.Mission, timer.Body, f.VisibleLocals());
        Emit(_functions[bodyIndex]);

        var driverIndex = NewFunction($"{f.Name}: timer driver (line {timer.Location.Line})", ["$interval"], f.Mission, body: null);
        int[] code =
        [
            (int)OpCode.LoadLocal, 0,
            (int)OpCode.Wait,
            (int)OpCode.Call, bodyIndex, 0,
            (int)OpCode.Pop,
            .. timer.Repeat ? new[] { (int)OpCode.Jump, 0 } : [],
            (int)OpCode.Null,
            (int)OpCode.Return,
        ];
        _functions[driverIndex].PrebuiltCode = code;

        EmitExpression(f, timer.Seconds);
        f.Op(OpCode.Spawn, driverIndex, 1);
    }
}
