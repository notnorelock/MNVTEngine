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
