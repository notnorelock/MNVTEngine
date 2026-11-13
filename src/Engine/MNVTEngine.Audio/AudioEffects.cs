namespace MNVTEngine.Audio;

/// <summary>One insert effect on a mixer group: its type id (<see cref="AudioEffects"/>), on/off and parameter values.</summary>
public sealed class AudioEffectSettings
{
    public string Type { get; set; } = AudioEffects.LowPass;

    public bool Enabled { get; set; } = true;

    /// <summary>Values by parameter name; a missing one uses its default, values are clamped to the parameter's range.</summary>
    public Dictionary<string, float> Parameters { get; set; } = [];

    public float Get(AudioEffectParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return Parameters.TryGetValue(parameter.Name, out var value) && float.IsFinite(value) ? Math.Clamp(value, parameter.Min, parameter.Max) : parameter.Default;
    }
}

/// <summary>A parameter of an effect type, with its range and unit (for the mixer UI).</summary>
public sealed record AudioEffectParameter(string Name, string Label, float Min, float Max, float Default, string Unit = "", bool Logarithmic = false);

/// <summary>An effect type: its id, parameters and how to create it for a sample rate.</summary>
public sealed record AudioEffectDefinition(string Id, string DisplayName, IReadOnlyList<AudioEffectParameter> Parameters, Func<int, AudioEffect> Create)
{
    public AudioEffectParameter? Parameter(string name) => Parameters.FirstOrDefault(p => p.Name == name);

    public AudioEffectSettings CreateSettings() => new() { Type = Id, Parameters = Parameters.ToDictionary(p => p.Name, p => p.Default) };
}

/// <summary>
/// An insert effect instance on a bus: processes interleaved stereo blocks in place on the audio thread. It keeps its
/// state (filters, delay lines, reverb tails) while its parameters change.
/// </summary>
public abstract class AudioEffect
{
    /// <summary>Reads the parameters the effect needs (called before each block when they may have changed).</summary>
    public abstract void Configure(AudioEffectSettings settings, AudioEffectDefinition definition);

    /// <param name="stereo">Interleaved left/right samples, nominally -1 to 1.</param>
    public abstract void Process(Span<float> stereo);
}

/// <summary>The built-in insert effects. Settings name them by <see cref="AudioEffectDefinition.Id"/>.</summary>
public static class AudioEffects
{
    public const string LowPass = "lowpass", HighPass = "highpass", Eq3 = "eq3", Compressor = "compressor", Delay = "delay", Reverb = "reverb", Distortion = "distortion", Chorus = "chorus";

    private static readonly AudioEffectParameter Mix = new("mix", "Dry/Wet", 0f, 1f, 0.3f, "%");

    public static IReadOnlyList<AudioEffectDefinition> All { get; } =
    [
        new(LowPass, "Low Pass", [new("cutoff", "Cutoff", 20f, 20000f, 2000f, "Hz", true), new("resonance", "Resonance", 0.3f, 10f, 0.707f)], rate => new BiquadEffect(rate, BiquadKind.LowPass)),
        new(HighPass, "High Pass", [new("cutoff", "Cutoff", 20f, 20000f, 200f, "Hz", true), new("resonance", "Resonance", 0.3f, 10f, 0.707f)], rate => new BiquadEffect(rate, BiquadKind.HighPass)),
        new(Eq3, "EQ Three", [new("low", "Low", -24f, 12f, 0f, "dB"), new("mid", "Mid", -24f, 12f, 0f, "dB"), new("high", "High", -24f, 12f, 0f, "dB"), new("lowFrequency", "Low Freq", 50f, 1000f, 250f, "Hz", true), new("highFrequency", "High Freq", 1000f, 15000f, 4000f, "Hz", true)], rate => new Eq3Effect(rate)),
        new(Compressor, "Compressor", [new("threshold", "Threshold", -60f, 0f, -18f, "dB"), new("ratio", "Ratio", 1f, 20f, 4f, ":1"), new("attack", "Attack", 0.1f, 200f, 10f, "ms", true), new("release", "Release", 5f, 2000f, 120f, "ms", true), new("makeup", "Makeup", 0f, 24f, 0f, "dB")], rate => new CompressorEffect(rate)),
        new(Delay, "Delay", [new("time", "Time", 1f, 2000f, 350f, "ms", true), new("feedback", "Feedback", 0f, 0.95f, 0.4f, "%"), Mix], rate => new DelayEffect(rate)),
        new(Reverb, "Reverb", [new("size", "Size", 0f, 1f, 0.7f, "%"), new("damping", "Damping", 0f, 1f, 0.5f, "%"), new("width", "Width", 0f, 1f, 1f, "%"), Mix], rate => new ReverbEffect(rate)),
        new(Distortion, "Saturator", [new("drive", "Drive", 0f, 36f, 12f, "dB"), new("output", "Output", -24f, 0f, -6f, "dB"), new("mix", "Dry/Wet", 0f, 1f, 1f, "%")], _ => new DistortionEffect()),
        new(Chorus, "Chorus", [new("rate", "Rate", 0.05f, 5f, 0.8f, "Hz", true), new("depth", "Depth", 0f, 10f, 3f, "ms"), Mix with { Default = 0.5f }], rate => new ChorusEffect(rate)),
    ];

    public static AudioEffectDefinition? Find(string? id) => All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    internal static float DbToGain(float db) => MathF.Pow(10f, db / 20f);
}

internal enum BiquadKind
{
    LowPass,
    HighPass,
    LowShelf,
    HighShelf,
    Peak,
}

/// <summary>An RBJ cookbook biquad, one per channel (transposed direct form II).</summary>
internal struct Biquad
{
    private float _b0, _b1, _b2, _a1, _a2, _z1L, _z2L, _z1R, _z2R;

    public void Set(BiquadKind kind, int sampleRate, float frequency, float q, float gainDb = 0f)
    {
        var w = 2f * MathF.PI * Math.Clamp(frequency, 10f, sampleRate * 0.45f) / sampleRate;
        var (sin, cos) = MathF.SinCos(w);
        var alpha = sin / (2f * Math.Max(q, 0.05f));
        var a = MathF.Pow(10f, gainDb / 40f);
        float b0, b1, b2, a0, a1, a2;
        switch (kind)
        {
            case BiquadKind.LowPass:
                (b0, b1, b2, a0, a1, a2) = ((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
                break;
            case BiquadKind.HighPass:
                (b0, b1, b2, a0, a1, a2) = ((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
                break;
            case BiquadKind.Peak:
                (b0, b1, b2, a0, a1, a2) = (1 + (alpha * a), -2 * cos, 1 - (alpha * a), 1 + (alpha / a), -2 * cos, 1 - (alpha / a));
                break;
            default:
                var sqrtA = 2f * MathF.Sqrt(a) * alpha;
                (b0, b1, b2, a0, a1, a2) = kind == BiquadKind.LowShelf
                    ? (a * ((a + 1) - ((a - 1) * cos) + sqrtA), 2 * a * ((a - 1) - ((a + 1) * cos)), a * ((a + 1) - ((a - 1) * cos) - sqrtA), (a + 1) + ((a - 1) * cos) + sqrtA, -2 * ((a - 1) + ((a + 1) * cos)), (a + 1) + ((a - 1) * cos) - sqrtA)
                    : (a * ((a + 1) + ((a - 1) * cos) + sqrtA), -2 * a * ((a - 1) + ((a + 1) * cos)), a * ((a + 1) + ((a - 1) * cos) - sqrtA), (a + 1) - ((a - 1) * cos) + sqrtA, 2 * ((a - 1) - ((a + 1) * cos)), (a + 1) - ((a - 1) * cos) - sqrtA);
                break;
        }

        (_b0, _b1, _b2, _a1, _a2) = (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    public void Process(Span<float> stereo)
    {
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            var l = stereo[i];
            var y = (_b0 * l) + _z1L;
            _z1L = (_b1 * l) - (_a1 * y) + _z2L;
            _z2L = (_b2 * l) - (_a2 * y);
            stereo[i] = y;

            var r = stereo[i + 1];
            y = (_b0 * r) + _z1R;
            _z1R = (_b1 * r) - (_a1 * y) + _z2R;
            _z2R = (_b2 * r) - (_a2 * y);
            stereo[i + 1] = y;
        }
    }
}

internal sealed class BiquadEffect(int sampleRate, BiquadKind kind) : AudioEffect
{
    private Biquad _filter;

    public override void Configure(AudioEffectSettings settings, AudioEffectDefinition definition) =>
        _filter.Set(kind, sampleRate, settings.Get(definition.Parameters[0]), settings.Get(definition.Parameters[1]));

    public override void Process(Span<float> stereo) => _filter.Process(stereo);
}

internal sealed class Eq3Effect(int sampleRate) : AudioEffect
{
    private Biquad _low, _mid, _high;

    public override void Configure(AudioEffectSettings settings, AudioEffectDefinition definition)
    {
        float P(string name) => settings.Get(definition.Parameter(name)!);
        var (lowFrequency, highFrequency) = (P("lowFrequency"), P("highFrequency"));
        _low.Set(BiquadKind.LowShelf, sampleRate, lowFrequency, 0.707f, P("low"));
        _mid.Set(BiquadKind.Peak, sampleRate, MathF.Sqrt(lowFrequency * highFrequency), 0.7f, P("mid"));
        _high.Set(BiquadKind.HighShelf, sampleRate, highFrequency, 0.707f, P("high"));
    }

    public override void Process(Span<float> stereo)
    {
        _low.Process(stereo);
        _mid.Process(stereo);
        _high.Process(stereo);
    }
}

/// <summary>Feed-forward peak compressor, stereo-linked, with attack/release smoothing of the gain reduction.</summary>
internal sealed class CompressorEffect(int sampleRate) : AudioEffect
{
    private float _threshold, _ratio, _attack, _release, _makeup, _envelope;

    public override void Configure(AudioEffectSettings settings, AudioEffectDefinition definition)
    {
        float P(string name) => settings.Get(definition.Parameter(name)!);
        (_threshold, _ratio, _makeup) = (P("threshold"), P("ratio"), AudioEffects.DbToGain(P("makeup")));
        _attack = MathF.Exp(-1f / (P("attack") * 0.001f * sampleRate));
        _release = MathF.Exp(-1f / (P("release") * 0.001f * sampleRate));
    }

    public override void Process(Span<float> stereo)
    {
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            var level = Math.Max(Math.Abs(stereo[i]), Math.Abs(stereo[i + 1]));
            var db = 20f * MathF.Log10(Math.Max(level, 1e-6f));
            var over = Math.Max(0f, db - _threshold);
            var reduction = over - (over / _ratio); // dB to take off
            var coefficient = reduction > _envelope ? _attack : _release;
            _envelope = reduction + (coefficient * (_envelope - reduction));
            var gain = AudioEffects.DbToGain(-_envelope) * _makeup;
            stereo[i] *= gain;
            stereo[i + 1] *= gain;
        }
    }
}

internal sealed class DelayEffect(int sampleRate) : AudioEffect
{
    private readonly float[] _line = new float[(sampleRate * 2 + 1) * 2];
    private int _write, _delayFrames;
    private float _feedback, _mix;

    public override void Configure(AudioEffectSettings settings, AudioEffectDefinition definition)
    {
        _delayFrames = Math.Clamp((int)(settings.Get(definition.Parameter("time")!) * 0.001f * sampleRate), 1, (_line.Length / 2) - 1);
        (_feedback, _mix) = (settings.Get(definition.Parameter("feedback")!), settings.Get(definition.Parameter("mix")!));
    }

    public override void Process(Span<float> stereo)
    {
        var frames = _line.Length / 2;
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            var read = (_write - _delayFrames + frames) % frames;
            var (dl, dr) = (_line[read * 2], _line[(read * 2) + 1]);
            _line[_write * 2] = stereo[i] + (dl * _feedback);
            _line[(_write * 2) + 1] = stereo[i + 1] + (dr * _feedback);
            stereo[i] += (dl - stereo[i]) * _mix;
            stereo[i + 1] += (dr - stereo[i + 1]) * _mix;
            _write = (_write + 1) % frames;
        }
    }
}

/// <summary>Freeverb (Jezar's public-domain design): 8 damped combs and 4 allpasses per channel, tunings scaled to the rate.</summary>
internal sealed class ReverbEffect : AudioEffect
{
    private static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllpassTunings = [556, 441, 341, 225];
    private const int StereoSpread = 23;

    private readonly Comb[] _combsL, _combsR;
    private readonly Allpass[] _allpassL, _allpassR;
    private float _mix, _width;

    public ReverbEffect(int sampleRate)
    {
        var scale = sampleRate / 44100f;
        _combsL = [.. CombTunings.Select(t => new Comb((int)(t * scale)))];
        _combsR = [.. CombTunings.Select(t => new Comb((int)((t + StereoSpread) * scale)))];
        _allpassL = [.. AllpassTunings.Select(t => new Allpass((int)(t * scale)))];
        _allpassR = [.. AllpassTunings.Select(t => new Allpass((int)((t + StereoSpread) * scale)))];
    }

    public override void Configure(AudioEffectSettings settings, AudioEffectDefinition definition)
    {
        float P(string name) => settings.Get(definition.Parameter(name)!);
        var feedback = 0.7f + (P("size") * 0.28f);
        var damping = P("damping") * 0.4f;
        foreach (var comb in _combsL.Concat(_combsR))
        {
            (comb.Feedback, comb.Damping) = (feedback, damping);
        }

        (_mix, _width) = (P("mix"), P("width"));
    }

    public override void Process(Span<float> stereo)
    {
        var wet1 = (_width / 2f) + 0.5f;
        var wet2 = (1f - _width) / 2f;
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            var input = (stereo[i] + stereo[i + 1]) * 0.015f;
            float outL = 0f, outR = 0f;
            for (var c = 0; c < _combsL.Length; c++)
            {
                outL += _combsL[c].Process(input);
                outR += _combsR[c].Process(input);
            }

            for (var a = 0; a < _allpassL.Length; a++)
            {
                outL = _allpassL[a].Process(outL);
                outR = _allpassR[a].Process(outR);
            }

            var (wetL, wetR) = ((outL * wet1) + (outR * wet2), (outR * wet1) + (outL * wet2));
            stereo[i] += (wetL * 3f - stereo[i]) * _mix;
            stereo[i + 1] += (wetR * 3f - stereo[i + 1]) * _mix;
        }
    }

    private sealed class Comb(int length)
    {
        private readonly float[] _buffer = new float[Math.Max(1, length)];
        private int _index;
        private float _store;

        public float Feedback { get; set; }

        public float Damping { get; set; }

        public float Process(float input)
        {
            var output = _buffer[_index];
            _store = (output * (1f - Damping)) + (_store * Damping);
            _buffer[_index] = input + (_store * Feedback);
            _index = (_index + 1) % _buffer.Length;
            return output;
        }
    }

    private sealed class Allpass(int length)
    {
        private readonly float[] _buffer = new float[Math.Max(1, length)];
        private int _index;

        public float Process(float input)
        {
            var buffered = _buffer[_index];
            _buffer[_index] = input + (buffered * 0.5f);
            _index = (_index + 1) % _buffer.Length;
            return buffered - input;
        }
    }
}

/// <summary>Soft saturation: tanh of the driven signal, then the output level and dry/wet.</summary>
internal sealed class DistortionEffect : AudioEffect
{
    private float _drive, _output, _mix;

    public override void Configure(AudioEffectSettings settings, AudioEffectDefinition definition)
    {
        _drive = AudioEffects.DbToGain(settings.Get(definition.Parameter("drive")!));
        _output = AudioEffects.DbToGain(settings.Get(definition.Parameter("output")!));
        _mix = settings.Get(definition.Parameter("mix")!);
    }

    public override void Process(Span<float> stereo)
    {
        for (var i = 0; i < stereo.Length; i++)
        {
            var wet = MathF.Tanh(stereo[i] * _drive) * _output;
            stereo[i] += (wet - stereo[i]) * _mix;
        }
    }
}

/// <summary>Two modulated delay taps (left and right in opposite phase) around 15 ms.</summary>
internal sealed class ChorusEffect(int sampleRate) : AudioEffect
{
    private readonly float[] _line = new float[(int)(sampleRate * 0.05f) * 2];
    private int _write;
    private double _phase;
    private float _rate, _depth, _mix;

    public override void Configure(AudioEffectSettings settings, AudioEffectDefinition definition)
    {
        _rate = settings.Get(definition.Parameter("rate")!);
        _depth = settings.Get(definition.Parameter("depth")!) * 0.001f * sampleRate;
        _mix = settings.Get(definition.Parameter("mix")!);
    }

    public override void Process(Span<float> stereo)
    {
        var frames = _line.Length / 2;
        var center = 0.015f * sampleRate;
        var step = 2 * Math.PI * _rate / sampleRate;
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            _line[_write * 2] = stereo[i];
            _line[(_write * 2) + 1] = stereo[i + 1];
            var modulation = (float)Math.Sin(_phase) * _depth;
            var wetL = Tap(center + modulation, 0, frames);
            var wetR = Tap(center - modulation, 1, frames);
            stereo[i] += (wetL - stereo[i]) * _mix;
            stereo[i + 1] += (wetR - stereo[i + 1]) * _mix;
            _write = (_write + 1) % frames;
            _phase = (_phase + step) % (2 * Math.PI);
        }
    }

    private float Tap(float delayFrames, int channel, int frames)
    {
        var position = _write - Math.Clamp(delayFrames, 1f, frames - 2);
        if (position < 0)
        {
            position += frames;
        }

        var index = (int)position;
        var fraction = position - index;
        var a = _line[(index % frames * 2) + channel];
        var b = _line[((index + 1) % frames * 2) + channel];
        return a + ((b - a) * fraction);
    }
}
