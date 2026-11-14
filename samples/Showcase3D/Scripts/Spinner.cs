// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-04.

using MNVTEngine;
using MNVTEngine.Mathematics;

namespace Showcase3D;

/// <summary>Turns the object around its local axes (Euler degrees per second).</summary>
public sealed class Spinner : Component
{
    public Vector3 DegreesPerSecond = new(0f, 45f, 0f);

    public override void Update() => Transform.Rotate(DegreesPerSecond * Time.DeltaTime);
}
