using System.Diagnostics.CodeAnalysis;
using ImGuiNET;
using Microsoft.Extensions.Logging;
using MNVTEngine.Build;
using MNVTEngine.Core.Timing;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Mathematics;
using MNVTEngine.Modules;
using MNVTEngine.Platform;
using MNVTEngine.Runtime;
using MNVTEngine.Serialization;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor;

/// <summary>
/// Shown when the editor starts without a project: recent projects, New Project (2D or 3D, from a template of that
/// profile), and Open. A project from before profiles gets its profile saved on open; when its scenes mix 2D and 3D,
/// the hub asks which profile it is. Sets <see cref="SelectedProject"/> and closes; the editor then opens that project.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "GPU and ImGui resources live from IGameLoop.Load to IGameLoop.Unload.")]
/// <param name="open">A project to open at once (asks for its profile when it needs one).</param>
internal sealed class ProjectHub(ILoggerFactory loggerFactory, RecentProjects recent, int? exitAfterFrames = null, string? screenshot = null, string? open = null) : IGameLoop
{
    private static readonly SysVector4 ErrorColor = new(1f, 0.45f, 0.45f, 1f);
    private GameWindow _window = null!;
    private VulkanGraphicsDevice _device = null!;
    private Renderer2D _renderer = null!;
    private ImGuiRenderer _imgui = null!;
    private Texture2D? _logo;
    private IReadOnlyList<string> _recent = [];
    private IReadOnlyList<ProjectTemplate> _templates = [];
    private string? _engineRoot;
    private string _name = "MyGame";
    private string _location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MNVTEngine Projects");
    private int _template = -1;
    private ProjectProfile? _profile;
    private GameProject? _choosing; // a pre-profile project with mixed content, waiting for the user's choice
    private ProfileInference? _conflict;
    private string _openPath = "";
    private string? _error;
    private int _frames;

    /// <summary>The project file chosen by the user, or null if the hub was closed.</summary>
    public string? SelectedProject { get; private set; }

    /// <summary>A loadable project from before profiles whose scenes mix 2D and 3D: only the user can choose its profile.</summary>
    public static bool NeedsProfileChoice(string path)
    {
        try
        {
            var project = GameProject.Load(path);
            return project.NeedsProfileMigration && ProjectProfileMigration.Infer(project).IsConflict;
        }
        catch (Exception ex) when (ex is SerializationException or IOException)
        {
            return false; // the editor's own load reports it
        }
    }

    public void Load(GameWindow window)
    {
        _window = window;
        _device = VulkanGraphicsDevice.Create(window.VulkanSurface, window.FramebufferWidth, window.FramebufferHeight, loggerFactory.CreateLogger("Graphics"), VulkanDeviceOptions.ForApplication("Editor", window.Options.VSync, window.Options.GraphicsValidation));
        _renderer = new Renderer2D(_device);
        _imgui = new ImGuiRenderer(_device, _renderer.DefaultMaterial, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"), 17f, iconFont: Icons.Font);
        _logo = _device.CreateTexture(Graphics.Svg.EngineLogo.Render(520), SamplerState.LinearClamp, "MNVTEngine logo");
        Gui.ApplyDarkTheme();
        _recent = recent.Load();
        try
        {
            _engineRoot = EngineLocation.FindRoot();
            _templates = ProjectTemplates.List(EngineLocation.TemplatesDirectory(_engineRoot));
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidDataException)
        {
            _error = $"Templates unavailable: {ex.Message}";
        }

        if (open is not null)
        {
            Choose(open);
        }
    }

    public void FixedUpdate(float fixedDeltaTime)
    {
    }

    public void Update(FrameTime time)
    {
        _imgui.PruneDisposed();
        ImGuiInput.NewFrame(_window, time.UnscaledDeltaTime);
        ImGui.NewFrame();
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.Begin("##hub", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);
        if (_logo is not null)
        {
            Gui.Image(_imgui.Register(_logo), _logo, new System.Numerics.Vector2(260f, 260f * _logo.Height / _logo.Width));
        }
        else
        {
            ImGui.TextUnformatted("MNVTEngine");
        }

        Gui.Muted("Open a recent project, create a new one, or open an existing folder.");
        ImGui.Spacing();

        if (_choosing is not null && _conflict is not null)
        {
            DrawProfileChoice(_choosing, _conflict);
        }
        else if (ImGui.BeginTable("##hub-columns", 2, ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableNextColumn();
            DrawRecent();
            ImGui.TableNextColumn();
            DrawNew();
            ImGui.Spacing();
            DrawOpen();
            ImGui.EndTable();
        }

        if (_error is not null)
        {
            ImGui.Spacing();
            Gui.TextColored(ErrorColor, _error);
        }

        ImGui.End();
        ImGui.Render();
    }

    public void Render(FrameTime time, float interpolation)
    {
        _device.SetRenderTarget(null);
        _device.Clear(new Color(0.08f, 0.08f, 0.09f));
        _imgui.Render(ImGui.GetDrawData(), _window.FramebufferWidth, _window.FramebufferHeight);
        var exiting = ++_frames == exitAfterFrames;
        if (exiting && screenshot is not null)
        {
            Screenshot.Save(_device, screenshot); // before presenting: the back buffer is gone afterwards
        }

        _device.Present();
        if (exiting)
        {
            _window.Close();
        }
    }

    public void Resize(int width, int height) => _device.ResizeBackBuffer(width, height);

    public void Unload()
    {
        if (_device is null)
        {
            return; // the graphics device could not be created
        }

        _imgui?.Dispose();
        _logo?.Dispose();
        _renderer?.Dispose();
        _device.Dispose();
    }

    private void DrawRecent()
    {
        ImGui.SeparatorText("Recent projects");
        if (_recent.Count == 0)
        {
            Gui.Muted("None yet.");
        }

        foreach (var project in _recent)
        {
            ImGui.PushID(project);
            if (ImGui.Selectable($"{Path.GetFileNameWithoutExtension(project)}\n", false, ImGuiSelectableFlags.AllowDoubleClick, new SysVector2(0, 38)))
            {
                Choose(project);
            }

            var min = ImGui.GetItemRectMin();
            ImGui.GetWindowDrawList().AddText(min + new SysVector2(0, 19), ImGui.GetColorU32(ImGuiCol.TextDisabled), Path.GetDirectoryName(project));
            if (ImGui.BeginPopupContextItem())
            {
                if (ImGui.MenuItem("Remove from list"))
                {
                    recent.Remove(project);
                    _recent = recent.Load();
                }

                ImGui.EndPopup();
            }

            ImGui.PopID();
        }
    }

    private void DrawNew()
    {
        ImGui.SeparatorText("New project");
        ImGui.InputText("Name", ref _name, 64);
        ImGui.InputText("Location", ref _location, 400);
        ImGui.TextUnformatted("Profile");
        foreach (var profile in ProjectProfiles.All)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton(profile == ProjectProfile.TwoD ? "2D" : "3D", _profile == profile) && _profile != profile)
            {
                _profile = profile;
                _template = _templates.ToList().FindIndex(t => t.Id == ProjectTemplates.DefaultTemplateId(profile));
            }
        }

        if (_profile is not { } chosen)
        {
            Gui.Muted("Choose 2D or 3D: a project has one profile, which decides its engine modules.");
            return;
        }

        var choices = _templates.Where(t => t.Profile == chosen).ToList();
        if (choices.Count == 0)
        {
            Gui.Muted($"No {chosen.DisplayName()} templates found.");
            return;
        }

        var selected = _template >= 0 && _template < _templates.Count && _templates[_template].Profile == chosen ? _templates[_template] : choices[0];
        if (ImGui.BeginCombo("Template", selected.Name))
        {
            foreach (var template in choices)
            {
                if (ImGui.Selectable(template.Name, template == selected))
                {
                    _template = _templates.ToList().IndexOf(template);
                    selected = template;
                }
            }

            ImGui.EndCombo();
        }

        Gui.Muted(selected.Description);
        Gui.Muted($"Creates {Path.Combine(_location, _name)}");
        if (ImGui.Button($"{Icons.Plus}  Create {chosen.DisplayName()} project", new SysVector2(200, 0)) && _engineRoot is not null)
        {
            try
            {
                Choose(ProjectTemplates.Create(selected, _name, Path.Combine(_location, _name), _engineRoot));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SerializationException or InvalidDataException)
            {
                _error = ex.Message;
            }
        }
    }

    private void DrawProfileChoice(GameProject project, ProfileInference conflict)
    {
        ImGui.SeparatorText($"Choose the profile of {project.Name}");
        Gui.TextWrapped("This project was made before projects had a profile, and its scenes contain both 2D and 3D components. "
            + "A project is either 2D or 3D, and the profile decides which engine modules it is built with; it cannot be guessed here.");
        ImGui.Spacing();
        Gui.TextWrapped(conflict.Describe());
        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        Gui.Muted($"The profile is saved in {Path.GetFileName(project.ProjectFile)}; the current file is kept as {Path.GetFileName(project.ProjectFile)}.v1.bak. "
            + "Components of the other profile stay in the scenes and are reported when the project is validated.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        foreach (var profile in ProjectProfiles.All)
        {
            if (ImGui.Button($"Make it {profile.DisplayName()}", new SysVector2(160, 0)))
            {
                Migrate(project, profile);
            }

            ImGui.SameLine();
        }

        if (ImGui.Button("Cancel", new SysVector2(100, 0)))
        {
            (_choosing, _conflict) = (null, null);
        }
    }

    private void Migrate(GameProject project, ProjectProfile? choice)
    {
        try
        {
            ProjectProfileMigration.Migrate(project, choice);
            (_choosing, _conflict) = (null, null);
            SelectedProject = project.ProjectFile;
            _window.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            _error = ex.Message;
        }
    }

    private void DrawOpen()
    {
        ImGui.SeparatorText("Open project");
        ImGui.InputText("Folder or .mnvtproj", ref _openPath, 400);
        if (ImGui.Button($"{Icons.FolderOpen}  Open", new SysVector2(160, 0)))
        {
            try
            {
                Choose(GameProject.FindProjectFile(_openPath.Trim().Trim('"')));
            }
            catch (FileNotFoundException ex)
            {
                _error = ex.Message;
            }
        }
    }

    private void Choose(string projectFile)
    {
        try
        {
            var project = GameProject.Load(projectFile); // validate before closing the hub
            if (project.NeedsProfileMigration && ProjectProfileMigration.Infer(project) is { IsConflict: true } conflict)
            {
                (_choosing, _conflict) = (project, conflict);
                return;
            }

            // The editor saves the profile of an unambiguous pre-profile project when it opens it.
            SelectedProject = project.ProjectFile;
            _window.Close();
        }
        catch (Exception ex) when (ex is SerializationException or FileNotFoundException)
        {
            _error = ex.Message;
        }
    }
}
