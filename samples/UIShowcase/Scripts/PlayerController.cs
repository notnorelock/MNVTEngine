// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-04.

using MNVTEngine;
using MNVTEngine.Mathematics;

namespace UIShowcase;

/// <summary>Moves with WASD or the arrow keys.</summary>
public sealed class PlayerController : Component
{
    public float Speed = 5f;

    public override void Update()
    {
        var movement = new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
        Transform.Position += movement.Normalized * Speed * Time.DeltaTime;
    }
}
