using MNVTEngine.Mathematics;

namespace MNVTEngine.Scenes.Tests;

/// <summary>Records every lifecycle callback into a shared log as "Name.Callback".</summary>
public class Recorder : Component
{
    public static List<string> Log { get; } = [];

    public string Label = "R";

    public override void Awake() => Log.Add($"{Label}.Awake");

    public override void OnEnable() => Log.Add($"{Label}.OnEnable");

    public override void Start() => Log.Add($"{Label}.Start");

    public override void FixedUpdate() => Log.Add($"{Label}.FixedUpdate");

    public override void Update() => Log.Add($"{Label}.Update");

    public override void LateUpdate() => Log.Add($"{Label}.LateUpdate");

    public override void OnDisable() => Log.Add($"{Label}.OnDisable");

    public override void OnDestroy() => Log.Add($"{Label}.OnDestroy");
}

public sealed class OtherRecorder : Recorder;

/// <summary>The example from the engine spec.</summary>
public sealed class PlayerController : Component
{
    public float Speed = 5f;

    public override void Update()
    {
        var movement = Input.GetAxis("Horizontal");
        Transform.Position += new Vector2(movement * Speed * Time.DeltaTime, 0f);
    }
}

public sealed class Thrower : Component
{
    public int Updates;

    public override void Update()
    {
        Updates++;
        throw new InvalidOperationException("boom");
    }
}

public sealed class Inspector : Component
{
    public GameObject? Target;
    public Transform? TargetTransform;
    public PlayerController? Controller;
    public List<GameObject> Group = [];

    [SerializeField]
    private int _secret = 7;

    [DontSerialize]
    public int Transient = 3;

    public int Secret
    {
        get => _secret;
        set => _secret = value;
    }

    public Color Tint = Color.White;
    public Vector2 Offset;
    public Mode Kind;
}

public enum Mode
{
    Idle,
    Running,
}
