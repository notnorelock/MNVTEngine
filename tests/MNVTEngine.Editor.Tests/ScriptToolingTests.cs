using MNVTEngine.Editor.Services;

namespace MNVTEngine.Editor.Tests;

public sealed class SyntaxHighlighterTests
{
    /// <summary>The coloured words of one line as (text, class), plain text left out.</summary>
    private static List<(string Text, TokenClass Class)> Tokens(HighlightedText highlighted, int line)
    {
        var text = highlighted.Line(line);
        return [.. highlighted.Segments(line).Where(s => s.Class != TokenClass.Plain).Select(s => (text.Substring(s.Column, s.Length), s.Class))];
    }

    [Fact]
    public void CSharp_KeywordsTypesCallsStringsNumbersAndComments()
    {
        var code = SyntaxHighlighter.Highlight("public sealed class Player : Component // the hero\n{\n    if (Input.GetKeyDown(Key.Space)) { speed = 4.5f; Log.Info($\"jump {speed}\"); }\n}", ScriptLanguage.CSharp);
        Assert.Equal(4, code.LineCount);
        Assert.Equal(
            [("public", TokenClass.Keyword), ("sealed", TokenClass.Keyword), ("class", TokenClass.Keyword), ("Player", TokenClass.Type), ("Component", TokenClass.Type), ("// the hero", TokenClass.Comment)],
            Tokens(code, 0));
        Assert.Equal(
            [("if", TokenClass.Control), ("Input", TokenClass.Type), ("GetKeyDown", TokenClass.Function), ("Key", TokenClass.Type), ("Space", TokenClass.Type),
             ("speed", TokenClass.Variable), ("4.5f", TokenClass.Number), ("Log", TokenClass.Type), ("Info", TokenClass.Function), ("$\"jump {speed}\"", TokenClass.String)],
            Tokens(code, 2));
    }

    [Fact]
    public void CSharp_MultiLineCommentsAndStrings_ColourEveryLineTheyCover()
    {
        var code = SyntaxHighlighter.Highlight("/* one\ntwo */ int x;\nvar s = @\"a\n\"\"b\"\"\";\n#if DEBUG\nvar r = \"\"\"\nraw \" text\n\"\"\";", ScriptLanguage.CSharp);
        Assert.Equal([("/* one", TokenClass.Comment)], Tokens(code, 0));
        Assert.Equal([("two */", TokenClass.Comment), ("int", TokenClass.Keyword), ("x", TokenClass.Variable)], Tokens(code, 1));
        Assert.Equal(("@\"a", TokenClass.String), Tokens(code, 2)[^1]);
        Assert.Equal([("\"\"b\"\"\"", TokenClass.String)], Tokens(code, 3)); // a verbatim string's "" does not end it
        Assert.Equal([("#if DEBUG", TokenClass.Preprocessor)], Tokens(code, 4));
        Assert.Equal([("raw \" text", TokenClass.String)], Tokens(code, 6)); // inside a raw string a single quote is text
    }

    [Fact]
    public void Generic_Calls_AreFunctions_ButComparisonsAreNot()
    {
        var code = SyntaxHighlighter.Highlight("var b = GetComponent<Rigidbody2D>(); if (a < b) {}", ScriptLanguage.CSharp);
        var tokens = Tokens(code, 0);
        Assert.Contains(("GetComponent", TokenClass.Function), tokens);
        Assert.Contains(("a", TokenClass.Variable), tokens);
    }

    [Fact]
    public void Mnvts_UsesItsOwnKeywords_AndColoursNamespacesAsTypes()
    {
        var code = SyntaxHighlighter.Highlight("mission intro {\n  on start { wait 2; audio.play(\"Door\") } // go\n}", ScriptLanguage.Mnvts);
        Assert.Equal([("mission", TokenClass.Keyword), ("intro", TokenClass.Variable)], Tokens(code, 0));
        Assert.Equal(
            [("on", TokenClass.Keyword), ("start", TokenClass.Variable), ("wait", TokenClass.Control), ("2", TokenClass.Number), ("audio", TokenClass.Type), ("play", TokenClass.Function), ("\"Door\"", TokenClass.String), ("// go", TokenClass.Comment)],
            Tokens(code, 1));
    }

    [Fact]
    public void Segments_CoverEachLineExactly()
    {
        const string Text = "int a = 1; // x\r\n\nstring b = \"q\";";
        var code = SyntaxHighlighter.Highlight(Text, ScriptLanguage.CSharp);
        for (var i = 0; i < code.LineCount; i++)
        {
            var line = code.Line(i);
            Assert.Equal(line, string.Concat(code.Segments(i).Select(s => line.Substring(s.Column, s.Length))));
        }

        Assert.Equal("int a = 1; // x", code.Line(0)); // \r is not part of the line
        Assert.Equal(ScriptLanguage.Mnvts, SyntaxHighlighter.LanguageOf("Missions/intro.MNVTS"));
        Assert.Null(SyntaxHighlighter.LanguageOf("readme.txt"));
    }
}

public sealed class ExternalEditorTests
{
    [Fact]
    public void VisualStudioCode_OpensTheProjectFolder_AtTheFileAndLine()
    {
        var code = Path.Combine(Path.GetTempPath(), "mnvt-test-code-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(code, []);
        try
        {
            var start = ExternalEditor.Command(new EditorPreferences { ExternalEditor = ExternalEditorKind.VisualStudioCode }, @"C:\Game\Scripts\Player.cs", 12, 5, @"C:\Game", () => code);
            Assert.Equal(code, start.FileName);
            Assert.Equal([@"C:\Game", "--goto", @"C:\Game\Scripts\Player.cs:12:5"], start.ArgumentList);
            Assert.False(start.UseShellExecute);
        }
        finally
        {
            File.Delete(code);
        }

        Assert.Throws<InvalidOperationException>(() => ExternalEditor.Command(new EditorPreferences { ExternalEditor = ExternalEditorKind.VisualStudioCode }, "a.cs", 1, 1, "C:\\Game", () => null));
    }

    [Fact]
    public void CustomEditors_GetTheirArgumentTemplateFilledIn()
    {
        var preferences = new EditorPreferences { ExternalEditor = ExternalEditorKind.Custom, CustomEditorPath = @"C:\Tools\npp.exe", CustomEditorArguments = "\"{file}\" -n{line} -c{column} -project \"{project}\"" };
        var start = ExternalEditor.Command(preferences, @"C:\Game\Missions\a.mnvts", 3, 0, @"C:\Game");
        Assert.Equal(@"C:\Tools\npp.exe", start.FileName);
        Assert.Equal("\"C:\\Game\\Missions\\a.mnvts\" -n3 -c1 -project \"C:\\Game\"", start.Arguments); // columns below 1 become 1
        Assert.Throws<InvalidOperationException>(() => ExternalEditor.Command(new EditorPreferences { ExternalEditor = ExternalEditorKind.Custom }, "a.cs", 1, 1, "C:\\Game"));
        Assert.True(ExternalEditor.Command(new EditorPreferences(), "a.cs", 1, 1, "C:\\Game").UseShellExecute); // the system default
    }

    [Fact]
    public void Preferences_RoundTrip_AndADamagedFileGivesDefaults()
    {
        var file = Path.Combine(Path.GetTempPath(), "mnvt-test-prefs-" + Guid.NewGuid().ToString("N"), "editor-preferences.json");
        try
        {
            new EditorPreferences { ExternalEditor = ExternalEditorKind.VisualStudioCode, OpenScriptsExternally = true, VisualStudioCodePath = @"D:\Code\Code.exe" }.Save(file);
            var loaded = EditorPreferences.Load(file);
            Assert.Equal((ExternalEditorKind.VisualStudioCode, true, @"D:\Code\Code.exe"), (loaded.ExternalEditor, loaded.OpenScriptsExternally, loaded.VisualStudioCodePath));
            File.WriteAllText(file, "{ not json");
            Assert.Equal(ExternalEditorKind.SystemDefault, EditorPreferences.Load(file).ExternalEditor);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
