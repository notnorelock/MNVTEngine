namespace MNVTEngine.Scripting.Syntax;

// Abstract syntax tree. Every node carries the location of its first token for diagnostics and line tables.

public abstract record Node(SourceLocation Location);

// ---- Declarations ----

public sealed record CompilationUnit(IReadOnlyList<Declaration> Declarations, SourceLocation Location) : Node(Location);

public abstract record Declaration(SourceLocation Location) : Node(Location);

public sealed record VarDeclaration(string Name, bool IsConst, Expression? Initializer, SourceLocation Location) : Declaration(Location);

public sealed record FunctionDeclaration(string Name, IReadOnlyList<string> Parameters, BlockStatement Body, SourceLocation Location) : Declaration(Location);

/// <summary><c>on start { }</c>, <c>on complete { }</c>, ... or <c>on event "name"(payload) { }</c>.</summary>
public sealed record HandlerDeclaration(string Hook, string? EventName, string? Parameter, BlockStatement Body, SourceLocation Location) : Declaration(Location);

public sealed record MissionDeclaration(
    string Id,
    IReadOnlyDictionary<string, Expression> Metadata,
    IReadOnlyList<Declaration> Members,
    SourceLocation Location) : Declaration(Location);

public sealed record ObjectiveDeclaration(
    string Id,
    IReadOnlyDictionary<string, Expression> Metadata,
    IReadOnlyList<string> Requires,
    BlockStatement Body,
    SourceLocation Location) : Declaration(Location);

// ---- Statements ----

public abstract record Statement(SourceLocation Location) : Node(Location);

public sealed record BlockStatement(IReadOnlyList<Statement> Statements, SourceLocation Location) : Statement(Location);

public sealed record VarStatement(string Name, bool IsConst, Expression Initializer, SourceLocation Location) : Statement(Location);

/// <param name="Operator">Assign, PlusAssign, MinusAssign, StarAssign or SlashAssign.</param>
public sealed record AssignStatement(Expression Target, TokenKind Operator, Expression Value, SourceLocation Location) : Statement(Location);

public sealed record IfStatement(Expression Condition, BlockStatement Then, Statement? Else, SourceLocation Location) : Statement(Location);

public sealed record WhileStatement(Expression Condition, BlockStatement Body, SourceLocation Location) : Statement(Location);

public sealed record ForStatement(string Variable, Expression Iterable, BlockStatement Body, SourceLocation Location) : Statement(Location);

public sealed record BreakStatement(SourceLocation Location) : Statement(Location);

public sealed record ContinueStatement(SourceLocation Location) : Statement(Location);

public sealed record ReturnStatement(Expression? Value, SourceLocation Location) : Statement(Location);

public sealed record ExpressionStatement(Expression Expression, SourceLocation Location) : Statement(Location);

public sealed record WaitSecondsStatement(Expression Seconds, SourceLocation Location) : Statement(Location);

public sealed record WaitUntilStatement(Expression Condition, SourceLocation Location) : Statement(Location);

/// <summary><c>wait for event "name" [as payload]</c>.</summary>
public sealed record WaitEventStatement(Expression EventName, string? Binding, SourceLocation Location) : Statement(Location);

public sealed record TryStatement(BlockStatement Body, string? CatchVariable, BlockStatement Handler, SourceLocation Location) : Statement(Location);

public sealed record ThrowStatement(Expression Value, SourceLocation Location) : Statement(Location);

/// <summary><c>spawn f(args)</c>: runs a function as a separate coroutine.</summary>
public sealed record SpawnStatement(CallExpression Call, SourceLocation Location) : Statement(Location);

/// <summary><c>after 2.0 { }</c> runs once later; <c>every 1.0 { }</c> repeats. Bodies run as separate coroutines.</summary>
public sealed record TimerStatement(bool Repeat, Expression Seconds, BlockStatement Body, SourceLocation Location) : Statement(Location);

// ---- Expressions ----

public abstract record Expression(SourceLocation Location) : Node(Location);

/// <summary>Null, bool, double or string.</summary>
public sealed record LiteralExpression(object? Value, SourceLocation Location) : Expression(Location);

public sealed record NameExpression(string Name, SourceLocation Location) : Expression(Location);

public sealed record ListExpression(IReadOnlyList<Expression> Items, SourceLocation Location) : Expression(Location);

public sealed record MapExpression(IReadOnlyList<(string Key, Expression Value)> Entries, SourceLocation Location) : Expression(Location);

public sealed record MemberExpression(Expression Target, string Member, SourceLocation Location) : Expression(Location);

public sealed record IndexExpression(Expression Target, Expression Index, SourceLocation Location) : Expression(Location);

public sealed record CallExpression(Expression Callee, IReadOnlyList<Expression> Arguments, SourceLocation Location) : Expression(Location);

public sealed record UnaryExpression(TokenKind Operator, Expression Operand, SourceLocation Location) : Expression(Location);

public sealed record BinaryExpression(Expression Left, TokenKind Operator, Expression Right, SourceLocation Location) : Expression(Location);

/// <summary><c>a..b</c>: numbers from a up to, not including, b.</summary>
public sealed record RangeExpression(Expression Start, Expression End, SourceLocation Location) : Expression(Location);
