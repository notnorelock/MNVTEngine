using MNVTEngine.Mathematics;

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
    public void TextInput_AccumulatesPerFrame()
    {
        var input = new InputState();
        input.AddText('h');
        input.AddText('i');
        Assert.Equal("hi", input.TextInput);
        input.BeginFrame();
        Assert.Equal("", input.TextInput);
    }

    [Fact]
    public void UnknownKey_IsIgnored()
    {
        var input = new InputState();
        input.SetKey(Key.Unknown, true);
        Assert.False(input.IsKeyDown(Key.Unknown));
    }

    [Fact]
    public void Capture_HidesKeyboardAndMouseFromGameplay_UntilReleased()
    {
        var input = new InputState();
        input.BeginFrame();
        input.SetKey(Key.A, true);
        input.SetMouseButton(MouseButton.Left, true);
        input.AddScroll(new Vector2(0, 1));
        input.AddText('a');

        input.Capture(keyboard: true, mouse: false);
        Assert.False(input.IsKeyDown(Key.A));
        Assert.False(input.WasKeyPressed(Key.A));
        Assert.Equal("", input.TextInput);
        Assert.Equal(0f, input.GetAxis("Horizontal") + (input.IsKeyDown(Key.D) ? 1 : 0));
        Assert.True(input.IsMouseButtonDown(MouseButton.Left));

        input.Capture(keyboard: false, mouse: true);
        Assert.True(input.IsKeyDown(Key.A));
        Assert.False(input.IsMouseButtonDown(MouseButton.Left));
        Assert.Equal(Vector2.Zero, input.ScrollDelta);

        input.ReleaseCapture();
        Assert.True(input.WasKeyPressed(Key.A) && input.IsMouseButtonDown(MouseButton.Left));
        Assert.Equal(new Vector2(0, 1), input.ScrollDelta);
    }

    [Fact]
    public void SuppressedKey_StaysHiddenUntilReleased()
    {
        var input = new InputState();
        input.BeginFrame();
        input.SetKey(Key.F1, true);
        input.SetKey(Key.A, true);
        input.Suppress(Key.F1);
        Assert.False(input.WasKeyPressed(Key.F1));
        Assert.True(input.IsKeyDown(Key.A));

        input.BeginFrame(); // still held
        Assert.False(input.IsKeyDown(Key.F1));
        input.SetKey(Key.F1, false);
        input.BeginFrame();
        input.SetKey(Key.F1, true); // a new press is visible again
        Assert.True(input.WasKeyPressed(Key.F1));
    }

    [Fact]
    public void EveryEngineKey_MapsToASilkKey() => Assert.Empty(SilkInputBridge.UnmappedKeys);
}
