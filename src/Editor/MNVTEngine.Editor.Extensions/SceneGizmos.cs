// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-27.

using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Extensions;

/// <summary>
/// Draws over the 2D Scene view, in world units, every frame while the view's gizmos are on: what the game's data means
/// but nothing renders (collision cells, trigger areas, spawn points). One instance per type and generation of code.
/// </summary>
public abstract class SceneGizmos
{
    private IEditorServices? _editor;

    /// <exception cref="InvalidOperationException">Read before the editor attached the gizmos.</exception>
    protected IEditorServices Editor => _editor ?? throw new InvalidOperationException("The gizmos are not attached to the editor yet.");

    /// <summary>Draws the gizmos of <see cref="IEditorServices.Scene"/>.</summary>
    public abstract void OnDrawGizmos(ISceneGizmoDrawer draw);

    /// <summary>Called by the editor.</summary>
    public void Attach(IEditorServices editor) => _editor = editor ?? throw new ArgumentNullException(nameof(editor));
}

/// <summary>Shapes in world units over the Scene view; lines keep one screen pixel and a half at any zoom.</summary>
public interface ISceneGizmoDrawer
{
    /// <summary>World units per screen pixel at the view's zoom.</summary>
    float UnitsPerPixel { get; }

    void Line(Vector2 a, Vector2 b, Color color);

    /// <summary>A rectangle's outline.</summary>
    void Rectangle(Rect rect, Color color);

    /// <summary>A filled rectangle (use a translucent colour to keep what is under it visible).</summary>
    void FillRectangle(Rect rect, Color color);
}
