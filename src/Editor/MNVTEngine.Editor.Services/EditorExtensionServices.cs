// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-27.

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MNVTEngine.Editor.Extensions;
using Newtonsoft.Json;

namespace MNVTEngine.Editor.Services;

/// <summary>The extensions one generation of editor code brings: its windows (with their menu paths), inspectors and scene gizmos.</summary>
public sealed class ExtensionCatalog
{
    private ExtensionCatalog(string name, IReadOnlyList<(Type Type, string? MenuPath)> windows, IReadOnlyList<Type> inspectors, IReadOnlyList<Type> gizmos, IReadOnlyList<string> problems)
    {
        Name = name;
        Windows = windows;
        Inspectors = inspectors;
        Gizmos = gizmos;
        Problems = problems;
    }

    public static ExtensionCatalog Empty { get; } = new("Editor", [], [], [], []);

    /// <summary>The editor code's assembly name: its settings and secrets are kept under it.</summary>
    public string Name { get; }

    /// <summary>Concrete <see cref="EditorWindow"/> types and their <see cref="EditorMenuAttribute"/> paths.</summary>
    public IReadOnlyList<(Type Type, string? MenuPath)> Windows { get; }

    /// <summary>Concrete <see cref="ComponentInspector"/> types.</summary>
    public IReadOnlyList<Type> Inspectors { get; }

    /// <summary>Concrete <see cref="SceneGizmos"/> types.</summary>
    public IReadOnlyList<Type> Gizmos { get; }

    /// <summary>Types that look like extensions but cannot be created (no public parameterless constructor), and bad menu paths.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Finds the extensions among <paramref name="assembly"/>'s public types.</summary>
    public static ExtensionCatalog From(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var (windows, inspectors, gizmos, problems) = (new List<(Type, string?)>(), new List<Type>(), new List<Type>(), new List<string>());
        foreach (var type in assembly.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false }).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var window = typeof(EditorWindow).IsAssignableFrom(type);
            var gizmo = typeof(SceneGizmos).IsAssignableFrom(type);
            if (!window && !gizmo && !typeof(ComponentInspector).IsAssignableFrom(type))
            {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                problems.Add($"{type.FullName} has no public parameterless constructor; the editor cannot create it.");
                continue;
            }

            if (!window)
            {
                (gizmo ? gizmos : inspectors).Add(type);
                continue;
            }

            var path = type.GetCustomAttribute<EditorMenuAttribute>()?.Path?.Trim();
            if (path is not null && (path.Length == 0 || path.Split('/').Any(p => p.Trim().Length == 0)))
            {
                problems.Add($"{type.FullName}: the menu path '{path}' has an empty part.");
                path = null;
            }

            windows.Add((type, path));
        }

        return new ExtensionCatalog(assembly.GetName().Name ?? "Editor", windows, inspectors, gizmos, problems);
    }
}

/// <summary>
/// An extension's settings in the project: <c>ProjectSettings/Extensions/&lt;name&gt;.json</c>, a flat object of strings,
/// written at every change. A file that does not parse is reported and replaced on the next write.
/// </summary>
public sealed class ProjectExtensionSettings : IExtensionSettings
{
    private readonly string _file;
    private readonly SortedDictionary<string, string> _values;

    public ProjectExtensionSettings(string projectDirectory, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectDirectory);
        _file = Path.Combine(projectDirectory, "ProjectSettings", "Extensions", SafeName(name) + ".json");
        _values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(_file))
        {
            return;
        }

        try
        {
            foreach (var (key, value) in JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(_file)) ?? [])
            {
                _values[key] = value;
            }
        }
        catch (JsonException ex)
        {
            Problem = $"{_file} could not be read ({ex.Message}); it is replaced on the next change.";
        }
    }

    /// <summary>Why the file could not be read; null when it was fine.</summary>
    public string? Problem { get; }

    public string? Read(string key) => _values.GetValueOrDefault(key);

    public void Write(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (value is null ? !_values.Remove(key) : _values.TryGetValue(key, out var old) && old == value)
        {
            return;
        }

        if (value is not null)
        {
            _values[key] = value;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonConvert.SerializeObject(_values, Formatting.Indented) + "\n");
    }

    /// <summary>A file name from an assembly name: letters, digits, '.', '-' and '_' only.</summary>
    public static string SafeName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]);
    }
}

/// <summary>
/// An extension's secrets for this user: one file per project and extension under
/// <c>%LOCALAPPDATA%/MNVTEngine/Secrets</c>, encrypted with DPAPI for the Windows account (another user, or a copy on
/// another computer, cannot read it). Not available elsewhere: nothing is stored there.
/// </summary>
public sealed class EditorSecretStore : IEditorSecrets
{
    private static readonly byte[] Entropy = "MNVTEngine.Editor.Secrets.v1"u8.ToArray();
    private readonly string _file;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <param name="root">Where secrets live; defaults to <c>%LOCALAPPDATA%/MNVTEngine/Secrets</c>.</param>
    public EditorSecretStore(string projectDirectory, string name, string? root = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectDirectory);
        var project = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectDirectory).ToUpperInvariant())))[..16];
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MNVTEngine", "Secrets");
        _file = Path.Combine(root, project, ProjectExtensionSettings.SafeName(name) + ".bin");
        if (!OperatingSystem.IsWindows() || !File.Exists(_file))
        {
            return;
        }

        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_file), Entropy, DataProtectionScope.CurrentUser);
            foreach (var (key, value) in JsonConvert.DeserializeObject<Dictionary<string, string>>(Encoding.UTF8.GetString(plain)) ?? [])
            {
                _values[key] = value;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            Problem = $"The saved secrets could not be read ({ex.Message}); sign in again.";
        }
    }

    public bool Available => OperatingSystem.IsWindows();

    /// <summary>Why the saved secrets could not be read; null when they were fine.</summary>
    public string? Problem { get; }

    public string? Read(string key) => _values.GetValueOrDefault(key);

    public void Write(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (value is null)
        {
            _values.Remove(key);
        }
        else
        {
            _values[key] = value;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllBytes(_file, ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_values)), Entropy, DataProtectionScope.CurrentUser));
    }
}
