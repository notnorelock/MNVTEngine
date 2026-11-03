using Silk.NET.OpenGL;

namespace MNVTEngine.Graphics.OpenGL;

/// <summary>
/// GPU timing with GL_TIMESTAMP queries (glQueryCounter, core since 3.3). Each frame writes a timestamp at its start, at
/// every scope begin/end and at its end, into one of <see cref="SlotCount"/> rotating slots. A slot is read when it comes
/// round again, only if every query reports GL_QUERY_RESULT_AVAILABLE; otherwise that frame is dropped. Nothing waits on
/// the GPU. Query objects are pooled per slot and grow only when a frame uses more scopes than before.
/// </summary>
internal sealed class GLGpuTimer : IGpuTimer, IDisposable
{
    private const int SlotCount = 4;
    private readonly GL _gl;
    private readonly Slot[] _slots = [new(), new(), new(), new()];
    private readonly List<GpuTiming> _lastScopes = [];
    private readonly Stack<int> _open = new();
    private Slot? _current;
    private long _frame;

    public GLGpuTimer(GL gl) => _gl = gl;

    public int Latency => SlotCount - 1;

    public double LastFrameMilliseconds { get; private set; } = double.NaN;

    public IReadOnlyList<GpuTiming> LastScopes => _lastScopes;

    public long LastFrameIndex { get; private set; } = -1;

    public long DroppedFrames { get; private set; }

    public void BeginFrame()
    {
        var slot = _slots[_frame % SlotCount];
        if (slot.Used > 0)
        {
            Collect(slot); // issued SlotCount frames ago
        }

        slot.Reset(_frame);
        _current = slot;
        _open.Clear();
        Stamp(slot, null, -1); // frame start
        _frame++;
    }

    public void BeginScope(string name)
    {
        if (_current is { } slot)
        {
            _open.Push(Stamp(slot, name, -1));
        }
    }

    public void EndScope()
    {
        if (_current is { } slot && _open.TryPop(out var begin))
        {
            Stamp(slot, null, begin);
        }
    }

    public void EndFrame()
    {
        if (_current is { } slot)
        {
            while (_open.TryPop(out var begin))
            {
                Stamp(slot, null, begin); // close scopes left open
            }

            slot.FrameEnd = Stamp(slot, null, 0);
            _current = null;
        }
    }

    public void Dispose()
    {
        foreach (var slot in _slots)
        {
            if (slot.Queries.Count > 0)
            {
                _gl.DeleteQueries((uint)slot.Queries.Count, [.. slot.Queries]);
                slot.Queries.Clear();
            }
        }
    }

    /// <param name="pairsWith">Index of the matching begin stamp (scope end), 0 for the frame end, -1 for a begin.</param>
    private int Stamp(Slot slot, string? name, int pairsWith)
    {
        if (slot.Used == slot.Queries.Count)
        {
            Span<uint> created = stackalloc uint[8];
            _gl.CreateQueries((QueryTarget)GLEnum.Timestamp, (uint)created.Length, created);
            foreach (var id in created)
            {
                slot.Queries.Add(id);
            }

            slot.Names.AddRange(new string?[created.Length]);
            slot.Pairs.AddRange(new int[created.Length]);
        }

        var index = slot.Used++;
        slot.Names[index] = name;
        slot.Pairs[index] = pairsWith;
        _gl.QueryCounter(slot.Queries[index], QueryCounterTarget.Timestamp);
        return index;
    }

    private void Collect(Slot slot)
    {
        if (slot.FrameEnd <= 0)
        {
            return; // frame never ended (e.g. the host skipped EndFrame)
        }

        for (var i = 0; i < slot.Used; i++)
        {
            _gl.GetQueryObject(slot.Queries[i], QueryObjectParameterName.ResultAvailable, out int available);
            if (available == 0)
            {
                DroppedFrames++;
                return;
            }
        }

        Span<ulong> stamps = slot.Used <= 256 ? stackalloc ulong[slot.Used] : new ulong[slot.Used];
        for (var i = 0; i < slot.Used; i++)
        {
            _gl.GetQueryObject(slot.Queries[i], QueryObjectParameterName.Result, out ulong value);
            stamps[i] = value;
        }

        _lastScopes.Clear();
        for (var i = 1; i < slot.Used; i++)
        {
            if (slot.Pairs[i] > 0 && slot.Names[slot.Pairs[i]] is { } name)
            {
                _lastScopes.Add(new GpuTiming(name, (stamps[i] - stamps[slot.Pairs[i]]) / 1_000_000.0));
            }
        }

        LastFrameMilliseconds = (stamps[slot.FrameEnd] - stamps[0]) / 1_000_000.0;
        LastFrameIndex = slot.Frame;
    }

    private sealed class Slot
    {
        public List<uint> Queries { get; } = [];

        public List<string?> Names { get; } = [];

        public List<int> Pairs { get; } = [];

        public int Used { get; set; }

        public int FrameEnd { get; set; }

        public long Frame { get; private set; }

        public void Reset(long frame)
        {
            Used = 0;
            FrameEnd = 0;
            Frame = frame;
        }
    }
}
