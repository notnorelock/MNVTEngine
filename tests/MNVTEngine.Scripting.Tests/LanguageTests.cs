using MNVTEngine.Scripting.Syntax;
using static MNVTEngine.Scripting.Tests.ScriptTestHost;

namespace MNVTEngine.Scripting.Tests;

public class LanguageTests
{
    [Fact]
    public void Lexer_TokensLocationsAndNewlinesInsideParens()
    {
        var tokens = Lexer.Tokenize("var x = f(1,\n 2.5) // c\n\"a\\n\"", "t", out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(
            [TokenKind.Var, TokenKind.Identifier, TokenKind.Assign, TokenKind.Identifier, TokenKind.LeftParen, TokenKind.Number, TokenKind.Comma,
             TokenKind.Number, TokenKind.RightParen, TokenKind.Newline, TokenKind.String, TokenKind.Newline, TokenKind.EndOfFile],
            tokens.Select(t => t.Kind));
        Assert.Equal(2.5, tokens[7].Value);
        Assert.Equal(new SourceLocation(2, 2), tokens[7].Location);
        Assert.Equal("a\n", tokens[10].Value);
    }

    [Theory]
    [InlineData("var s = \"open", "MNV1002")]
    [InlineData("var x = 1 # 2", "MNV1001")]
    [InlineData("/* never closed", "MNV1003")]
    [InlineData("var s = \"\\q\"", "MNV1004")]
    public void Lexer_Errors(string source, string code) => Assert.Contains(Errors(source), d => d.Code == code);

    [Fact]
    public void Parser_RecoversAndReportsEachError()
    {
        var errors = Errors("""
            func a() {
                var = 1
                log("still parsed")
                if { }
            }
            func b() { log(1 +) }
            """);
        Assert.Equal(3, errors.Count);
        Assert.Equal([2, 4, 6], errors.Select(e => e.Location.Line));
    }

    [Fact]
    public void ArithmeticPrecedenceAndStrings() => Assert.Equal(
        ["7", "1", "true", "ab3", "-2", "2.5"],
        RunToEnd("""
            on start {
                log(1 + 2 * 3)
                log(10 % 3)
                log(1 < 2 and not (3 < 2))
                log("a" + "b" + 3)
                log(-(1 + 1))
                log(5 / 2)
            }
            """));

    [Fact]
    public void Variables_ConstantsScopesAndShadowing() => Assert.Equal(
        ["outer", "inner", "outer", "11"],
        RunToEnd("""
            const NAME = "outer"
            var counter = 10
            on start {
                log(NAME)
                if true {
                    var NAME = "inner"
                    log(NAME)
                }
                log(NAME)
                counter += 1
                log(counter)
            }
            """));

    [Fact]
    public void Functions_RecursionAndReturn() => Assert.Equal(
        ["120", "null"],
        RunToEnd("""
            func fact(n) {
                if n <= 1 { return 1 }
                return n * fact(n - 1)
            }
            func nothing() { }
            on start {
                log(fact(5))
                log(nothing())
            }
            """));

    [Fact]
    public void Loops_WhileForBreakContinue() => Assert.Equal(
        ["0", "1", "3", "a", "b", "x", "y", "6"],
        RunToEnd("""
            on start {
                for i in 0..5 {
                    if i == 2 { continue }
                    if i == 4 { break }
                    log(i)
                }
                for item in ["a", "b"] { log(item) }
                for key in { x: 1, y: 2 } { log(key) }
                var n = 0
                var total = 0
                while n < 4 {
                    total += n
                    n += 1
                }
                log(total)
            }
            """));

    [Fact]
    public void ListsAndMaps() => Assert.Equal(
        ["3", "[1, 2, 9]", "door", "null", "{name: door, open: true}", "true", "2"],
        RunToEnd("""
            on start {
                var items = [1, 2]
                push(items, 3)
                log(len(items))
                items[2] = 9
                log(items)
                var thing = { name: "door" }
                log(thing.name)
                log(thing.missing)
                thing.open = true
                log(thing)
                log(contains(items, 9))
                log(len(keys(thing)))
            }
            """));

    [Fact]
    public void ShortCircuitReturnsOperandValues() => Assert.Equal(
        ["fallback", "null", "true", "never evaluated"],
        RunToEnd("""
            func boom() { throw "should not run" }
            on start {
                log(null or "fallback")
                log(null and boom())
                log(true or boom())
                log("never evaluated")
            }
            """));

    [Fact]
    public void TryCatch_ThrowAndRuntimeErrors() => Assert.Equal(
        ["caught: custom", "caught: Division by zero.", "{code: 7}", "after"],
        RunToEnd("""
            on start {
                try { throw "custom" } catch e { log("caught: " + e) }
                try { var x = 1 / 0 } catch e { log("caught: " + e) }
                try { throw { code: 7 } } catch e { log(e) }
                log("after")
            }
            """));

    [Fact]
    public void BreakOutOfTryInsideLoop_DropsTheHandler() => Assert.Equal(
        ["outer caught: later"],
        RunToEnd("""
            on start {
                try {
                    while true {
                        try { break } catch e { log("wrong handler") }
                    }
                    throw "later"
                } catch e { log("outer caught: " + e) }
            }
            """));

    [Theory]
    [InlineData("on start { log(missing) }", "MNV3011")]
    [InlineData("const A = 1\non start { A = 2 }", "MNV3004")]
    [InlineData("on start { break }", "MNV3016")]
    [InlineData("func f(a) { }\non start { f() }", "MNV3019")]
    [InlineData("on start { nope.call() }", "MNV3014")]
    [InlineData("on start { log(1, 2) }", "MNV3019")]
    [InlineData("var x = 1\nvar x = 2", "MNV3002")]
    [InlineData("on start { var x = \"a\" - 1 }", "MNV3020")]
    [InlineData("on start { wait -1 }", "MNV3022")]
    [InlineData("on start { var x = 1\n after 1 { log(x) } }", "MNV3010")]
    [InlineData("on complete { }", "MNV3005")]
    public void SemanticErrors(string source, string code)
    {
        var errors = Errors(source, new Compiler.CompileOptions { Api = new Compiler.ScriptApi().Add("log", 1, 1, "") });
        Assert.Contains(errors, e => e.Code == code);
    }

    [Fact]
    public void UnknownModuleFunctions_CanBeDeferredToLinkTime()
    {
        var result = Compiler.ScriptCompiler.Compile("on start { game.reward(5) }", "t", new Compiler.CompileOptions { AllowUnknownFunctions = true });
        Assert.True(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "MNV3015" && d.Severity == DiagnosticSeverity.Warning);
        var link = Assert.Throws<VM.ScriptLinkException>(() => new VM.ScriptVM(result.Script!));
        Assert.Equal(["game.reward"], link.Missing);
    }

    [Fact]
    public void Diagnostics_FormatWithFileLineAndCode()
    {
        var error = Errors("on start {\n  log(missing)\n}", new Compiler.CompileOptions { Api = new Compiler.ScriptApi().Add("log", 1, 1, "") }).Single();
        Assert.Equal("test.mnvts(2,7): error MNV3011: Undefined name 'missing'.", error.ToString());
    }
}
