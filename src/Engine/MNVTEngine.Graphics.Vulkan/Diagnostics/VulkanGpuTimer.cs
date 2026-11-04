using MNVTEngine.Graphics.Vulkan.Instance;
using Silk.NET.Vulkan;

namespace MNVTEngine.Graphics.Vulkan.Diagnostics;

/// <summary>
/// GPU timing with timestamp queries (<c>vkCmdWriteTimestamp2</c>). Each device frame slot has its own query pool. A
/// frame writes a timestamp at its start, at every scope begin and end, and at its end. The slot's results are read
/// when the slot is reused, after its fence has signalled, with <c>vkGetQueryPoolResults</c> without WAIT. A frame whose
/// queries are not all available, or that did not end in the device frame it began in, is dropped. Nothing waits on the GPU.
/// </summary>
internal sealed unsafe class VulkanGpuTimer : IGpuTimer, IDisposable
{
    private const uint InitialQueries = 64;
    private readonly VulkanGraphicsDevice _device;
    private readonly Slot[] _slots;
    private readonly List<GpuTiming> _lastScopes = [];
    private readonly Stack<int> _open = new();
    private readonly double _nanosecondsPerTick;
    private readonly ulong _validMask;
    private Slot? _current;

    public VulkanGpuTimer(VulkanGraphicsDevice device, int slots)
    {
        _device = device;
        _nanosecondsPerTick = device.Context.Limits.TimestampPeriod;
        var bits = device.Context.TimestampValidBits;
        _validMask = bits >= 64 ? ulong.MaxValue : (1ul << (int)bits) - 1;
        _slots = Enumerable.Range(0, slots).Select(_ => new Slot()).ToArray();
    }

    public int Latency => _slots.Length;

    public double LastFrameMilliseconds { get; private set; } = double.NaN;

    public IReadOnlyList<GpuTiming> LastScopes => _lastScopes;

    public long LastFrameIndex { get; private set; } = -1;

    public long DroppedFrames { get; private set; }

    public void BeginFrame()
    {
        var frame = _device.BeginRecording(); // waits for this slot's previous frame, so its queries are complete
        var slot = _slots[_device.FrameSlot];
        if (slot.Used > 0)
        {
            Collect(slot);
        }

        if (slot.Overflowed)
        {
            slot.Capacity *= 2;
        }

        slot.Reset(frame);
        if (slot.Pool.Handle == 0 || slot.PoolCapacity < slot.Capacity)
        {
            CreatePool(slot);
        }
        else
        {
            _device.Context.Vk.ResetQueryPool(_device.Context.Device, slot.Pool, 0, slot.PoolCapacity); // results were read above
        }

        _current = slot;
        _open.Clear();
        Stamp(slot, null, -1);
    }

    public void BeginScope(string name)
    {
        if (Active() is { } slot)
        {
            _open.Push(Stamp(slot, name, -1));
        }
    }

    public void EndScope()
    {
        if (Active() is { } slot && _open.TryPop(out var begin) && begin >= 0)
        {
            Stamp(slot, null, begin);
        }
    }

    public void EndFrame()
    {
        if (Active() is { } slot)
        {
            Stamp(slot, null, 0);
            slot.Complete = true;
        }

        _current = null;
    }

    public void Dispose()
    {
        foreach (var slot in _slots)
        {
            if (slot.Pool.Handle != 0)
            {
                _device.Context.Vk.DestroyQueryPool(_device.Context.Device, slot.Pool, null);
            }
        }
    }

    /// <summary>The frame's slot while the device is still recording the frame the timer began in.</summary>
    private Slot? Active()
    {
        if (_current is { } slot && slot.Frame != _device.FrameNumber)
        {
            _current = null; // the frame was presented without EndFrame: its timestamps are incomplete
        }

        return _current;
    }

    private int Stamp(Slot slot, string? name, int begin)
    {
        if (slot.Used == slot.PoolCapacity)
        {
            // A pool cannot grow while its queries are recorded: this frame is dropped and the next one gets a bigger pool.
            slot.Overflowed = true;
            return -1;
        }

        var index = slot.Used++;
        _device.Context.Vk.CmdWriteTimestamp2(_device.CurrentCommandBuffer, PipelineStageFlags2.AllCommandsBit, slot.Pool, (uint)index);
        slot.Names.Add(name);
        slot.Begins.Add(begin);
        return index;
    }

    private void CreatePool(Slot slot)
    {
        var context = _device.Context;
        if (slot.Pool.Handle != 0)
        {
            _device.DestroyLater(slot.Pool);
        }

        var info = new QueryPoolCreateInfo { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = slot.Capacity };
        QueryPool pool;
        VulkanInstance.Check(context.Vk.CreateQueryPool(context.Device, &info, null, &pool), "vkCreateQueryPool");
        context.Vk.ResetQueryPool(context.Device, pool, 0, slot.Capacity);
        slot.Pool = pool;
        slot.PoolCapacity = slot.Capacity;
    }

    private void Collect(Slot slot)
    {
        if (!slot.Complete || slot.Overflowed)
        {
            DroppedFrames++;
            return;
        }

        var count = slot.Used;
        var results = stackalloc ulong[count * 2]; // value, availability
        var status = _device.Context.Vk.GetQueryPoolResults(_device.Context.Device, slot.Pool, 0, (uint)count, (nuint)(count * 2 * sizeof(ulong)), results, 2 * sizeof(ulong),
            QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit);
        if (status is not (Result.Success or Result.NotReady))
        {
            throw new InvalidOperationException($"vkGetQueryPoolResults failed: {status}.");
        }

        for (var i = 0; i < count; i++)
        {
            if (results[(i * 2) + 1] == 0)
            {
                DroppedFrames++;
                return;
            }
        }

        _lastScopes.Clear();
        for (var i = 1; i < count; i++)
        {
            if (slot.Begins[i] >= 0)
            {
                var ticks = Ticks(results, slot.Begins[i], i);
                if (slot.Begins[i] == 0)
                {
                    LastFrameMilliseconds = ticks * _nanosecondsPerTick / 1e6;
                }
                else
                {
                    _lastScopes.Add(new GpuTiming(slot.Names[slot.Begins[i]]!, ticks * _nanosecondsPerTick / 1e6));
                }
            }
        }

        LastFrameIndex = slot.Frame;
    }

    private ulong Ticks(ulong* results, int begin, int end) => ((results[end * 2] - results[begin * 2]) & _validMask);

    private sealed class Slot
    {
        public QueryPool Pool { get; set; }

        public uint Capacity { get; set; } = InitialQueries;

        public uint PoolCapacity { get; set; }

        public int Used { get; set; }

        public long Frame { get; set; }

        public bool Complete { get; set; }

        public bool Overflowed { get; set; }

        public List<string?> Names { get; } = [];

        /// <summary>Per query: index of its begin query (0 = frame start for the end stamp), -1 for begins.</summary>
        public List<int> Begins { get; } = [];

        public void Reset(long frame)
        {
            Used = 0;
            Frame = frame;
            Complete = false;
            Overflowed = false;
            Names.Clear();
            Begins.Clear();
        }
    }
}
