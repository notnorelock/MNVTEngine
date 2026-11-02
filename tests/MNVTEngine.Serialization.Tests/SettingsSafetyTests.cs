using Newtonsoft.Json;

namespace MNVTEngine.Serialization.Tests;

public class SettingsSafetyTests
{
    public sealed class Holder
    {
        public object? Payload { get; set; }
    }

    public sealed class Strict
    {
        public int Value { get; set; }
    }

    public enum Mode
    {
        Normal,
        FastForward,
    }

    [Fact]
    public void TypeNameHandling_IsDisabled()
    {
        var settings = MnvtJson.CreateSettings();
        Assert.Equal(TypeNameHandling.None, settings.TypeNameHandling);
        Assert.Equal(MetadataPropertyHandling.Ignore, settings.MetadataPropertyHandling);
    }

    [Fact]
    public void TypeMetadata_InDataIsNotHonored()
    {
        // A classic gadget payload; must never be instantiated.
        const string json = """{"payload":{"$type":"System.IO.FileInfo, System.IO.FileSystem","fileName":"x"}}""";
        var holder = MnvtJson.Deserialize<Holder>(json);
        Assert.IsNotType<FileInfo>(holder.Payload);
    }

    [Fact]
    public void TypeMetadata_OnKnownTypeIsRejectedAsUnknownMember() =>
        Assert.Throws<JsonSerializationException>(() => MnvtJson.Deserialize<Strict>("""{"$type":"X","value":1}"""));

    [Fact]
    public void UnknownMembers_AreErrors() =>
        Assert.Throws<JsonSerializationException>(() => MnvtJson.Deserialize<Strict>("""{"value":1,"other":2}"""));

    [Fact]
    public void Names_AreCamelCase() =>
        Assert.Contains("\"value\"", MnvtJson.Serialize(new Strict { Value = 1 }), StringComparison.Ordinal);

    [Fact]
    public void Enums_AreCamelCaseStrings_AndIntegersRejected()
    {
        Assert.Equal("\"fastForward\"", MnvtJson.Serialize(Mode.FastForward));
        Assert.Equal(Mode.FastForward, MnvtJson.Deserialize<Mode>("\"fastForward\""));
        Assert.ThrowsAny<JsonException>(() => MnvtJson.Deserialize<Mode>("1"));
    }

    [Fact]
    public void Dates_AreNotAutoParsed()
    {
        var holder = MnvtJson.Deserialize<Holder>("""{"payload":"2026-01-01T00:00:00Z"}""");
        Assert.IsType<string>(holder.Payload);
    }
}
