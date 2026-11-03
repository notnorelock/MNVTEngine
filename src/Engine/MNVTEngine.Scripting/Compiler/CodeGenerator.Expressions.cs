using MNVTEngine.Scripting.Bytecode;
using MNVTEngine.Scripting.Syntax;
using MNVTEngine.Scripting.VM;

namespace MNVTEngine.Scripting.Compiler;

internal sealed partial class CodeGenerator
{
    private void EmitExpression(FunctionBuilder f, Expression expression)
    {
        switch (expression)
        {
            case LiteralExpression { Value: null }:
                f.Op(OpCode.Null);
                break;
            case LiteralExpression { Value: bool b }:
                f.Op(b ? OpCode.True : OpCode.False);
                break;
            case LiteralExpression { Value: double d }:
                f.Op(OpCode.Constant, Constant(ScriptValue.FromNumber(d)));
                break;
            case LiteralExpression { Value: string s }:
                f.Op(OpCode.Constant, Constant(ScriptValue.FromString(s)));
                break;
            case NameExpression name:
                var (load, _, slot) = ResolveVariable(f, name, forWrite: false);
                if (slot >= 0)
                {
                    f.Op(load, slot);
                }

                break;
            case ListExpression list:
                foreach (var item in list.Items)
                {
                    EmitExpression(f, item);
                }

                f.Op(OpCode.MakeList, list.Items.Count);
                break;
            case MapExpression map:
                foreach (var (key, value) in map.Entries)
                {
                    f.Op(OpCode.Constant, Constant(ScriptValue.FromString(key)));
                    EmitExpression(f, value);
                }

                f.Op(OpCode.MakeMap, map.Entries.Count);
                break;
            case MemberExpression member:
                if (member.Target is NameExpression module && IsModuleName(f, module.Name))
                {
                    diagnostics.Error("MNV3018", $"'{module.Name}.{member.Member}' is a function; call it with parentheses.", member.Location);
                    return;
                }

                EmitExpression(f, member.Target);
                f.Op(OpCode.GetMember, Constant(ScriptValue.FromString(member.Member)));
                break;
            case IndexExpression index:
                EmitExpression(f, index.Target);
                EmitExpression(f, index.Index);
                f.Op(OpCode.GetIndex);
                break;
            case CallExpression call:
                EmitCall(f, call);
                break;
            case UnaryExpression unary:
                if (unary.Operator == TokenKind.Minus && unary.Operand is LiteralExpression { Value: not double })
                {
                    diagnostics.Error("MNV3020", "Only numbers can be negated.", unary.Location);
                }

                EmitExpression(f, unary.Operand);
                f.Op(unary.Operator == TokenKind.Minus ? OpCode.Negate : OpCode.Not);
                break;
            case BinaryExpression { Operator: TokenKind.And or TokenKind.Or } logical:
                EmitExpression(f, logical.Left);
                var shortCircuit = f.Jump(logical.Operator == TokenKind.And ? OpCode.JumpIfFalseKeep : OpCode.JumpIfTrueKeep);
                EmitExpression(f, logical.Right);
                f.Patch(shortCircuit);
                break;
            case BinaryExpression binary:
                CheckOperandTypes(binary);
                EmitExpression(f, binary.Left);
                EmitExpression(f, binary.Right);
                f.Op(binary.Operator switch
                {
                    TokenKind.Plus => OpCode.Add,
                    TokenKind.Minus => OpCode.Subtract,
                    TokenKind.Star => OpCode.Multiply,
                    TokenKind.Slash => OpCode.Divide,
                    TokenKind.Percent => OpCode.Modulo,
                    TokenKind.Equal => OpCode.Equal,
                    TokenKind.NotEqual => OpCode.NotEqual,
                    TokenKind.Less => OpCode.Less,
                    TokenKind.LessEqual => OpCode.LessEqual,
                    TokenKind.Greater => OpCode.Greater,
                    _ => OpCode.GreaterEqual,
                });
                break;
            case RangeExpression range:
                EmitExpression(f, range.Start);
                EmitExpression(f, range.End);
                f.Op(OpCode.MakeRange);
                break;
        }
    }

    /// <summary>Static type check where both sides are literals, e.g. <c>"a" - 1</c>.</summary>
    private void CheckOperandTypes(BinaryExpression binary)
    {
        if (binary.Operator is TokenKind.Equal or TokenKind.NotEqual || binary.Left is not LiteralExpression left || binary.Right is not LiteralExpression right)
        {
            return;
        }

        var bothNumbers = left.Value is double && right.Value is double;
        var valid = binary.Operator switch
        {
            TokenKind.Plus => bothNumbers || left.Value is string || right.Value is string,
            TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual => bothNumbers || (left.Value is string && right.Value is string),
            _ => bothNumbers,
        };

        if (!valid)
        {
            diagnostics.Error("MNV3020", $"Operator cannot be applied to {TypeName(left.Value)} and {TypeName(right.Value)}.", binary.Location);
        }

        static string TypeName(object? value) => value switch
        {
            null => "null",
            bool => "bool",
            double => "number",
            _ => "string",
        };
    }

    private void EmitCall(FunctionBuilder f, CallExpression call)
    {
        string? nativeName = null;
        switch (call.Callee)
        {
            case NameExpression name when ResolveFunction(f, name.Name) is { } function:
                if (!CheckArity(function, call))
                {
                    return;
                }

                foreach (var argument in call.Arguments)
                {
                    EmitExpression(f, argument);
                }

                f.Op(OpCode.Call, function, call.Arguments.Count);
                return;
            case NameExpression name when IsVariable(f, name.Name):
                diagnostics.Error("MNV3012", $"'{name.Name}' is a variable, not a function.", name.Location);
                return;
            case NameExpression name:
                nativeName = name.Name;
                break;
            case MemberExpression { Target: NameExpression module } member when !IsVariable(f, module.Name) && ResolveFunction(f, module.Name) is null:
                nativeName = $"{module.Name}.{member.Member}";
                break;
            default:
                diagnostics.Error("MNV3013", "Only named functions can be called (e.g. f(x) or module.f(x)).", call.Location);
                return;
        }

        if (api.TryGet(nativeName, out var signature))
        {
            if (call.Arguments.Count < signature.MinArgs || call.Arguments.Count > signature.MaxArgs)
            {
                var expected = signature.MinArgs == signature.MaxArgs ? $"{signature.MinArgs}" : $"{signature.MinArgs} to {signature.MaxArgs}";
                diagnostics.Error("MNV3019", $"'{nativeName}' takes {expected} argument(s), got {call.Arguments.Count}.", call.Location);
                return;
            }
        }
        else if (allowUnknownFunctions && nativeName.Contains('.', StringComparison.Ordinal))
        {
            diagnostics.Warning("MNV3015", $"'{nativeName}' is not a known API function; the game must provide it at runtime.", call.Location);
        }
        else
        {
            diagnostics.Error("MNV3014", $"Unknown function '{nativeName}'.", call.Location);
            return;
        }

        foreach (var argument in call.Arguments)
        {
            EmitExpression(f, argument);
        }

        f.Op(OpCode.CallNative, Native(nativeName), call.Arguments.Count);
    }

    private bool CheckArity(int function, CallExpression call)
    {
        var target = _functions[function];
        if (call.Arguments.Count == target.ParameterCount)
        {
            return true;
        }

        diagnostics.Error("MNV3019", $"'{target.Name}' takes {target.ParameterCount} argument(s), got {call.Arguments.Count}.", call.Location);
        return false;
    }

    private int? ResolveFunction(FunctionBuilder f, string name)
    {
        if (f.Mission is { } mission && _missionFunctions[mission].TryGetValue(name, out var missionFunction))
        {
            return missionFunction;
        }

        return _topFunctions.TryGetValue(name, out var function) ? function : null;
    }

    private bool IsVariable(FunctionBuilder f, string name) =>
        f.TryResolve(name, out _, out _) || (f.Mission is { } m && _missionGlobals[m].ContainsKey(name)) || _topGlobals.ContainsKey(name);

    private bool IsModuleName(FunctionBuilder f, string name) => !IsVariable(f, name) && (api.IsModule(name) || name is "objective" or "mission");

    private (OpCode Load, OpCode Store, int Slot) ResolveVariable(FunctionBuilder f, NameExpression name, bool forWrite)
    {
        bool isConst;
        var resolved = (Load: OpCode.LoadLocal, Store: OpCode.StoreLocal, Slot: -1);
        if (f.TryResolve(name.Name, out var local, out isConst))
        {
            resolved = (OpCode.LoadLocal, OpCode.StoreLocal, local);
        }
        else if (f.Mission is { } mission && _missionGlobals[mission].TryGetValue(name.Name, out var missionSlot))
        {
            resolved = (OpCode.LoadGlobal, OpCode.StoreGlobal, missionSlot);
            isConst = _globals[missionSlot].IsConst;
        }
        else if (_topGlobals.TryGetValue(name.Name, out var global))
        {
            resolved = (OpCode.LoadGlobal, OpCode.StoreGlobal, global);
            isConst = _globals[global].IsConst;
        }
        else
        {
            var message = f.EnclosingLocals.Contains(name.Name)
                ? $"'{name.Name}' is a local variable of the enclosing code; 'after'/'every' blocks run later and can only use globals or mission variables."
                : ResolveFunction(f, name.Name) is not null
                    ? $"'{name.Name}' is a function; call it, or start it with 'spawn'."
                    : IsModuleName(f, name.Name) ? $"'{name.Name}' is an API module; call one of its functions, e.g. {name.Name}.something()." : $"Undefined name '{name.Name}'.";
            diagnostics.Error(f.EnclosingLocals.Contains(name.Name) ? "MNV3010" : "MNV3011", message, name.Location);
            return resolved;
        }

        if (forWrite && isConst)
        {
            diagnostics.Error("MNV3004", $"Cannot assign to constant '{name.Name}'.", name.Location);
            resolved.Slot = -1;
        }

        return resolved;
    }
}
