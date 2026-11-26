// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.Editor.Extensions;

/// <summary>
/// A dockable editor window from a project's editor code (docs/editor/extensions.md). The editor creates one per type
/// for each loaded generation of code (a script reload recreates it), opens it from <see cref="EditorMenuAttribute"/>
/// items or <see cref="IEditorServices.OpenWindow{T}"/>, and calls <see cref="OnGui"/> every frame while it is open.
/// Keep state that must survive a reload in <see cref="IEditorServices.Settings"/>.
/// </summary>
public abstract class EditorWindow
{
    private IEditorServices? _editor;

    /// <summary>The tab title.</summary>
    public virtual string Title => GetType().Name;

    /// <summary>Set before the first <see cref="OnGui"/>.</summary>
    /// <exception cref="InvalidOperationException">Read before the editor attached the window.</exception>
    protected IEditorServices Editor => _editor ?? throw new InvalidOperationException("The window is not attached to the editor yet.");

    /// <summary>Draws the window's contents (the editor has begun the window and ends it).</summary>
    public abstract void OnGui(IEditorGui gui);

    /// <summary>The window opened (or the editor started with it open).</summary>
    public virtual void OnOpen()
    {
    }

    /// <summary>The window closed, or its code is being reloaded: let go of anything it holds.</summary>
    public virtual void OnClose()
    {
    }

    /// <summary>Called by the editor.</summary>
    public void Attach(IEditorServices editor) => _editor = editor ?? throw new ArgumentNullException(nameof(editor));
}

/// <summary>
/// Adds a section below the inspector's fields for components of <typeparamref name="T"/> (and its subclasses):
/// buttons, status, shortcuts to windows. One instance per type and generation of code.
/// </summary>
public abstract class ComponentInspector<T> : ComponentInspector
    where T : Component
{
    public sealed override Type ComponentType => typeof(T);

    public sealed override void OnInspectorGui(IEditorGui gui, Component component) => OnInspectorGui(gui, (T)component);

    /// <summary>Draws the section for the selected component.</summary>
    protected abstract void OnInspectorGui(IEditorGui gui, T component);
}

/// <summary>Untyped base of <see cref="ComponentInspector{T}"/>; derive from that.</summary>
public abstract class ComponentInspector
{
    private IEditorServices? _editor;

    public abstract Type ComponentType { get; }

    /// <exception cref="InvalidOperationException">Read before the editor attached the inspector.</exception>
    protected IEditorServices Editor => _editor ?? throw new InvalidOperationException("The inspector is not attached to the editor yet.");

    public abstract void OnInspectorGui(IEditorGui gui, Component component);

    /// <summary>Called by the editor.</summary>
    public void Attach(IEditorServices editor) => _editor = editor ?? throw new ArgumentNullException(nameof(editor));
}

/// <summary>
/// On an <see cref="EditorWindow"/>: an item in the editor's Tools menu that opens it. Slashes make submenus
/// (<c>"Borealise/SDK Control Panel"</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EditorMenuAttribute(string path) : Attribute
{
    public string Path { get; } = path;
}

/// <summary>
/// Immediate-mode drawing for extensions. Text is drawn as is (never as a format string). Every widget needs a label
/// unique within the window; a label after "##" is hidden (<c>"##password"</c>).
/// </summary>
public interface IEditorGui
{
    void Heading(string text);

    void Text(string text);

    /// <summary>Secondary text.</summary>
    void Muted(string text);

    void ErrorText(string text);

    void SuccessText(string text);

    /// <summary>Wraps at the window's width.</summary>
    void Paragraph(string text);

    bool Button(string label, bool enabled = true);

    /// <summary>A text field; true when its value changed this frame.</summary>
    bool TextField(string label, ref string value, bool password = false, int maxLength = 256);

    bool Checkbox(string label, ref bool value);

    /// <summary>A row the user can select; true when clicked.</summary>
    bool Selectable(string label, bool selected);

    /// <summary>A section that can be folded; draw its contents only when it returns true.</summary>
    bool Section(string label, bool openByDefault = true);

    /// <summary>Puts the next widget on the same line.</summary>
    void SameLine();

    void Separator();

    void Space();

    /// <summary>A progress bar from 0 to 1, with an optional caption.</summary>
    void Progress(float fraction, string? caption = null);
}

/// <summary>What extensions can do in the editor. Call it from the editor's thread (<c>OnGui</c>).</summary>
public interface IEditorServices
{
    /// <summary>The open project's root folder.</summary>
    string ProjectDirectory { get; }

    /// <summary>The scene being edited (the running one in Play Mode).</summary>
    Scene Scene { get; }

    /// <summary>The selected object, or null.</summary>
    GameObject? Selected { get; }

    /// <summary>Settings of this extension kept in the project (<c>ProjectSettings/Extensions</c>, meant to be committed).</summary>
    IExtensionSettings Settings { get; }

    /// <summary>Secrets of this user (sign-in tokens), encrypted for this Windows account and never in the project.</summary>
    IEditorSecrets Secrets { get; }

    /// <summary>The project's assets of a type (<c>AssetTypes.Prefab</c>), by path.</summary>
    IReadOnlyList<EditorAsset> Assets(string type);

    /// <summary>
    /// A prefab asset instantiated into a scene of its own, to read (never shown, never saved); null when it cannot be
    /// read. Do not keep it past the frame.
    /// </summary>
    GameObject? ReadPrefab(Guid prefab);

    /// <summary>Opens (and focuses) the window of type <typeparamref name="T"/> from the same editor code.</summary>
    void OpenWindow<T>()
        where T : EditorWindow;

    /// <summary>
    /// Packages a prefab as a content bundle (docs/architecture/content-bundles.md) in the background. The task completes
    /// off the editor's thread: poll it from <c>OnGui</c>.
    /// </summary>
    Task<EditorBundleResult> BuildBundleAsync(EditorBundleRequest request);

    /// <summary>Writes to the Console panel.</summary>
    void Log(EditorLogLevel level, string message);
}

public enum EditorLogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>A project asset: its GUID, path relative to Assets and type.</summary>
public sealed record EditorAsset(Guid Guid, string Path, string Type);

/// <summary>What to package; see <c>MNVTEngine.Build.BundleRequest</c>.</summary>
/// <param name="Root">The root prefab's path relative to Assets.</param>
/// <param name="Provided">Folders (relative to Assets) whose assets the game already ships.</param>
/// <param name="Policy">What the game accepts: the build refuses what the game would.</param>
public sealed record EditorBundleRequest(
    string Root, string Kind, string Name, IReadOnlyList<string>? Provided = null,
    IReadOnlyDictionary<string, string>? Properties = null, MNVTEngine.Bundles.ContentBundlePolicy? Policy = null);

/// <summary>The bundle written (null when the build failed) and every problem, as text.</summary>
public sealed record EditorBundleResult(string? File, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool Succeeded => File is not null && Errors.Count == 0;
}

/// <summary>An extension's settings in the project: small strings by key, saved at once.</summary>
public interface IExtensionSettings
{
    string? Read(string key);

    /// <summary>Sets a value; null removes it.</summary>
    void Write(string key, string? value);
}

/// <summary>An extension's secrets for this user on this computer.</summary>
public interface IEditorSecrets
{
    /// <summary>False where the editor cannot protect secrets (not Windows): nothing is stored then.</summary>
    bool Available { get; }

    string? Read(string key);

    /// <summary>Stores a secret; null removes it. Does nothing when not <see cref="Available"/>.</summary>
    void Write(string key, string? value);
}
