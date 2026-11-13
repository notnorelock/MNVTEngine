using Newtonsoft.Json.Linq;

namespace MNVTEngine.Assets.Tests;

public class AudioImporterTests
{
    [Fact]
    public void LoadType_IsKept_WhenValid_AndRemovedWithAProblemOtherwise()
    {
        var importer = new AudioImporter();
        var problems = new List<string>();
        Assert.Equal("streaming", importer.NormalizeSettings(JObject.Parse("""{ "loadType": "streaming" }"""), problems)["loadType"]!.ToString());
        Assert.Empty(problems);

        var normalized = importer.NormalizeSettings(JObject.Parse("""{ "loadType": "sometimes", "volume": 2 }"""), problems);
        Assert.False(normalized.ContainsKey("loadType"));
        Assert.Equal(2, problems.Count);
        Assert.Equal([".wav", ".ogg"], importer.Extensions);
    }
}
