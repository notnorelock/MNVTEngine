namespace MNVTEngine.Scripting.Syntax;

/// <summary>Location in a source file; lines and columns are 1-based.</summary>
public readonly record struct SourceLocation(int Line, int Column)
{
    public override string ToString() => $"{Line}:{Column}";
}

public enum TokenKind
{
    EndOfFile,
    Newline,
    Identifier,
    Number,
    String,

    // Keywords
    Var, Const, Func, Return, If, Else, While, For, In, Break, Continue,
    Wait, Until, Event, Mission, Objective, On, True, False, Null,
    And, Or, Not, Try, Catch, Throw, Spawn, After, Every, Requires, As,

    // Punctuation
    LeftParen, RightParen, LeftBrace, RightBrace, LeftBracket, RightBracket,
    Comma, Dot, DotDot, Colon, Semicolon,
    Assign, PlusAssign, MinusAssign, StarAssign, SlashAssign,
    Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
    Plus, Minus, Star, Slash, Percent,
}

public readonly record struct Token(TokenKind Kind, string Text, SourceLocation Location, object? Value = null)
{
    public override string ToString() => $"{Kind} '{Text}' at {Location}";
}

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>A compiler message. Codes: MNV1xxx lexical, MNV2xxx syntax, MNV3xxx semantic, MNV4xxx bytecode/linking.</summary>
public sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Message, string Source, SourceLocation Location)
{
    public override string ToString() =>
        $"{Source}({Location.Line},{Location.Column}): {(Severity == DiagnosticSeverity.Error ? "error" : "warning")} {Code}: {Message}";
}

internal sealed class DiagnosticBag(string source)
{
    private readonly List<Diagnostic> _items = [];

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasErrors => _items.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Error(string code, string message, SourceLocation at) => _items.Add(new Diagnostic(DiagnosticSeverity.Error, code, message, source, at));

    public void Warning(string code, string message, SourceLocation at) => _items.Add(new Diagnostic(DiagnosticSeverity.Warning, code, message, source, at));
}
