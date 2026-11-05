using Microsoft.Extensions.Logging;
using MNVTEngine.Serialization;

namespace MNVTEngine;

/// <summary>
/// Drives the <see cref="ModelAnimator"/> of its GameObject with a state machine (<see cref="Controller"/>, a
/// <c>.mnvtanimator</c> asset, or <see cref="Use"/> from code), like Unity's Animator Controller. Game code sets
/// parameters (<see cref="SetFloat"/>, <see cref="SetBool"/>, <see cref="SetInteger"/>, <see cref="SetTrigger"/>);
/// every frame the first transition of the current state (then of "any state") whose conditions hold crossfades to
/// its target state. Entering a state sets the animator's clip, speed and looping.
/// </summary>
[ComponentId("MNVTEngine.ModelAnimatorController")]
public sealed partial class ModelAnimatorController : Component
{
    private readonly Dictionary<string, float> _values = new(StringComparer.Ordinal);
    private bool _loaded;
    private bool _warnedNoAnimator;

    /// <summary>The state machine asset.</summary>
    [SerializeField]
    [AssetType(AssetTypes.AnimatorController)]
    public AssetRef Controller { get; set; }

    /// <summary>The state machine in use: loaded from <see cref="Controller"/> on start, or set with <see cref="Use"/>.</summary>
    public AnimatorControllerDefinition? Definition { get; private set; }

    public string? CurrentState { get; private set; }

    /// <summary>Seconds since the current state was entered (scaled by its speed).</summary>
    public float StateTime { get; private set; }

    /// <summary><see cref="StateTime"/> in clip lengths: 1 is the end of the state's first cycle.</summary>
    public float NormalizedTime => Animator is { Duration: > 0f } animator ? StateTime / animator.Duration : 0f;

    /// <summary>Raised on every state change, with the state left (null at start) and the one entered.</summary>
    public event Action<ModelAnimatorController, string?, string>? StateChanged;

    private ModelAnimator? Animator => GameObject.GetComponent<ModelAnimator>();

    /// <summary>The animator plays what the states say, not its own first clip.</summary>
    public override void Awake()
    {
        if (Animator is { } animator)
        {
            animator.PlayOnStart = false;
        }
    }

    public override void Start()
    {
        if (Definition is null && !_loaded)
        {
            _loaded = true;
            Load();
        }

        if (Definition is { } definition && CurrentState is null)
        {
            Enter(definition.DefaultState, 0f);
        }
    }

    /// <summary>Switches to a state machine built in code (or parsed elsewhere): parameters reset, the default state plays.</summary>
    public void Use(AnimatorControllerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        _loaded = true;
        ResetParameters();
        CurrentState = null;
        if (GameObject.Scene.IsRunning)
        {
            Enter(definition.DefaultState, 0f);
        }
    }

    public override void Update()
    {
        if (Definition is not { } definition || CurrentState is not { } current)
        {
            return;
        }

        StateTime += Time.DeltaTime * MathF.Abs(definition.FindState(current)?.Speed ?? 1f);
        foreach (var transition in definition.Transitions.Where(t => t.From == current).Concat(definition.Transitions.Where(t => t.From is null && t.To != current)))
        {
            if ((transition.ExitTime is not { } exit || NormalizedTime >= exit) && transition.Conditions.All(Holds))
            {
                foreach (var trigger in transition.Conditions.Where(c => definition.FindParameter(c.Parameter)?.Type == AnimatorParameterType.Trigger))
                {
                    _values[trigger.Parameter] = 0f; // consumed by the transition
                }

                Enter(transition.To, transition.Duration);
                break;
            }
        }
    }

    /// <summary>Goes to a state now (crossfading over <paramref name="fade"/> seconds), whatever the transitions say.</summary>
    /// <exception cref="ArgumentException">No such state.</exception>
    public void Play(string state, float fade = 0f)
    {
        if (Definition?.FindState(state) is null)
        {
            throw new ArgumentException($"The controller has no state '{state}'.", nameof(state));
        }

        Enter(state, fade);
    }

    public void SetFloat(string parameter, float value) => Set(parameter, value, AnimatorParameterType.Float);

    public void SetInteger(string parameter, int value) => Set(parameter, value, AnimatorParameterType.Int);

    public void SetBool(string parameter, bool value) => Set(parameter, value ? 1f : 0f, AnimatorParameterType.Bool);

    /// <summary>Sets a trigger until a transition uses it.</summary>
    public void SetTrigger(string parameter) => Set(parameter, 1f, AnimatorParameterType.Trigger);

    public void ResetTrigger(string parameter) => Set(parameter, 0f, AnimatorParameterType.Trigger);

    public float GetFloat(string parameter) => Get(parameter);

    public int GetInteger(string parameter) => (int)Get(parameter);

    public bool GetBool(string parameter) => Get(parameter) != 0f;

    private float Get(string parameter) =>
        _values.TryGetValue(parameter, out var value) ? value : Definition?.FindParameter(parameter)?.Default ?? 0f;

    /// <exception cref="ArgumentException">The loaded controller has no such parameter, or it is of another type.</exception>
    private void Set(string parameter, float value, AnimatorParameterType type)
    {
        ArgumentException.ThrowIfNullOrEmpty(parameter);
        if (Definition is { } definition)
        {
            var known = definition.FindParameter(parameter) ?? throw new ArgumentException($"The controller has no parameter '{parameter}'.", nameof(parameter));
            if (known.Type != type)
            {
                throw new ArgumentException($"'{parameter}' is a {known.Type.ToString().ToLowerInvariant()} parameter, not a {type.ToString().ToLowerInvariant()}.", nameof(parameter));
            }
        }

        _values[parameter] = value; // before the controller loads, kept for when it does
    }

    private bool Holds(AnimatorCondition condition)
    {
        var value = Get(condition.Parameter);
        return condition.Mode switch
        {
            AnimatorConditionMode.Greater => value > condition.Threshold,
            AnimatorConditionMode.Less => value < condition.Threshold,
            AnimatorConditionMode.Equals => value == condition.Threshold,
            AnimatorConditionMode.NotEquals => value != condition.Threshold,
            AnimatorConditionMode.IsTrue => value != 0f,
            _ => value == 0f,
        };
    }

    private void Enter(string state, float fade)
    {
        var target = Definition!.FindState(state)!;
        var previous = CurrentState;
        (CurrentState, StateTime) = (state, 0f);
        if (Animator is not { } animator)
        {
            if (!_warnedNoAnimator)
            {
                _warnedNoAnimator = true;
                LogNoAnimator(GameObject.Scene.Logger, GameObject.Name);
            }

            return;
        }

        (animator.Speed, animator.Loop) = (target.Speed, target.Loop);
        if (fade > 0f && previous is not null)
        {
            animator.CrossFade(target.Clip, fade);
        }
        else
        {
            animator.Play(target.Clip, restart: true);
        }

        StateChanged?.Invoke(this, previous, state);
    }

    private void ResetParameters()
    {
        var pending = new Dictionary<string, float>(_values, StringComparer.Ordinal);
        _values.Clear();
        foreach (var (name, value) in pending)
        {
            if (Definition?.FindParameter(name) is not null)
            {
                _values[name] = value; // set before the controller loaded
            }
            else
            {
                LogUnknownParameter(GameObject.Scene.Logger, GameObject.Name, name);
            }
        }
    }

    private void Load()
    {
        if (Controller.IsNone)
        {
            return;
        }

        try
        {
            var reader = GameObject.Scene.TextAssets ?? throw new InvalidOperationException("The scene has no text asset reader; the host must set Scene.TextAssets.");
            Definition = AnimatorControllerDefinition.Parse(reader.ReadText(Controller, AssetTypes.AnimatorController), Controller.ToString());
            ResetParameters();
        }
        catch (Exception ex) when (ex is InvalidOperationException or SerializationException)
        {
            LogControllerFailed(GameObject.Scene.Logger, ex, Controller.ToString(), GameObject.Name);
        }
    }

    [LoggerMessage(EventId = 1410, Level = LogLevel.Error, Message = "Animator controller {Controller} on '{Owner}' could not be loaded; the animator plays its own clip")]
    private static partial void LogControllerFailed(ILogger logger, Exception exception, string controller, string owner);

    [LoggerMessage(EventId = 1412, Level = LogLevel.Warning, Message = "ModelAnimatorController on '{Owner}': parameter '{Parameter}' was set before the controller loaded, but the controller has no such parameter")]
    private static partial void LogUnknownParameter(ILogger logger, string owner, string parameter);

    [LoggerMessage(EventId = 1411, Level = LogLevel.Error, Message = "ModelAnimatorController on '{Owner}' needs a ModelAnimator on the same GameObject")]
    private static partial void LogNoAnimator(ILogger logger, string owner);
}
