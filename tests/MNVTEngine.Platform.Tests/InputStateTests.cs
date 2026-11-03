using MNVTEngine.Mathematics;
using MNVTEngine.Platform.Input;

namespace MNVTEngine.Platform.Tests;

public class InputStateTests
{
    [Fact]
    public void Press_IsAnEdgeForOneFrame()
    {
        var input = new InputState();
        input.BeginFrame();
        input.SetKey(Key.Space, true);
        Assert.True(input.IsKeyDown(Key.Space));
        Assert.True(input.WasKeyPressed(Key.Space));

        input.BeginFrame();
        Assert.True(input.IsKeyDown(Key.Space));
        Assert.False(input.WasKeyPressed(Key.Space));

        input.SetKey(Key.Space, false);
        Assert.False(input.IsKeyDown(Key.Space));
        Assert.True(input.WasKeyReleased(Key.Space));
    }

    [Fact]
    public void PressAndReleaseInOneFrame_ReportsBothEdges()
    {
        var input = new InputState();
        input.SetKey(Key.A, true);
        input.SetKey(Key.A, false);
        Assert.True(input.WasKeyPressed(Key.A));
        Assert.True(input.WasKeyReleased(Key.A));
        Assert.False(input.IsKeyDown(Key.A));
    }

    [Fact]
    public void KeyRepeat_DoesNotProduceNewPress()
    {
        var input = new InputState();
        input.SetKey(Key.W, true);
        input.BeginFrame();
        input.SetKey(Key.W, true);
        Assert.False(input.WasKeyPressed(Key.W));
    }

    [Fact]
    public void Axes_CombineKeys()
    {
        var input = new InputState();
        Assert.Equal(0f, input.GetAxis("Horizontal"));
        input.SetKey(Key.D, true);
        Assert.Equal(1f, input.GetAxis("horizontal"));
        input.SetKey(Key.Left, true);
        Assert.Equal(0f, input.GetAxis("Horizontal"));
        input.SetKey(Key.D, false);
        Assert.Equal(-1f, input.GetAxis("Horizontal"));
        input.SetKey(Key.W, true);
        Assert.Equal(1f, input.GetAxis("Vertical"));
    }

    [Fact]
    public void CustomAxis_AndUnknownAxisError()
    {
        var input = new InputState();
        input.DefineAxis("Zoom", new InputAxis([Key.Minus], [Key.Equal]));
        input.SetKey(Key.Equal, true);
        Assert.Equal(1f, input.GetAxis("Zoom"));
        var ex = Assert.Throws<KeyNotFoundException>(() => input.GetAxis("Jump"));
        Assert.Contains("Zoom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mouse_DeltaAndScrollArePerFrame()
    {
        var input = new InputState();
        input.SetMousePosition(new Vector2(10f, 10f));
        input.BeginFrame();
        input.SetMousePosition(new Vector2(15f, 7f));
        input.AddScroll(new Vector2(0f, 1f));
        input.AddScroll(new Vector2(0f, 1f));
        Assert.Equal(new Vector2(0f, 2f), input.ScrollDelta);

        input.BeginFrame();
        Assert.Equal(new Vector2(5f, -3f), input.MouseDelta);
        Assert.Equal(Vector2.Zero, input.ScrollDelta);
    }

    [Fact]
    public void MouseButtons_HaveEdges()
    {
        var input = new InputState();
        input.SetMouseButton(MouseButton.Right, true);
        Assert.True(input.WasMouseButtonPressed(MouseButton.Right));
        Assert.True(input.IsMouseButtonDown(MouseButton.Right));
    }

    [Fact]
    public void ReleaseAll_ReleasesHeldKeysWithEdges()
    {
        var input = new InputState();
        input.SetKey(Key.ShiftLeft, true);
        input.SetMouseButton(MouseButton.Left, true);
        input.BeginFrame();
        input.ReleaseAll();
        Assert.False(input.IsKeyDown(Key.ShiftLeft));
        Assert.True(input.WasKeyReleased(Key.ShiftLeft));
        Assert.False(input.IsMouseButtonDown(MouseButton.Left));
    }

    [Fact]
    public void UnknownKey_IsIgnored()
    {
        var input = new InputState();
        input.SetKey(Key.Unknown, true);
        Assert.False(input.IsKeyDown(Key.Unknown));
    }

    [Fact]
    public void EveryEngineKey_MapsToASilkKey() => Assert.Empty(SilkInputBridge.UnmappedKeys);
}
