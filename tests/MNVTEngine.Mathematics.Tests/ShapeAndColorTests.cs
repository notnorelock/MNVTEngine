namespace MNVTEngine.Mathematics.Tests;

public class ShapeAndColorTests
{
    [Fact]
    public void Rect_ContainsIsHalfOpen()
    {
        var r = new Rect(0f, 0f, 10f, 10f);
        Assert.True(r.Contains(Vector2.Zero));
        Assert.True(r.Contains(new Vector2(5f, 5f)));
        Assert.False(r.Contains(new Vector2(10f, 5f)));
        Assert.False(r.Contains(new Vector2(-0.1f, 5f)));
    }

    [Fact]
    public void Rect_NegativeSizeNormalizesMinMax()
    {
        var r = new Rect(10f, 10f, -10f, -10f);
        Assert.Equal(Vector2.Zero, r.Min);
        Assert.Equal(new Vector2(10f, 10f), r.Max);
        Assert.True(r.Contains(new Vector2(5f, 5f)));
    }

    [Fact]
    public void Rect_TouchingEdgesDoNotOverlap()
    {
        var a = new Rect(0f, 0f, 10f, 10f);
        Assert.False(a.Overlaps(new Rect(10f, 0f, 5f, 5f)));
        Assert.True(a.Overlaps(new Rect(9f, 9f, 5f, 5f)));
    }

    [Fact]
    public void Rect_TryIntersect()
    {
        var a = new Rect(0f, 0f, 10f, 10f);
        Assert.True(a.TryIntersect(new Rect(5f, 5f, 10f, 10f), out var i));
        Assert.Equal(new Rect(5f, 5f, 5f, 5f), i);
        Assert.False(a.TryIntersect(new Rect(20f, 20f, 1f, 1f), out _));
    }

    [Fact]
    public void Rect_Union() =>
        Assert.Equal(new Rect(0f, 0f, 20f, 15f), Rect.Union(new Rect(0f, 0f, 10f, 10f), new Rect(15f, 5f, 5f, 10f)));

    [Fact]
    public void Bounds_MinMaxAndContainment()
    {
        var b = new Bounds(Vector3.Zero, new Vector3(2f, 4f, 6f));
        Assert.Equal(new Vector3(-1f, -2f, -3f), b.Min);
        Assert.Equal(new Vector3(1f, 2f, 3f), b.Max);
        Assert.True(b.Contains(new Vector3(1f, 2f, 3f)));
        Assert.False(b.Contains(new Vector3(1.1f, 0f, 0f)));
    }

    [Fact]
    public void Bounds_IntersectsIncludesTouching()
    {
        var a = Bounds.FromMinMax(Vector3.Zero, Vector3.One);
        Assert.True(a.Intersects(Bounds.FromMinMax(Vector3.One, new Vector3(2f, 2f, 2f))));
        Assert.False(a.Intersects(Bounds.FromMinMax(new Vector3(1.1f, 0f, 0f), new Vector3(2f, 1f, 1f))));
    }

    [Fact]
    public void Bounds_EncapsulateAndExpand()
    {
        var b = new Bounds(Vector3.Zero, Vector3.Zero);
        b.Encapsulate(new Vector3(2f, -2f, 1f));
        Assert.Equal(new Vector3(0f, -2f, 0f), b.Min);
        Assert.Equal(new Vector3(2f, 0f, 1f), b.Max);
        b.Expand(2f);
        Assert.Equal(new Vector3(4f, 4f, 3f), b.Size);
    }

    [Fact]
    public void Bounds_ClosestPoint() =>
        Assert.Equal(new Vector3(1f, 0f, -1f), new Bounds(Vector3.Zero, new Vector3(2f, 2f, 2f)).ClosestPoint(new Vector3(5f, 0f, -9f)));

    [Fact]
    public void Bounds_IntersectRay()
    {
        var b = new Bounds(Vector3.Zero, new Vector3(2f, 2f, 2f));
        Assert.True(b.IntersectRay(new Ray(new Vector3(-5f, 0f, 0f), Vector3.UnitX), out var d));
        Assert.True(Mathf.Approximately(4f, d));
        Assert.False(b.IntersectRay(new Ray(new Vector3(-5f, 0f, 0f), -Vector3.UnitX), out _));
        Assert.False(b.IntersectRay(new Ray(new Vector3(-5f, 3f, 0f), Vector3.UnitX), out _));
        Assert.True(b.IntersectRay(new Ray(Vector3.Zero, Vector3.UnitY), out d));
        Assert.Equal(0f, d);
    }

    [Fact]
    public void Ray_NormalizesDirection()
    {
        var r = new Ray(Vector3.Zero, new Vector3(0f, 10f, 0f));
        Assert.Equal(Vector3.UnitY, r.Direction);
        Assert.Equal(new Vector3(0f, 3f, 0f), r.GetPoint(3f));
    }

    [Theory]
    [InlineData("#FF0000", 1f, 0f, 0f, 1f)]
    [InlineData("00FF0080", 0f, 1f, 0f, 128f / 255f)]
    [InlineData("#00f", 0f, 0f, 1f, 1f)]
    public void Color_TryParseHex(string hex, float r, float g, float b, float a)
    {
        Assert.True(Color.TryParseHex(hex, out var c));
        Assert.True(Color.Approximately(new Color(r, g, b, a), c));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void Color_TryParseHex_RejectsInvalid(string? hex) => Assert.False(Color.TryParseHex(hex, out _));

    [Fact]
    public void Color_HexRoundTrip()
    {
        Assert.True(Color.TryParseHex("#1A2B3C4D", out var c));
        Assert.Equal("#1A2B3C4D", c.ToHex());
    }

    [Fact]
    public void Color_Rgba32IsRgbaByteOrder()
    {
        Assert.Equal(0xFF0000FFu, Color.Red.ToRgba32());
        Assert.Equal(0x00000000u, Color.Clear.ToRgba32());
        Assert.Equal(0xFFFFFFFFu, new Color(2f, 2f, 2f, 2f).ToRgba32());
    }

    [Fact]
    public void Color_Arithmetic()
    {
        Assert.Equal(new Color(0.5f, 0.5f, 0.5f, 1f), Color.Lerp(Color.Black, Color.White, 0.5f));
        Assert.Equal(Color.Red, Color.White * Color.Red);
        Assert.Equal(0.25f, Color.White.WithAlpha(0.25f).A);
    }
}
