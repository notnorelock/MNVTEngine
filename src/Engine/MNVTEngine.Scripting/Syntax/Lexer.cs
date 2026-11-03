using System.Globalization;
using System.Text;

namespace MNVTEngine.Scripting.Syntax;

/// <summary>
/// Turns source text into tokens. Newlines end statements, except inside parentheses and brackets, where they are
/// ignored so long calls and lists can span lines. Comments: <c>// line</c> and <c>/* block */</c>.
/// </summary>
public sealed class Lexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.Ordinal)
    {
        ["var"] = TokenKind.Var, ["const"] = TokenKind.Const, ["func"] = TokenKind.Func, ["return"] = TokenKind.Return,
        ["if"] = TokenKind.If, ["else"] = TokenKind.Else, ["while"] = TokenKind.While, ["for"] = TokenKind.For,
        ["in"] = TokenKind.In, ["break"] = TokenKind.Break, ["continue"] = TokenKind.Continue, ["wait"] = TokenKind.Wait,
        ["until"] = TokenKind.Until, ["event"] = TokenKind.Event, ["mission"] = TokenKind.Mission, ["objective"] = TokenKind.Objective,
        ["on"] = TokenKind.On, ["true"] = TokenKind.True, ["false"] = TokenKind.False, ["null"] = TokenKind.Null,
        ["and"] = TokenKind.And, ["or"] = TokenKind.Or, ["not"] = TokenKind.Not, ["try"] = TokenKind.Try,
        ["catch"] = TokenKind.Catch, ["throw"] = TokenKind.Throw, ["spawn"] = TokenKind.Spawn, ["after"] = TokenKind.After,
        ["every"] = TokenKind.Every, ["requires"] = TokenKind.Requires, ["as"] = TokenKind.As,
    };

    private readonly string _text;
    private readonly DiagnosticBag _diagnostics;
    private readonly List<Token> _tokens = [];
    private int _position;
    private int _line = 1;
    private int _lineStart;
    private int _groupingDepth;

    private Lexer(string text, DiagnosticBag diagnostics)
    {
        _text = text;
        _diagnostics = diagnostics;
    }

    public static IReadOnlyList<Token> Tokenize(string text, string source, out IReadOnlyList<Diagnostic> diagnostics)
    {
        var bag = new DiagnosticBag(source);
        var tokens = Tokenize(text, bag);
        diagnostics = bag.Items;
        return tokens;
    }

    /// <summary>True for words that cannot be used as names.</summary>
    public static bool IsKeyword(string word) => Keywords.ContainsKey(word);

    internal static IReadOnlyList<Token> Tokenize(string text, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lexer = new Lexer(text, diagnostics);
        lexer.Run();
        return lexer._tokens;
    }

    private SourceLocation Here => new(_line, _position - _lineStart + 1);

    private char Peek(int offset = 0) => _position + offset < _text.Length ? _text[_position + offset] : '\0';

    private void Run()
    {
        while (_position < _text.Length)
        {
            var c = Peek();
            if (c is ' ' or '\t' or '\r')
            {
                _position++;
            }
            else if (c == '\n')
            {
                if (_groupingDepth == 0)
                {
                    Add(TokenKind.Newline, "\\n", Here);
                }

                _position++;
                _line++;
                _lineStart = _position;
            }
            else if (c == '/' && Peek(1) == '/')
            {
                while (_position < _text.Length && Peek() != '\n')
                {
                    _position++;
                }
            }
            else if (c == '/' && Peek(1) == '*')
            {
                SkipBlockComment();
            }
            else if (char.IsDigit(c))
            {
                ReadNumber();
            }
            else if (c == '"')
            {
                ReadString();
            }
            else if (char.IsLetter(c) || c == '_')
            {
                ReadWord();
            }
            else
            {
                ReadPunctuation();
            }
        }

        Add(TokenKind.Newline, "\\n", Here);
        Add(TokenKind.EndOfFile, "", Here);
    }

    private void SkipBlockComment()
    {
        var start = Here;
        _position += 2;
        while (_position < _text.Length && !(Peek() == '*' && Peek(1) == '/'))
        {
            if (Peek() == '\n')
            {
                _line++;
                _lineStart = _position + 1;
            }

            _position++;
        }

        if (_position >= _text.Length)
        {
            _diagnostics.Error("MNV1003", "Unterminated block comment.", start);
            return;
        }

        _position += 2;
    }

    private void ReadNumber()
    {
        var start = _position;
        var location = Here;
        while (char.IsDigit(Peek()))
        {
            _position++;
        }

        // A '.' followed by a digit is a fraction; '..' is the range operator.
        if (Peek() == '.' && char.IsDigit(Peek(1)))
        {
            _position++;
            while (char.IsDigit(Peek()))
            {
                _position++;
            }
        }

        var text = _text[start.._position];
        Add(TokenKind.Number, text, location, double.Parse(text, CultureInfo.InvariantCulture));
    }

    private void ReadString()
    {
        var location = Here;
        var builder = new StringBuilder();
        _position++;
        while (true)
        {
            var c = Peek();
            if (c is '\0' or '\n')
            {
                _diagnostics.Error("MNV1002", "Unterminated string.", location);
                break;
            }

            _position++;
            if (c == '"')
            {
                break;
            }

            if (c == '\\')
            {
                var escape = Peek();
                _position++;
                switch (escape)
                {
                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    default:
                        _diagnostics.Error("MNV1004", $"Unknown escape sequence '\\{escape}'.", Here);
                        break;
                }
            }
            else
            {
                builder.Append(c);
            }
        }

        var value = builder.ToString();
        Add(TokenKind.String, value, location, value);
    }

    private void ReadWord()
    {
        var start = _position;
        var location = Here;
        while (char.IsLetterOrDigit(Peek()) || Peek() == '_')
        {
            _position++;
        }

        var word = _text[start.._position];
        Add(Keywords.GetValueOrDefault(word, TokenKind.Identifier), word, location);
    }

    private void ReadPunctuation()
    {
        var location = Here;
        var c = Peek();
        var next = Peek(1);
        (TokenKind Kind, int Length) token = (c, next) switch
        {
            ('=', '=') => (TokenKind.Equal, 2),
            ('!', '=') => (TokenKind.NotEqual, 2),
            ('<', '=') => (TokenKind.LessEqual, 2),
            ('>', '=') => (TokenKind.GreaterEqual, 2),
            ('+', '=') => (TokenKind.PlusAssign, 2),
            ('-', '=') => (TokenKind.MinusAssign, 2),
            ('*', '=') => (TokenKind.StarAssign, 2),
            ('/', '=') => (TokenKind.SlashAssign, 2),
            ('.', '.') => (TokenKind.DotDot, 2),
            ('(', _) => (TokenKind.LeftParen, 1),
            (')', _) => (TokenKind.RightParen, 1),
            ('{', _) => (TokenKind.LeftBrace, 1),
            ('}', _) => (TokenKind.RightBrace, 1),
            ('[', _) => (TokenKind.LeftBracket, 1),
            (']', _) => (TokenKind.RightBracket, 1),
            (',', _) => (TokenKind.Comma, 1),
            ('.', _) => (TokenKind.Dot, 1),
            (':', _) => (TokenKind.Colon, 1),
            (';', _) => (TokenKind.Semicolon, 1),
            ('=', _) => (TokenKind.Assign, 1),
            ('<', _) => (TokenKind.Less, 1),
            ('>', _) => (TokenKind.Greater, 1),
            ('+', _) => (TokenKind.Plus, 1),
            ('-', _) => (TokenKind.Minus, 1),
            ('*', _) => (TokenKind.Star, 1),
            ('/', _) => (TokenKind.Slash, 1),
            ('%', _) => (TokenKind.Percent, 1),
            _ => (TokenKind.EndOfFile, 0),
        };

        if (token.Length == 0)
        {
            _diagnostics.Error("MNV1001", $"Unexpected character '{c}'.", location);
            _position++;
            return;
        }

        if (token.Kind is TokenKind.LeftParen or TokenKind.LeftBracket)
        {
            _groupingDepth++;
        }
        else if (token.Kind is TokenKind.RightParen or TokenKind.RightBracket && _groupingDepth > 0)
        {
            _groupingDepth--;
        }

        Add(token.Kind, _text.Substring(_position, token.Length), location);
        _position += token.Length;
    }

    private void Add(TokenKind kind, string text, SourceLocation location, object? value = null) =>
        _tokens.Add(new Token(kind, text, location, value));
}
