using MNVTEngine.Mathematics;
using MNVTEngine.Particles;
using MNVTEngine.Particles3D;

namespace MNVTEngine.Runtime.Tests;

public sealed class ParticleQuadTests
{
    private static readonly ParticleView View = new(new Vector3(0f, 0f, 10f), Vector3.Right, Vector3.Up);

    private static (Vector3 Right, Vector3 Up) Axes(ParticleRenderMode mode, Vector3 position, float rotation = 0f, Vector3 velocity = default) =>
        ParticleQuads.Axes(mode, View, position, new Vector2(2f, 2f), new Vector3(0f, 0f, rotation), velocity, 1f, 0.5f);

    [Fact]
    public void Billboards_FaceTheCamera_FromAnyPosition()
    {
        var position = new Vector3(4f, 3f, 0f);
        var (right, up) = Axes(ParticleRenderMode.Billboard, position);
        var toCamera = View.Position - position;
        Assert.Equal(0f, Vector3.Dot(right, toCamera), 3);
        Assert.Equal(0f, Vector3.Dot(up, toCamera), 3);
        Assert.Equal(1f, right.Magnitude, 3); // half of the 2-unit size
        Assert.Equal(1f, up.Magnitude, 3);
    }

    [Fact]
    public void OtherModes_OrientAsDocumented()
    {
        Assert.Equal((Vector3.Right, Vector3.Up), Axes(ParticleRenderMode.ViewAligned, new Vector3(4f, 3f, 0f)));
        var flat = Axes(ParticleRenderMode.HorizontalBillboard, Vector3.Zero);
        Assert.Equal(0f, flat.Right.Y + flat.Up.Y, 5); // lies on XZ
        var upright = Axes(ParticleRenderMode.VerticalBillboard, new Vector3(5f, 0f, 0f));
        Assert.Equal(Vector3.Up, upright.Up);

        var (side, along) = Axes(ParticleRenderMode.Stretched, Vector3.Zero, velocity: new Vector3(0f, 4f, 0f));
        Assert.Equal(new Vector3(0f, 2f, 0f), along); // (size 2 * length 1 + speed 4 * 0.5) / 2
        Assert.Equal(0f, Vector3.Dot(side, along), 4);

        var turned = Axes(ParticleRenderMode.ViewAligned, Vector3.Zero, rotation: 90f);
        Assert.True(Vector3.Distance(turned.Right, Vector3.Up) < 1e-4f && Vector3.Distance(turned.Up, Vector3.Left) < 1e-4f); // rotated counterclockwise
    }
}
