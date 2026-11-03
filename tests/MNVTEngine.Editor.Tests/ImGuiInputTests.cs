using MNVTEngine.Editor.ImGuiBackend;

namespace MNVTEngine.Editor.Tests;

public class ImGuiInputTests
{
    [Fact]
    public void EveryEngineKey_MapsToAnImGuiKey() => Assert.Empty(ImGuiInput.UnmappedKeys);
}
