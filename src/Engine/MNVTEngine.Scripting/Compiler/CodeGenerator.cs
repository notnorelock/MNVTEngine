using MNVTEngine.Scripting.Bytecode;
using MNVTEngine.Scripting.Syntax;
using MNVTEngine.Scripting.VM;

namespace MNVTEngine.Scripting.Compiler;

/// <summary>
/// Two passes: declare every global, function, handler, mission and objective (so order of declarations does not
/// matter), then generate code for each function body. Name resolution order inside a body: locals (innermost block
/// first), the enclosing mission's variables and functions, then top-level globals and functions, then host functions.
/// </summary>
internal sealed partial class CodeGenerator(string sourceName, ScriptApi api, bool allowUnknownFunctions, DiagnosticBag diagnostics)
{
    private readonly List<FunctionBuilder> _functions = [];
    private readonly Dictionary<string, int> _topFunctions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _missionFunctions = new(StringComparer.Ordinal);
    private readonly List<GlobalInfo> _globals = [];
    private readonly Dictionary<string, int> _topGlobals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _missionGlobals = new(StringComparer.Ordinal);
    private readonly List<(int Slot, Expression Initializer, string? Mission)> _initializers = [];
    private readonly List<ScriptValue> _constants = [];
    private readonly Dictionary<(ValueKind, object?), int> _constantIndex = [];
    private readonly List<string> _natives = [];
    private readonly List<EventHandlerInfo> _handlers = [];
    private readonly List<MissionInfo> _missions = [];
    private int _startFunction = -1;

    public CompiledScript Generate(CompilationUnit unit)
    {
        var init = NewFunction("<init>", [], null, body: null);
        foreach (var declaration in unit.Declarations)
        {
            Declare(declaration);
        }

        foreach (var function in _functions.ToList())
        {
            if (function.PrebuiltCode is null && function.Body is not null)
            {
                Emit(function);
            }
        }

        EmitInit(_functions[init]);
        return new CompiledScript
        {
            SourceName = sourceName,
            Constants = [.. _constants],
            Functions = [.. _functions.Select(f => f.Build())],
            Globals = [.. _globals],
            InitFunction = init,
            StartFunction = _startFunction,
            EventHandlers = [.. _handlers],
            Missions = [.. _missions],
            NativeNames = [.. _natives],
        };
    }

    // ---- Pass 1: declarations ----

    private void Declare(Declaration declaration)
    {
        switch (declaration)
        {
            case VarDeclaration v:
                DeclareGlobal(v, _topGlobals, null, v.Name);
                break;
            case FunctionDeclaration f:
                DeclareFunction(f, _topFunctions, null, f.Name);
                break;
            case HandlerDeclaration { Hook: "start" } h:
                if (_startFunction >= 0)
                {
                    diagnostics.Error("MNV3001", "Only one top-level 'on start' handler is allowed.", h.Location);
                }

                _startFunction = NewFunction("on start", [], null, h.Body);
                break;
            case HandlerDeclaration { Hook: "event" } h:
                _handlers.Add(new EventHandlerInfo(h.EventName!, NewFunction($"on event \"{h.EventName}\"", h.Parameter is null ? [] : [h.Parameter], null, h.Body), null));
                break;
            case HandlerDeclaration h:
                diagnostics.Error("MNV3005", $"'on {h.Hook}' is only valid inside a mission.", h.Location);
                break;
            case MissionDeclaration m:
                DeclareMission(m);
                break;
        }
    }

    private void DeclareGlobal(VarDeclaration v, Dictionary<string, int> scope, string? mission, string displayName)
    {
        if (scope.ContainsKey(v.Name))
        {
            diagnostics.Error("MNV3002", $"'{displayName}' is already declared.", v.Location);
            return;
        }

        var slot = _globals.Count;
        _globals.Add(new GlobalInfo(mission is null ? v.Name : $"{mission}.{v.Name}", v.IsConst));
        scope[v.Name] = slot;
        if (v.Initializer is not null)
        {
            _initializers.Add((slot, v.Initializer, mission));
        }
    }

    private void DeclareFunction(FunctionDeclaration f, Dictionary<string, int> scope, string? mission, string displayName)
    {
        if (scope.ContainsKey(f.Name))
        {
            diagnostics.Error("MNV3002", $"Function '{displayName}' is already declared.", f.Location);
            return;
        }

        if (f.Parameters.Distinct(StringComparer.Ordinal).Count() != f.Parameters.Count)
        {
            diagnostics.Error("MNV3003", $"Function '{displayName}' has duplicate parameter names.", f.Location);
        }

        scope[f.Name] = NewFunction(mission is null ? f.Name : $"{mission}.{f.Name}", f.Parameters, mission, f.Body);
    }

    private void DeclareMission(MissionDeclaration m)
    {
        if (_missions.Any(x => x.Id == m.Id) || _missionGlobals.ContainsKey(m.Id))
        {
            diagnostics.Error("MNV3006", $"Mission '{m.Id}' is declared twice.", m.Location);
            return;
        }

        var globals = new Dictionary<string, int>(StringComparer.Ordinal);
        var functions = new Dictionary<string, int>(StringComparer.Ordinal);
        _missionGlobals[m.Id] = globals;
        _missionFunctions[m.Id] = functions;
        var hooks = new Dictionary<string, int>(StringComparer.Ordinal);
        var objectives = new List<ObjectiveInfo>();
        var objectiveNodes = new List<ObjectiveDeclaration>();

        foreach (var member in m.Members)
        {
            switch (member)
            {
                case VarDeclaration v:
                    DeclareGlobal(v, globals, m.Id, $"{m.Id}.{v.Name}");
                    break;
                case FunctionDeclaration f:
                    DeclareFunction(f, functions, m.Id, $"{m.Id}.{f.Name}");
                    break;
                case HandlerDeclaration { Hook: "event" } h:
                    _handlers.Add(new EventHandlerInfo(h.EventName!, NewFunction($"{m.Id}: on event \"{h.EventName}\"", h.Parameter is null ? [] : [h.Parameter], m.Id, h.Body), m.Id));
                    break;
                case HandlerDeclaration h when hooks.ContainsKey(h.Hook):
                    diagnostics.Error("MNV3001", $"Mission '{m.Id}' has more than one 'on {h.Hook}'.", h.Location);
                    break;
                case HandlerDeclaration h:
                    hooks[h.Hook] = NewFunction($"{m.Id}: on {h.Hook}", [], m.Id, h.Body);
                    break;
                case ObjectiveDeclaration o when objectiveNodes.Any(x => x.Id == o.Id):
                    diagnostics.Error("MNV3007", $"Objective '{o.Id}' is declared twice in mission '{m.Id}'.", o.Location);
                    break;
                case ObjectiveDeclaration o:
                    objectiveNodes.Add(o);
                    objectives.Add(new ObjectiveInfo(
                        o.Id,
                        ConstantString(o.Metadata, "description", ""),
                        ConstantBool(o.Metadata, "optional"),
                        ConstantBool(o.Metadata, "hidden"),
                        [.. o.Requires],
                        NewFunction($"{m.Id}/{o.Id}", [], m.Id, o.Body)));
                    break;
            }
        }

        ValidateRequirements(m, objectiveNodes);
        _missions.Add(new MissionInfo(
            m.Id,
            ConstantString(m.Metadata, "name", m.Id),
            ConstantString(m.Metadata, "description", ""),
            ConstantString(m.Metadata, "category", ""),
            hooks,
            [.. objectives],
            [.. globals.Values]));
    }

    private void ValidateRequirements(MissionDeclaration mission, List<ObjectiveDeclaration> objectives)
    {
        var ids = objectives.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var objective in objectives)
        {
            foreach (var required in objective.Requires.Where(r => !ids.Contains(r)))
            {
                diagnostics.Error("MNV3008", $"Objective '{objective.Id}' requires unknown objective '{required}'.", objective.Location);
            }
        }

        // Dependency cycles would leave objectives that can never activate.
        var graph = objectives.ToDictionary(o => o.Id, o => o.Requires.Where(ids.Contains).ToList(), StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        bool Visit(string id)
        {
            if (state.GetValueOrDefault(id) == 1)
            {
                return true;
            }

            if (state.GetValueOrDefault(id) == 2)
            {
                return false;
            }

            state[id] = 1;
            var cycle = graph[id].Any(Visit);
            state[id] = 2;
            return cycle;
        }

        foreach (var objective in objectives)
        {
            if (state.GetValueOrDefault(objective.Id) == 0 && Visit(objective.Id))
            {
                diagnostics.Error("MNV3009", $"Objectives in mission '{mission.Id}' have a circular 'requires' dependency involving '{objective.Id}'.", objective.Location);
                break;
            }
        }
    }

    private string ConstantString(IReadOnlyDictionary<string, Expression> metadata, string key, string fallback)
    {
        if (!metadata.TryGetValue(key, out var expression))
        {
            return fallback;
        }

        if (expression is LiteralExpression { Value: string text })
        {
            return text;
        }

        diagnostics.Error("MNV3021", $"'{key}' must be a string literal.", expression.Location);
        return fallback;
    }

    private bool ConstantBool(IReadOnlyDictionary<string, Expression> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var expression))
        {
            return false;
        }

        if (expression is LiteralExpression { Value: bool value })
        {
            return value;
        }

        diagnostics.Error("MNV3021", $"'{key}' must be true or false.", expression.Location);
        return false;
    }

    // ---- Pass 2: code ----

    private int NewFunction(string name, IReadOnlyList<string> parameters, string? mission, BlockStatement? body, HashSet<string>? enclosingLocals = null)
    {
        _functions.Add(new FunctionBuilder(name, parameters, mission, body, enclosingLocals));
        return _functions.Count - 1;
    }

    private void Emit(FunctionBuilder function)
    {
        foreach (var statement in function.Body!.Statements)
        {
            EmitStatement(function, statement);
        }

        function.Op(OpCode.Null);
        function.Op(OpCode.Return);
    }

    private void EmitInit(FunctionBuilder init)
    {
        foreach (var (slot, initializer, mission) in _initializers)
        {
            init.Mission = mission;
            init.MarkLine(initializer.Location.Line);
            EmitExpression(init, initializer);
            init.Op(OpCode.StoreGlobal, slot);
        }

        init.Mission = null;
        init.Op(OpCode.Null);
        init.Op(OpCode.Return);
    }

    private int Constant(ScriptValue value)
    {
        var key = (value.Kind, value.ToObject());
        if (!_constantIndex.TryGetValue(key, out var index))
        {
            index = _constants.Count;
            _constants.Add(value);
            _constantIndex[key] = index;
        }

        return index;
    }

    private int Native(string name)
    {
        var index = _natives.IndexOf(name);
        if (index < 0)
        {
            index = _natives.Count;
            _natives.Add(name);
        }

        return index;
    }

    /// <summary>Per-function code buffer, block scopes and loop/try bookkeeping.</summary>
    private sealed class FunctionBuilder
    {
        private readonly List<int> _code = [];
        private readonly List<LineEntry> _lines = [];
        private readonly List<string> _localNames = [];

        public FunctionBuilder(string name, IReadOnlyList<string> parameters, string? mission, BlockStatement? body, HashSet<string>? enclosingLocals)
        {
            Name = name;
            ParameterCount = parameters.Count;
            Mission = mission;
            Body = body;
            EnclosingLocals = enclosingLocals ?? [];
            Scopes.Push(new Dictionary<string, (int, bool)>(StringComparer.Ordinal));
            foreach (var parameter in parameters)
            {
                Declare(parameter, isConst: false);
            }
        }

        public string Name { get; }

        public int ParameterCount { get; }

        public string? Mission { get; set; }

        public BlockStatement? Body { get; }

        public int[]? PrebuiltCode { get; set; }

        /// <summary>Locals of the function a timer body was written in; used to explain why they are not visible.</summary>
        public HashSet<string> EnclosingLocals { get; }

        public Stack<Dictionary<string, (int Slot, bool IsConst)>> Scopes { get; } = new();

        public Stack<LoopContext> Loops { get; } = new();

        public int TryDepth { get; set; }

        public int Position => _code.Count;

        public int Declare(string name, bool isConst)
        {
            var slot = _localNames.Count;
            _localNames.Add(name);
            Scopes.Peek()[name] = (slot, isConst);
            return slot;
        }

        public int Hidden(string name)
        {
            _localNames.Add("$" + name);
            return _localNames.Count - 1;
        }

        public bool TryResolve(string name, out int slot, out bool isConst)
        {
            foreach (var scope in Scopes)
            {
                if (scope.TryGetValue(name, out var local))
                {
                    (slot, isConst) = local;
                    return true;
                }
            }

            (slot, isConst) = (-1, false);
            return false;
        }

        public HashSet<string> VisibleLocals() => [.. Scopes.SelectMany(s => s.Keys)];

        public void Op(OpCode op, params int[] operands)
        {
            _code.Add((int)op);
            _code.AddRange(operands);
        }

        /// <summary>Emits a jump with a placeholder target and returns the operand position to patch.</summary>
        public int Jump(OpCode op)
        {
            Op(op, -1);
            return _code.Count - 1;
        }

        public void Patch(int operandPosition) => _code[operandPosition] = _code.Count;

        public void PatchTo(int operandPosition, int target) => _code[operandPosition] = target;

        public void MarkLine(int line)
        {
            if (_lines.Count == 0 || _lines[^1].Line != line)
            {
                if (_lines.Count > 0 && _lines[^1].Pc == _code.Count)
                {
                    _lines[^1] = new LineEntry(_code.Count, line);
                }
                else
                {
                    _lines.Add(new LineEntry(_code.Count, line));
                }
            }
        }

        public CompiledFunction Build() => new()
        {
            Name = Name,
            ParameterCount = ParameterCount,
            LocalCount = _localNames.Count,
            LocalNames = [.. _localNames],
            Code = PrebuiltCode ?? [.. _code],
            Lines = PrebuiltCode is null ? [.. _lines] : [],
        };
    }

    private sealed class LoopContext(int continueTarget, int tryDepth)
    {
        public int ContinueTarget { get; } = continueTarget;

        public int TryDepth { get; } = tryDepth;

        public List<int> Breaks { get; } = [];
    }
}
