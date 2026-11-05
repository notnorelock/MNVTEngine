using MNVTEngine.Terrain.Project;

namespace MNVTEngine.Terrain.Editing;

/// <summary>
/// Snapshot undo/redo for a terrain project: every entry is a full project save, restored with a load. A drag or
/// brush stroke is one entry (<see cref="BeginAction"/>/<see cref="EndAction"/>), and edits that change nothing add
/// none. Recording while a snapshot is being restored is ignored.
/// </summary>
public sealed class TerrainHistory(TerrainProject project)
{
    private readonly List<(string Label, string Snapshot)> _entries = [];
    private (string Label, string Before)? _pending;
    private bool _restoring;

    public IReadOnlyList<string> Labels => _entries.Select(e => e.Label).ToList();

    public int Index { get; private set; } = -1;

    public bool CanUndo => Index > 0;

    public bool CanRedo => Index >= 0 && Index < _entries.Count - 1;

    /// <summary>Raised after an entry was restored into the project (selection and tools should resync).</summary>
    public event Action? Restored;

    /// <summary>Raised whenever the entry list or the current index changes.</summary>
    public event Action? Changed;

    public void Reset(string label, string? snapshot = null)
    {
        _entries.Clear();
        _entries.Add((label, snapshot ?? project.Save()));
        Index = 0;
        _pending = null;
        Changed?.Invoke();
    }

    public void Record(string label)
    {
        if (!_restoring)
        {
            Push(label, project.Save());
        }
    }

    public void BeginAction(string label)
    {
        if (!_restoring && _pending is null)
        {
            _pending = (label, project.Save());
        }
    }

    public void EndAction(string? label = null)
    {
        if (_restoring || _pending is not { } pending)
        {
            return;
        }

        _pending = null;
        var after = project.Save();
        if (!string.Equals(pending.Before, after, StringComparison.Ordinal))
        {
            Push(label ?? pending.Label, after);
        }
    }

    public bool Undo() => CanUndo && Apply(Index - 1);

    public bool Redo() => CanRedo && Apply(Index + 1);

    /// <summary>Restores any entry (the history list's click-to-jump).</summary>
    public bool JumpTo(int index) => index >= 0 && index < _entries.Count && index != Index && Apply(index);

    private void Push(string label, string snapshot)
    {
        if (Index >= 0 && Index < _entries.Count && string.Equals(_entries[Index].Snapshot, snapshot, StringComparison.Ordinal))
        {
            return;
        }

        if (Index < _entries.Count - 1)
        {
            _entries.RemoveRange(Index + 1, _entries.Count - Index - 1);
        }

        _entries.Add((label, snapshot));
        Index = _entries.Count - 1;
        Changed?.Invoke();
    }

    private bool Apply(int index)
    {
        _restoring = true;
        _pending = null;
        try
        {
            project.Load(_entries[index].Snapshot);
            Index = index;
            Restored?.Invoke();
        }
        finally
        {
            _restoring = false;
            Changed?.Invoke();
        }

        return true;
    }
}
