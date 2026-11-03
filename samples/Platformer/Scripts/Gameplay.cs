using MNVTEngine;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace Platformer;

/// <summary>
/// Physics-driven player: A/D or arrows run, Space/W/Up jumps when standing on something. Picks idle, run, jump and
/// fall animations from its velocity, and respawns at the start after falling out of the level.
/// </summary>
[ComponentId("Platformer.Player")]
public sealed class Player : Component
{
    public float Speed = 6f;
    public float JumpSpeed = 13f;

    [AssetType(AssetTypes.Animation)]
    public AssetRef Idle;

    [AssetType(AssetTypes.Animation)]
    public AssetRef Run;

    [AssetType(AssetTypes.Animation)]
    public AssetRef Jump;

    [AssetType(AssetTypes.Animation)]
    public AssetRef Fall;

    private Rigidbody2D _body = null!;
    private SpriteAnimator? _animator;
    private SpriteRenderer? _sprite;
    private AudioSource? _audio;
    private Vector2 _spawn;
    private bool _jumpRequested;

    /// <summary>Standing on a solid collider (two short rays below the feet).</summary>
    public bool Grounded { get; private set; }

    public override void Start()
    {
        _body = GameObject.GetComponent<Rigidbody2D>()!;
        _animator = GameObject.GetComponent<SpriteAnimator>();
        _sprite = GameObject.GetComponent<SpriteRenderer>();
        _audio = GameObject.GetComponent<AudioSource>();
        _spawn = Transform.Position;
    }

    public override void Update()
    {
        // Key presses are seen in Update; physics applies them in the next FixedUpdate.
        if (Input.GetKeyDown(Key.Space) || Input.GetKeyDown(Key.W) || Input.GetKeyDown(Key.Up))
        {
            _jumpRequested = true;
        }

        var velocity = _body.Velocity;
        var input = Input.GetAxis("Horizontal");
        if (_sprite is not null && MathF.Abs(input) > 0.01f)
        {
            _sprite.FlipX = input < 0f;
        }

        _animator?.Play(!Grounded ? (velocity.Y > 0f ? Jump : Fall) : MathF.Abs(velocity.X) > 0.1f ? Run : Idle);

        if (Transform.Position.Y < -12f)
        {
            Transform.Position = _spawn; // teleports the body
            _body.Velocity = Vector2.Zero;
        }
    }

    public override void FixedUpdate()
    {
        Grounded = Probe(-0.22f) || Probe(0.22f);
        var velocity = _body.Velocity;
        velocity.X = Input.GetAxis("Horizontal") * Speed;
        if (_jumpRequested && Grounded)
        {
            velocity.Y = JumpSpeed;
            _audio?.Play();
        }

        _jumpRequested = false;
        _body.Velocity = velocity;
    }

    private bool Probe(float dx)
    {
        // The collider's bottom is 0.5 below the center; start just inside it and look a little further down.
        var origin = Transform.Position + new Vector2(dx, -0.45f);
        return Physics2D.Raycast(origin, Vector2.Down, 0.1f) is { } hit && hit.Collider.GameObject != GameObject;
    }
}

/// <summary>Collected when the player touches it: tells the mission and plays a sound.</summary>
[ComponentId("Platformer.Coin")]
public sealed class Coin : Component, ITriggerHandler2D
{
    [AssetType(AssetTypes.Audio)]
    public AssetRef Sound;

    public void OnTriggerEnter2D(Collider2D other)
    {
        if (other.GameObject.GetComponent<Player>() is null)
        {
            return;
        }

        Mission.Raise(GameObject.Scene, "coin");
        GameObject.Scene.Find("Sfx")?.GetComponent<AudioSource>()?.PlayOneShot(Sound);
        GameObject.Scene.Destroy(GameObject);
    }
}

/// <summary>The flag (shown by the mission once every coin is collected).</summary>
[ComponentId("Platformer.Goal")]
public sealed class Goal : Component, ITriggerHandler2D
{
    public void OnTriggerEnter2D(Collider2D other)
    {
        if (other.GameObject.GetComponent<Player>() is not null)
        {
            Mission.Raise(GameObject.Scene, "reached_flag");
        }
    }
}

/// <summary>Smoothly follows a target, clamped to the level.</summary>
[ComponentId("Platformer.CameraFollow")]
public sealed class CameraFollow : Component
{
    public GameObject? Target;
    public float Sharpness = 5f;
    public float MinX = float.NegativeInfinity;
    public float MaxX = float.PositiveInfinity;
    public float MinY = float.NegativeInfinity;

    public override void LateUpdate()
    {
        if (Target is null || Target.IsDestroyed)
        {
            return;
        }

        var goal = new Vector2(Math.Clamp(Target.Transform.Position.X, MinX, MaxX), MathF.Max(Target.Transform.Position.Y + 1f, MinY));
        Transform.Position = Vector2.Lerp(Transform.Position, goal, 1f - MathF.Exp(-Sharpness * Time.DeltaTime));
    }
}

internal static class Mission
{
    /// <summary>Delivers a game event to the level's mission script.</summary>
    public static void Raise(Scene scene, string name)
    {
        foreach (var runner in scene.FindObjectsOfType<MissionRunner>())
        {
            if (runner.Vm is not null)
            {
                runner.RaiseEvent(name);
            }
        }
    }
}
