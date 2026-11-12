using MNVTEngine.Browser;
using MNVTEngine.Mathematics;

namespace MNVTEngine;

/// <summary>
/// A browser page on this GameObject's <see cref="SpriteRenderer"/> (added when missing). The mouse is mapped through
/// the main (orthographic) camera onto the sprite's rectangle, with its pivot, rotation, scale and flips.
/// </summary>
public sealed class BrowserSurface2D : BrowserSurface
{
    protected override void ShowTexture(AssetRef texture)
    {
        var sprite = GameObject.GetComponent<SpriteRenderer>() ?? GameObject.AddComponent<SpriteRenderer>();
        sprite.Texture = texture;
    }

    protected override Vector2? PageUV(Vector2 screen)
    {
        if (GameObject.GetComponent<SpriteRenderer>() is not { } sprite || GameObject.Scene.MainCamera is not { } camera)
        {
            return null;
        }

        return SpriteUV(sprite, camera.ScreenToWorld(screen));
    }

    /// <summary>
    /// A world point on a sprite as page coordinates (u right, v down from the top), unclamped: outside 0..1 is off
    /// the sprite.
    /// </summary>
    public static Vector2 SpriteUV(SpriteRenderer sprite, Vector2 world)
    {
        ArgumentNullException.ThrowIfNull(sprite);
        var transform = sprite.Transform;
        var scale = (Vector2)transform.LossyScale;
        var size = Vector2.Scale(sprite.Size, scale);
        var offset = world - (Vector2)transform.Position;
        var angle = -transform.Rotation2D * Mathf.Deg2Rad;
        var (sin, cos) = MathF.SinCos(angle);
        var local = new Vector2((offset.X * cos) - (offset.Y * sin), (offset.X * sin) + (offset.Y * cos));
        var u = size.X != 0f ? (local.X / size.X) + sprite.Pivot.X : -1f;
        var up = size.Y != 0f ? (local.Y / size.Y) + sprite.Pivot.Y : -1f;
        if (sprite.FlipX)
        {
            u = 1f - u;
        }

        var v = 1f - up;
        if (sprite.FlipY)
        {
            v = 1f - v;
        }

        return new Vector2(u, v);
    }
}

/// <summary>The "browser.2d" engine module: <see cref="BrowserSurface2D"/>.</summary>
public sealed class Browser2DModule : Scenes.IEngineModule
{
    public string Id => "browser.2d";

    public void Register(Scenes.ComponentRegistry components)
    {
        ArgumentNullException.ThrowIfNull(components);
        components.Register<BrowserSurface2D>();
    }
}
