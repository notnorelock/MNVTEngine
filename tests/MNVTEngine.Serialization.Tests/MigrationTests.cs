using Newtonsoft.Json.Linq;

namespace MNVTEngine.Serialization.Tests;

public class MigrationTests
{
    private static JsonMigrationPipeline Pipeline() => new JsonMigrationPipeline(3)
        .Add(1, d => d["b"] = d["a"])
        .Add(2, d => d.Remove("a"));

    [Fact]
    public void AppliesStepsInOrderAndStampsVersion()
    {
        var doc = JObject.Parse("""{ "schemaVersion": 1, "a": 5 }""");
        var diagnostics = new List<SerializationDiagnostic>();
        Assert.Equal(1, Pipeline().Migrate(doc, diagnostics));
        Assert.Equal(3, doc.Value<int>("schemaVersion"));
        Assert.Equal(5, doc.Value<int>("b"));
        Assert.Null(doc["a"]);
        Assert.Equal(DiagnosticSeverity.Info, Assert.Single(diagnostics).Severity);
    }

    [Fact]
    public void CurrentVersion_IsUntouched()
    {
        var diagnostics = new List<SerializationDiagnostic>();
        Pipeline().Migrate(JObject.Parse("""{ "schemaVersion": 3 }"""), diagnostics);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("""{ "a": 1 }""", "Missing")]
    [InlineData("""{ "schemaVersion": "2" }""", "Missing")]
    [InlineData("""{ "schemaVersion": 4 }""", "newer")]
    [InlineData("""{ "schemaVersion": 0 }""", "No migration from schema version 0")]
    public void InvalidVersions_Throw(string json, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Pipeline().Migrate(JObject.Parse(json), []));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistrationErrors()
    {
        var pipeline = new JsonMigrationPipeline(2).Add(1, _ => { });
        Assert.Throws<InvalidOperationException>(() => pipeline.Add(1, _ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => pipeline.Add(2, _ => { }));
    }
}
