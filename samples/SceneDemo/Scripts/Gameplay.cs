using MNVTEngine;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace SceneDemo;

/// <summary>Moves with WASD/arrows.</summary>
[ComponentId("SceneDemo.PlayerController")]
public sealed class PlayerController : Component
{
    public float Speed = 5f;

    public override void Update()
    {
        var movement = new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
        Transform.Position += movement.Normalized * Speed * Time.DeltaTime;
        if (movement != Vector2.Zero)
        {
            Transform.Rotation = Mathf.MoveTowardsAngle(Transform.Rotation, Vector2.SignedAngle(Vector2.Up, movement), 720f * Time.DeltaTime);
        }
    }
}

/// <summary>Rotates continuously; children orbit through the transform hierarchy.</summary>
[ComponentId("SceneDemo.Spinner")]
public sealed class Spinner : Component
{
    public float DegreesPerSecond = 45f;

    public override void Update() => Transform.LocalRotation += DegreesPerSecond * Time.DeltaTime;
}

/// <summary>Smoothly follows a target GameObject, assigned by reference in the scene file.</summary>
[ComponentId("SceneDemo.CameraFollow")]
public sealed class CameraFollow : Component
{
    public GameObject? Target;
    public float Sharpness = 6f;

    public override void LateUpdate()
    {
        if (Target is null || Target.IsDestroyed)
        {
            return;
        }

        Transform.Position = Vector2.Lerp(Transform.Position, Target.Transform.Position, 1f - MathF.Exp(-Sharpness * Time.DeltaTime));
    }
}

/// <summary>Follows the mouse cursor in world space.</summary>
[ComponentId("SceneDemo.MouseMarker")]
public sealed class MouseMarker : Component
{
    public override void Update()
    {
        if (GameObject.Scene.MainCamera is { } camera)
        {
            Transform.Position = camera.ScreenToWorld(Input.MousePosition);
        }
    }
}

/// <summary>Space spawns a copy of a prefab (a short-lived orb) at the player.</summary>
[ComponentId("SceneDemo.Spawner")]
public sealed class Spawner : Component
{
    [AssetType(AssetTypes.Prefab)]
    public AssetRef OrbPrefab;

    public override void Update()
    {
        if (Input.GetKeyDown(Key.Space) && !OrbPrefab.IsNone)
        {
            Prefab.Instantiate(OrbPrefab, Transform.Position);
        }
    }
}

[ComponentId("SceneDemo.Lifetime")]
public sealed class Lifetime : Component
{
    public float Seconds = 1f;
    private float _age;

    public override void Update()
    {
        _age += Time.DeltaTime;
        var t = Mathf.Clamp01(_age / Seconds);
        Transform.LocalScale = Vector2.One * (1f + t);
        if (GetComponent<SpriteRenderer>() is { } sprite)
        {
            sprite.Color = sprite.Color.WithAlpha(1f - t);
        }

        if (_age >= Seconds)
        {
            GameObject.Destroy();
        }
    }
}

/// <summary>Tab switches to another scene.</summary>
[ComponentId("SceneDemo.SceneSwitcher")]
public sealed class SceneSwitcher : Component
{
    [AssetType(AssetTypes.Scene)]
    public AssetRef NextScene;

    public override void Update()
    {
        if (Input.GetKeyDown(Key.Tab) && !NextScene.IsNone)
        {
            SceneManager.LoadScene(NextScene);
        }
    }
}
