namespace MNVTEngine.Scripting.Syntax;

/// <summary>
/// Recursive-descent parser producing a <see cref="CompilationUnit"/>. On a syntax error it reports a diagnostic, skips to
/// the end of the statement and continues, so one typo yields one message instead of a cascade.
/// </summary>
public sealed class Parser
{
    /// <summary>Keys allowed as <c>key = value</c> metadata at the start of mission and objective blocks.</summary>
    public static readonly IReadOnlySet<string> MissionMetadataKeys = new HashSet<string>(StringComparer.Ordinal) { "name", "description", "category" };

    public static readonly IReadOnlySet<string> ObjectiveMetadataKeys = new HashSet<string>(StringComparer.Ordinal) { "description", "optional", "hidden" };

    private readonly IReadOnlyList<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private int _index;

    private Parser(IReadOnlyList<Token> tokens, DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    public static CompilationUnit Parse(string text, string source, out IReadOnlyList<Diagnostic> diagnostics)
    {
        var bag = new DiagnosticBag(source);
        var unit = Parse(text, bag);
        diagnostics = bag.Items;
        return unit;
    }

    internal static CompilationUnit Parse(string text, DiagnosticBag diagnostics) =>
        new Parser(Lexer.Tokenize(text, diagnostics), diagnostics).ParseUnit();

    private Token Current => _tokens[_index];

    private TokenKind Kind => Current.Kind;

    // ---- Declarations ----

    private CompilationUnit ParseUnit()
    {
        var declarations = new List<Declaration>();
        SkipNewlines();
        while (Kind != TokenKind.EndOfFile)
        {
            if (Recover(() => ParseTopLevel()) is { } declaration)
            {
                declarations.Add(declaration);
            }

            SkipNewlines();
        }

        return new CompilationUnit(declarations, new SourceLocation(1, 1));
    }

    private Declaration ParseTopLevel() => Kind switch
    {
        TokenKind.Var or TokenKind.Const => ParseVarDeclaration(),
        TokenKind.Func => ParseFunction(),
        TokenKind.On => ParseHandler(),
        TokenKind.Mission => ParseMission(),
        _ => throw Error("MNV2001", $"Expected a declaration (var, const, func, on, mission), found {Describe(Current)}."),
    };

    private VarDeclaration ParseVarDeclaration()
    {
        var keyword = Advance();
        var name = Expect(TokenKind.Identifier, "a variable name");
        Expression? initializer = null;
        if (Match(TokenKind.Assign))
        {
            initializer = ParseExpression();
        }
        else if (keyword.Kind == TokenKind.Const)
        {
            throw Error("MNV2002", $"Constant '{name.Text}' needs a value.");
        }

        EndStatement();
        return new VarDeclaration(name.Text, keyword.Kind == TokenKind.Const, initializer, keyword.Location);
    }

    private FunctionDeclaration ParseFunction()
    {
        var keyword = Expect(TokenKind.Func, "'func'");
        var name = Expect(TokenKind.Identifier, "a function name");
        Expect(TokenKind.LeftParen, "'('");
        var parameters = new List<string>();
        if (Kind != TokenKind.RightParen)
        {
            do
            {
                parameters.Add(Expect(TokenKind.Identifier, "a parameter name").Text);
            }
            while (Match(TokenKind.Comma));
        }

        Expect(TokenKind.RightParen, "')'");
        return new FunctionDeclaration(name.Text, parameters, ParseBlock(), keyword.Location);
    }

    private HandlerDeclaration ParseHandler()
    {
        var keyword = Expect(TokenKind.On, "'on'");
        if (Match(TokenKind.Event))
        {
            var name = Expect(TokenKind.String, "an event name string");
            string? parameter = null;
            if (Match(TokenKind.LeftParen))
            {
                parameter = Expect(TokenKind.Identifier, "a payload parameter name").Text;
                Expect(TokenKind.RightParen, "')'");
            }

            return new HandlerDeclaration("event", (string)name.Value!, parameter, ParseBlock(), keyword.Location);
        }

        var hook = Expect(TokenKind.Identifier, "a hook name (start, complete, fail, cancel) or 'event'");
        if (hook.Text is not ("start" or "complete" or "fail" or "cancel"))
        {
            throw Error("MNV2003", $"Unknown hook '{hook.Text}'. Expected start, complete, fail, cancel or event.", hook.Location);
        }

        return new HandlerDeclaration(hook.Text, null, null, ParseBlock(), keyword.Location);
    }

    private MissionDeclaration ParseMission()
    {
        var keyword = Expect(TokenKind.Mission, "'mission'");
        var id = Expect(TokenKind.String, "a mission id string");
        Expect(TokenKind.LeftBrace, "'{'");
        var metadata = new Dictionary<string, Expression>(StringComparer.Ordinal);
        var members = new List<Declaration>();
        SkipNewlines();
        while (Kind is not (TokenKind.RightBrace or TokenKind.EndOfFile))
        {
            var member = Recover<Declaration>(() =>
            {
                if (Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Assign)
                {
                    var key = Advance();
                    if (!MissionMetadataKeys.Contains(key.Text))
                    {
                        throw Error("MNV2004", $"Unknown mission property '{key.Text}'. Allowed: {string.Join(", ", MissionMetadataKeys)}. Use 'var' for mission variables.", key.Location);
                    }

                    Advance();
                    metadata[key.Text] = ParseExpression();
                    EndStatement();
                    return null;
                }

                return Kind switch
                {
                    TokenKind.Var or TokenKind.Const => ParseVarDeclaration(),
                    TokenKind.Func => ParseFunction(),
                    TokenKind.On => ParseHandler(),
                    TokenKind.Objective => (Declaration)ParseObjective(),
                    _ => throw Error("MNV2005", $"Expected a mission member (property, var, func, on, objective), found {Describe(Current)}."),
                };
            });

            if (member is not null)
            {
                members.Add(member);
            }

            SkipNewlines();
        }

        Expect(TokenKind.RightBrace, "'}' closing the mission");
        return new MissionDeclaration((string)id.Value!, metadata, members, keyword.Location);
    }

    private ObjectiveDeclaration ParseObjective()
    {
        var keyword = Expect(TokenKind.Objective, "'objective'");
        var id = Expect(TokenKind.String, "an objective id string");
        var open = Expect(TokenKind.LeftBrace, "'{'");
        var metadata = new Dictionary<string, Expression>(StringComparer.Ordinal);
        var requires = new List<string>();
        SkipNewlines();

        // Header: metadata and 'requires' lines, then ordinary statements.
        while (true)
        {
            if (Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Assign && ObjectiveMetadataKeys.Contains(Current.Text))
            {
                var key = Advance();
                Advance();
                metadata[key.Text] = ParseExpression();
                EndStatement();
            }
            else if (Match(TokenKind.Requires))
            {
                do
                {
                    requires.Add((string)Expect(TokenKind.String, "an objective id string").Value!);
                }
                while (Match(TokenKind.Comma));
                EndStatement();
            }
            else
            {
                break;
            }

            SkipNewlines();
        }

        var statements = ParseStatementsUntilBrace();
        Expect(TokenKind.RightBrace, "'}' closing the objective");
        return new ObjectiveDeclaration((string)id.Value!, metadata, requires, new BlockStatement(statements, open.Location), keyword.Location);
    }

    // ---- Statements ----

    private BlockStatement ParseBlock()
    {
        var open = Expect(TokenKind.LeftBrace, "'{'");
        var statements = ParseStatementsUntilBrace();
        Expect(TokenKind.RightBrace, "'}'");
        return new BlockStatement(statements, open.Location);
    }

    private List<Statement> ParseStatementsUntilBrace()
    {
        var statements = new List<Statement>();
        SkipNewlines();
        while (Kind is not (TokenKind.RightBrace or TokenKind.EndOfFile))
        {
            if (Recover(ParseStatement) is { } statement)
            {
                statements.Add(statement);
            }

            SkipNewlines();
        }

        return statements;
    }

    private Statement ParseStatement()
    {
        var start = Current;
        Statement statement;
        switch (Kind)
        {
            case TokenKind.Var or TokenKind.Const:
                Advance();
                var name = Expect(TokenKind.Identifier, "a variable name");
                Expect(TokenKind.Assign, "'=' (local variables need a value)");
                statement = new VarStatement(name.Text, start.Kind == TokenKind.Const, ParseExpression(), start.Location);
                break;
            case TokenKind.If:
                return ParseIf();
            case TokenKind.While:
                Advance();
                var condition = ParseExpression();
                return new WhileStatement(condition, ParseBlock(), start.Location);
            case TokenKind.For:
                Advance();
                var variable = Expect(TokenKind.Identifier, "a loop variable");
                Expect(TokenKind.In, "'in'");
                var iterable = ParseExpression();
                return new ForStatement(variable.Text, iterable, ParseBlock(), start.Location);
            case TokenKind.Break:
                Advance();
                statement = new BreakStatement(start.Location);
                break;
            case TokenKind.Continue:
                Advance();
                statement = new ContinueStatement(start.Location);
                break;
            case TokenKind.Return:
                Advance();
                var value = Kind is TokenKind.Newline or TokenKind.Semicolon or TokenKind.RightBrace ? null : ParseExpression();
                statement = new ReturnStatement(value, start.Location);
                break;
            case TokenKind.Wait:
                statement = ParseWait();
                break;
            case TokenKind.Try:
                return ParseTry();
            case TokenKind.Throw:
                Advance();
                statement = new ThrowStatement(ParseExpression(), start.Location);
                break;
            case TokenKind.Spawn:
                Advance();
                statement = ParseExpression() is CallExpression call
                    ? new SpawnStatement(call, start.Location)
                    : throw Error("MNV2006", "'spawn' must be followed by a function call.", start.Location);
                break;
            case TokenKind.After or TokenKind.Every:
                Advance();
                var seconds = ParseExpression();
                return new TimerStatement(start.Kind == TokenKind.Every, seconds, ParseBlock(), start.Location);
            default:
                statement = ParseAssignmentOrExpression();
                break;
        }

        EndStatement();
        return statement;
    }

    private Statement ParseWait()
    {
        var start = Expect(TokenKind.Wait, "'wait'");
        if (Match(TokenKind.Until))
        {
            return new WaitUntilStatement(ParseExpression(), start.Location);
        }

        if (Match(TokenKind.For))
        {
            Expect(TokenKind.Event, "'event' after 'wait for'");
            var name = ParseExpression();
            string? binding = null;
            if (Match(TokenKind.As))
            {
                binding = Expect(TokenKind.Identifier, "a variable name after 'as'").Text;
            }

            return new WaitEventStatement(name, binding, start.Location);
        }

        return new WaitSecondsStatement(ParseExpression(), start.Location);
    }

    private IfStatement ParseIf()
    {
        var start = Expect(TokenKind.If, "'if'");
        var condition = ParseExpression();
        var then = ParseBlock();
        Statement? otherwise = null;

        // 'else' may follow on the next line.
        var save = _index;
        SkipNewlines();
        if (Match(TokenKind.Else))
        {
            otherwise = Kind == TokenKind.If ? ParseIf() : ParseBlock();
        }
        else
        {
            _index = save;
        }

        return new IfStatement(condition, then, otherwise, start.Location);
    }

    private TryStatement ParseTry()
    {
        var start = Expect(TokenKind.Try, "'try'");
        var body = ParseBlock();
        SkipNewlines();
        Expect(TokenKind.Catch, "'catch' after a try block");
        var variable = Kind == TokenKind.Identifier ? Advance().Text : null;
        return new TryStatement(body, variable, ParseBlock(), start.Location);
    }

    private Statement ParseAssignmentOrExpression()
    {
        var start = Current;
        var expression = ParseExpression();
        if (Kind is TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign or TokenKind.StarAssign or TokenKind.SlashAssign)
        {
            var op = Advance();
            if (expression is not (NameExpression or MemberExpression or IndexExpression))
            {
                throw Error("MNV2007", "Only variables, map members and list elements can be assigned.", op.Location);
            }

            return new AssignStatement(expression, op.Kind, ParseExpression(), start.Location);
        }

        return new ExpressionStatement(expression, start.Location);
    }

    // ---- Expressions (lowest to highest precedence) ----

    private Expression ParseExpression() => ParseOr();

    private Expression ParseOr() => ParseBinary(ParseAnd, TokenKind.Or);

    private Expression ParseAnd() => ParseBinary(ParseEquality, TokenKind.And);

    private Expression ParseEquality() => ParseBinary(ParseComparison, TokenKind.Equal, TokenKind.NotEqual);

    private Expression ParseComparison() => ParseBinary(ParseRange, TokenKind.Less, TokenKind.LessEqual, TokenKind.Greater, TokenKind.GreaterEqual);

    private Expression ParseRange()
    {
        var left = ParseAdditive();
        if (Kind == TokenKind.DotDot)
        {
            var op = Advance();
            return new RangeExpression(left, ParseAdditive(), op.Location);
        }

        return left;
    }

    private Expression ParseAdditive() => ParseBinary(ParseMultiplicative, TokenKind.Plus, TokenKind.Minus);

    private Expression ParseMultiplicative() => ParseBinary(ParseUnary, TokenKind.Star, TokenKind.Slash, TokenKind.Percent);

    private Expression ParseBinary(Func<Expression> operand, params TokenKind[] operators)
    {
        var left = operand();
        while (operators.Contains(Kind))
        {
            var op = Advance();
            left = new BinaryExpression(left, op.Kind, operand(), op.Location);
        }

        return left;
    }

    private Expression ParseUnary()
    {
        if (Kind is TokenKind.Minus or TokenKind.Not)
        {
            var op = Advance();
            return new UnaryExpression(op.Kind, ParseUnary(), op.Location);
        }

        return ParsePostfix();
    }

    private Expression ParsePostfix()
    {
        var expression = ParsePrimary();
        while (true)
        {
            if (Kind == TokenKind.LeftParen)
            {
                var open = Advance();
                var arguments = new List<Expression>();
                if (Kind != TokenKind.RightParen)
                {
                    do
                    {
                        arguments.Add(ParseExpression());
                    }
                    while (Match(TokenKind.Comma));
                }

                Expect(TokenKind.RightParen, "')'");
                expression = new CallExpression(expression, arguments, open.Location);
            }
            else if (Kind == TokenKind.Dot)
            {
                var dot = Advance();
                var member = Current.Kind == TokenKind.Identifier || Lexer.IsKeyword(Current.Text)
                    ? Advance()
                    : throw Error("MNV2008", $"Expected a member name after '.', found {Describe(Current)}.");
                expression = new MemberExpression(expression, member.Text, dot.Location);
            }
            else if (Kind == TokenKind.LeftBracket)
            {
                var open = Advance();
                var index = ParseExpression();
                Expect(TokenKind.RightBracket, "']'");
                expression = new IndexExpression(expression, index, open.Location);
            }
            else
            {
                return expression;
            }
        }
    }

    private Expression ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Number or TokenKind.String:
                Advance();
                return new LiteralExpression(token.Value, token.Location);
            case TokenKind.True or TokenKind.False:
                Advance();
                return new LiteralExpression(token.Kind == TokenKind.True, token.Location);
            case TokenKind.Null:
                Advance();
                return new LiteralExpression(null, token.Location);
            case TokenKind.Identifier:
                Advance();
                return new NameExpression(token.Text, token.Location);
            case TokenKind.Objective or TokenKind.Mission or TokenKind.Event:
                // Keywords that double as API module names: objective.complete(), mission.fail(), event...
                Advance();
                return new NameExpression(token.Text, token.Location);
            case TokenKind.LeftParen:
                Advance();
                var inner = ParseExpression();
                Expect(TokenKind.RightParen, "')'");
                return inner;
            case TokenKind.LeftBracket:
                Advance();
                var items = new List<Expression>();
                if (Kind != TokenKind.RightBracket)
                {
                    do
                    {
                        items.Add(ParseExpression());
                    }
                    while (Match(TokenKind.Comma));
                }

                Expect(TokenKind.RightBracket, "']'");
                return new ListExpression(items, token.Location);
            case TokenKind.LeftBrace:
                return ParseMap();
            default:
                throw Error("MNV2009", $"Expected an expression, found {Describe(token)}.");
        }
    }

    private MapExpression ParseMap()
    {
        var open = Expect(TokenKind.LeftBrace, "'{'");
        var entries = new List<(string, Expression)>();
        SkipNewlines();
        while (Kind != TokenKind.RightBrace)
        {
            var key = Kind is TokenKind.String or TokenKind.Identifier
                ? Advance()
                : throw Error("MNV2010", $"Expected a map key (name or string), found {Describe(Current)}.");
            Expect(TokenKind.Colon, "':' after a map key");
            entries.Add(((string?)key.Value ?? key.Text, ParseExpression()));
            SkipNewlines();
            if (!Match(TokenKind.Comma))
            {
                break;
            }

            SkipNewlines();
        }

        SkipNewlines();
        Expect(TokenKind.RightBrace, "'}' closing the map");
        return new MapExpression(entries, open.Location);
    }

    // ---- Helpers ----

    private T? Recover<T>(Func<T?> parse)
        where T : class
    {
        try
        {
            return parse();
        }
        catch (ParseError)
        {
            // Skip the rest of the broken statement; a '}' is left for the enclosing block.
            while (Kind is not (TokenKind.Newline or TokenKind.Semicolon or TokenKind.RightBrace or TokenKind.EndOfFile))
            {
                Advance();
            }

            Match(TokenKind.Newline);
            Match(TokenKind.Semicolon);
            return null;
        }
    }

    private void EndStatement()
    {
        if (Kind is TokenKind.Newline or TokenKind.Semicolon)
        {
            Advance();
        }
        else if (Kind is not (TokenKind.RightBrace or TokenKind.EndOfFile))
        {
            throw Error("MNV2011", $"Expected the end of the statement, found {Describe(Current)}.");
        }
    }

    private void SkipNewlines()
    {
        while (Kind is TokenKind.Newline or TokenKind.Semicolon)
        {
            Advance();
        }
    }

    private Token Peek(int offset) => _tokens[Math.Min(_index + offset, _tokens.Count - 1)];

    private Token Advance()
    {
        var token = Current;
        if (_index < _tokens.Count - 1)
        {
            _index++;
        }

        return token;
    }

    private bool Match(TokenKind kind)
    {
        if (Kind != kind)
        {
            return false;
        }

        Advance();
        return true;
    }

    private Token Expect(TokenKind kind, string what) =>
        Kind == kind ? Advance() : throw Error("MNV2000", $"Expected {what}, found {Describe(Current)}.");

    private ParseError Error(string code, string message, SourceLocation? at = null)
    {
        _diagnostics.Error(code, message, at ?? Current.Location);
        return new ParseError();
    }

    private static string Describe(Token token) => token.Kind switch
    {
        TokenKind.EndOfFile => "end of file",
        TokenKind.Newline => "end of line",
        TokenKind.String => $"string \"{token.Text}\"",
        _ => $"'{token.Text}'",
    };

    private sealed class ParseError : Exception;
}
