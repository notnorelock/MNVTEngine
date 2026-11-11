using Newtonsoft.Json;

namespace MNVTEngine.Editor.Services;

/// <summary>
/// Unity's scene visibility: GameObjects hidden in the Scene view only (the Hierarchy's eye). A hidden object hides its
/// children too; nothing changes in the scene file or the game. <see cref="ShowHidden"/> (the Scene toolbar's toggle)
/// draws them anyway. Kept per user in the project's <c>Library/Editor</c> folder.
/// </summary>
public sealed class SceneVisibility
{
    private readonly HashSet<Guid> _hidden = [];

    /// <summary>Raised after every change; save <see cref="ToJson"/> here.</summary>
    public event Action? Changed;

    /// <summary>Draw hidden objects anyway.</summary>
    public bool ShowHidden { get; set; }

    public int HiddenCount => _hidden.Count;

    /// <summary>Hidden itself (not through a parent): the Hierarchy shows its crossed-out eye.</summary>
    public bool IsHiddenSelf(GameObject gameObject) => gameObject is not null && _hidden.Contains(gameObject.Id);

    /// <summary>Drawn and pickable in the Scene view: neither it nor a parent is hidden (or hidden objects are shown).</summary>
    public bool IsVisible(GameObject gameObject)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        if (ShowHidden || _hidden.Count == 0)
        {
            return true;
        }

        for (var transform = gameObject.Transform; transform is not null; transform = transform.Parent)
        {
            if (_hidden.Contains(transform.GameObject.Id))
            {
                return false;
            }
        }

        return true;
    }

    public void Toggle(GameObject gameObject)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        if (!_hidden.Remove(gameObject.Id))
        {
            _hidden.Add(gameObject.Id);
        }

        Changed?.Invoke();
    }

    public void ShowAll()
    {
        _hidden.Clear();
        Changed?.Invoke();
    }

    public string ToJson() => JsonConvert.SerializeObject(new { hidden = _hidden.Order() });

    /// <summary>Loads saved visibility; a file that is not valid leaves nothing hidden and returns false.</summary>
    public bool Load(string? json)
    {
        _hidden.Clear();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var stored = JsonConvert.DeserializeAnonymousType(json, new { hidden = Array.Empty<Guid>() });
            _hidden.UnionWith(stored?.hidden ?? []);
            return stored?.hidden is not null;
        }
        catch (JsonException)
        {
            // Corrupt editor preferences: start with everything visible; the caller reports the false result.
            return false;
        }
    }
}
