using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Services;

/// <summary>Scene queries used by editor views: picking objects under the cursor and their outlines.</summary>
public static class SceneEditing
{
    /// <summary>
    /// Topmost object at <paramref name="world"/>: sprites by their rotated quad (highest sorting order wins, then the
    /// most recently created); objects without sprites within <paramref name="pickRadius"/> of their position.
    /// </summary>
    public static GameObject? Pick(Scene scene, Vector2 world, float pickRadius)
    {
        ArgumentNullException.ThrowIfNull(scene);
        GameObject? best = null;
        var bestOrder = int.MinValue;

        foreach (var gameObject in scene.GameObjects)
        {
            if (!gameObject.ActiveInHierarchy)
            {
                continue;
            }

            int order;
            if (gameObject.GetComponent<SpriteRenderer>() is { } sprite)
            {
                if (!SpriteContains(sprite, world))
                {
                    continue;
                }

                order = sprite.SortingOrder;
            }
            else if (Vector2.Distance((Vector2)gameObject.Transform.Position, world) <= pickRadius)
            {
                order = int.MaxValue; // markers for empty objects sit above sprites
            }
            else
            {
                continue;
            }

            if (order >= bestOrder)
            {
                best = gameObject;
                bestOrder = order;
            }
        }

        return best;
    }

    /// <summary>Corners of the sprite's quad in world space: bottom-left, bottom-right, top-right, top-left.</summary>
    public static Vector2[] SpriteCorners(SpriteRenderer sprite)
    {
        ArgumentNullException.ThrowIfNull(sprite);
        float x0 = -sprite.Pivot.X * sprite.Size.X, y0 = -sprite.Pivot.Y * sprite.Size.Y;
        float x1 = x0 + sprite.Size.X, y1 = y0 + sprite.Size.Y;
        var t = sprite.Transform;
        return [(Vector2)t.TransformPoint(new Vector2(x0, y0)), (Vector2)t.TransformPoint(new Vector2(x1, y0)), (Vector2)t.TransformPoint(new Vector2(x1, y1)), (Vector2)t.TransformPoint(new Vector2(x0, y1))];
    }

    public static bool SpriteContains(SpriteRenderer sprite, Vector2 world)
    {
        ArgumentNullException.ThrowIfNull(sprite);
        var scale = sprite.Transform.LossyScale;
        if (scale.X == 0f || scale.Y == 0f)
        {
            return false;
        }

        var local = sprite.Transform.InverseTransformPoint(world);
        var min = -Vector2.Scale(sprite.Pivot, sprite.Size);
        var max = min + sprite.Size;
        return local.X >= MathF.Min(min.X, max.X) && local.X <= MathF.Max(min.X, max.X)
            && local.Y >= MathF.Min(min.Y, max.Y) && local.Y <= MathF.Max(min.Y, max.Y);
    }

    /// <summary>Deletes an object (and children) from the edit scene; immediate because the edit scene never runs.</summary>
    public static void Delete(EditorSession session, GameObject gameObject)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(gameObject);
        if (session.Selection.IsSelected(gameObject))
        {
            session.Selection.Clear();
        }

        gameObject.Destroy();
        session.Edited("Delete");
    }
}
