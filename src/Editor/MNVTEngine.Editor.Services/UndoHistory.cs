namespace MNVTEngine.Editor.Services;

/// <summary>
/// Undo/redo by snapshots: every step is the whole edit scene serialized the same way it is saved. Edits mark the
/// history as changed; <see cref="Commit"/> (called by the editor whenever no drag or text input is in progress)
/// turns everything since the last commit into one step, so a gizmo drag, a typed number or a tile stroke undoes as a
/// unit. Snapshot undo needs no per-command code, so every kind of edit is undoable, including ones added later.
/// </summary>
public sealed class UndoHistory(Func<string> capture, Action<string> restore, int capacity = 100)
{
    private readonly LinkedList<Step> _undo = new();
    private readonly Stack<Step> _redo = new();
    private string _baseline = "";
    private string? _pendingLabel;

    public bool CanUndo => _undo.Count > 0 || _pendingLabel is not null;

    public bool CanRedo => _redo.Count > 0 && _pendingLabel is null;

    /// <summary>Label of the step <see cref="Undo"/> would revert.</summary>
    public string? UndoLabel => _pendingLabel ?? _undo.Last?.Value.Label;

    public string? RedoLabel => CanRedo ? _redo.Peek().Label : null;

    public int Count => _undo.Count;

    /// <summary>Forgets every step and takes the current scene as the starting point (new or opened scene).</summary>
    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        _pendingLabel = null;
        _baseline = capture();
    }

    /// <summary>Records that the scene changed; the first label of a group names the step.</summary>
    public void MarkChanged(string label) => _pendingLabel ??= label;

    /// <summary>Closes the current group of changes as one step. Returns false if nothing actually changed.</summary>
    public bool Commit()
    {
        if (_pendingLabel is not { } label)
        {
            return false;
        }

        _pendingLabel = null;
        var current = capture();
        if (current == _baseline)
        {
            return false; // e.g. a field set to the value it already had
        }

        _undo.AddLast(new Step(_baseline, label));
        if (_undo.Count > capacity)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
        _baseline = current;
        return true;
    }

    public bool Undo()
    {
        Commit();
        if (_undo.Last is not { } last)
        {
            return false;
        }

        _undo.RemoveLast();
        _redo.Push(new Step(_baseline, last.Value.Label));
        _baseline = last.Value.Snapshot;
        restore(_baseline);
        return true;
    }

    public bool Redo()
    {
        Commit();
        if (!_redo.TryPop(out var step))
        {
            return false;
        }

        _undo.AddLast(new Step(_baseline, step.Label));
        _baseline = step.Snapshot;
        restore(_baseline);
        return true;
    }

    private sealed record Step(string Snapshot, string Label);
}
