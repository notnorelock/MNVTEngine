using Microsoft.Extensions.Logging;
using MNVTEngine.Particles;
using MNVTEngine.Serialization;

namespace MNVTEngine;

/// <summary>
/// A particle emitter. Its settings are inline (<see cref="Settings"/>) or come from a <c>.mnvtparticle</c> asset
/// (<see cref="Asset"/>, which replaces the inline settings when the system starts). The particles live in
/// <see cref="Simulation"/>; the 2D and 3D particle modules draw them. Advanced by <see cref="Update"/> on scaled or
/// unscaled time (<see cref="MainModule.UseUnscaledTime"/>).
/// <code>
/// var fire = gameObject.AddComponent&lt;ParticleSystem&gt;();
/// fire.Emission.RateOverTime = 30;
/// fire.Emission.AddBurst(new ParticleBurst { Time = 0.5f, Count = 50 });
/// fire.Play();
/// </code>
/// </summary>
public sealed partial class ParticleSystem : Component
{
    /// <summary>The system's definition (ignored once an <see cref="Asset"/> has been loaded in its place).</summary>
    [SerializeField]
    public ParticleSystemSettings Settings { get; set; } = new();

    [SerializeField]
    [AssetType(AssetTypes.ParticleSystem)]
    private AssetRef _asset;

    private ParticleSimulation? _simulation;
    private AssetRef _loadedAsset;

    /// <summary>A <c>.mnvtparticle</c> asset; set at run time, it loads and restarts the system.</summary>
    public AssetRef Asset
    {
        get => _asset;
        set
        {
            _asset = value;
            if (_simulation is not null && GameObject is not null && LoadAsset())
            {
                Restart();
            }
        }
    }

    /// <summary>The live simulation (created on first use); it always runs the current <see cref="Settings"/>.</summary>
    public ParticleSimulation Simulation
    {
        get
        {
            var simulation = _simulation ??= CreateSimulation();
            simulation.Settings = Settings;
            return simulation;
        }
    }

    public MainModule Main => Settings.Main;

    public EmissionModule Emission => Settings.Emission;

    public ShapeModule Shape => Settings.Shape;

    public float Duration { get => Main.Duration; set => Main.Duration = value; }

    public bool Loop { get => Main.Loop; set => Main.Loop = value; }

    public int MaxParticles { get => Main.MaxParticles; set => Main.MaxParticles = value; }

    public bool PlayOnAwake { get => Main.PlayOnAwake; set => Main.PlayOnAwake = value; }

    public float StartDelay { get => Main.StartDelay; set => Main.StartDelay = value; }

    /// <summary>Time multiplier (<see cref="MainModule.SimulationSpeed"/>).</summary>
    public float PlaybackSpeed { get => Main.SimulationSpeed; set => Main.SimulationSpeed = value; }

    public bool IsPlaying => _simulation?.State == ParticlePlayState.Playing;

    public bool IsPaused => _simulation?.State == ParticlePlayState.Paused;

    public bool IsEmitting => _simulation?.IsEmitting ?? false;

    public bool IsAlive => IsPlaying || ParticleCount > 0;

    public int ParticleCount => _simulation?.Particles.Count ?? 0;

    /// <summary>Raised when a non-looping system has finished (after its stop action).</summary>
    public event Action<ParticleSystem>? Stopped;

    public void Play() => Simulation.Play();

    public void Pause() => Simulation.Pause();

    public void Stop(ParticleStopBehavior behavior = ParticleStopBehavior.StopEmitting) => Simulation.Stop(behavior);

    public void Clear() => Simulation.Clear();

    public void Restart() => Simulation.Restart();

    /// <summary>Emits <paramref name="count"/> particles now (up to the maximum), whether or not the system plays.</summary>
    public int Emit(int count) => Simulation.Emit(count, Pose());

    /// <summary>The emitter's transform for the simulation.</summary>
    public EmitterPose Pose()
    {
        var transform = Transform;
        return new EmitterPose(transform.LocalToWorldMatrix, transform.Rotation, transform.Position);
    }

    public override void Start()
    {
        if (Main.PlayOnAwake)
        {
            Simulation.Play();
        }
    }

    public override void Update()
    {
        if (_simulation is { } simulation && (simulation.State != ParticlePlayState.Stopped || simulation.Particles.Count > 0))
        {
            simulation.Settings = Settings;
            simulation.Step(Main.UseUnscaledTime ? Time.UnscaledDeltaTime : Time.DeltaTime, Pose());
        }
    }

    private ParticleSimulation CreateSimulation()
    {
        if (GameObject is not null)
        {
            LoadAsset();
        }

        var simulation = new ParticleSimulation(Settings);
        simulation.Finished += OnFinished;
        return simulation;
    }

    /// <summary>Replaces <see cref="Settings"/> with the asset's; false when there is no asset or it cannot be read (logged).</summary>
    private bool LoadAsset()
    {
        if (_asset.IsNone || _asset == _loadedAsset)
        {
            return false;
        }

        _loadedAsset = _asset;
        try
        {
            var reader = GameObject.Scene.TextAssets ?? throw new InvalidOperationException("The scene has no text asset reader; the host must set Scene.TextAssets.");
            Settings = ParticleSystemAsset.Parse(reader.ReadText(_asset, AssetTypes.ParticleSystem), _asset.ToString());
            if (_simulation is not null)
            {
                _simulation.Settings = Settings;
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SerializationException)
        {
            LogAssetFailed(GameObject.Scene.Logger, ex, _asset.ToString(), GameObject.Name);
            return false;
        }
    }

    private void OnFinished()
    {
        switch (Main.StopAction)
        {
            case ParticleStopAction.Disable:
                GameObject.SetActive(false);
                break;
            case ParticleStopAction.Destroy:
                GameObject.Destroy();
                break;
        }

        Stopped?.Invoke(this);
    }

    [LoggerMessage(EventId = 2400, Level = LogLevel.Error, Message = "Particle system {Asset} on '{Owner}' could not be loaded; the inline settings are used")]
    private static partial void LogAssetFailed(ILogger logger, Exception exception, string asset, string owner);
}
