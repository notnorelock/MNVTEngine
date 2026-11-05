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
using MNVTEngine.Platform;
using MNVTEngine.Runtime;
using MNVTEngine.Serialization;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor;

/// <summary>
/// Shown when the editor starts without a project: recent projects, New Project from a template, and Open. Sets
/// <see cref="SelectedProject"/> and closes; the editor then opens that project.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "GPU and ImGui resources live from IGameLoop.Load to IGameLoop.Unload.")]
internal sealed class ProjectHub(ILoggerFactory loggerFactory, RecentProjects recent, int? exitAfterFrames = null, string? screenshot = null) : IGameLoop
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
    private int _template;
    private string _openPath = "";
    private string? _error;
    private int _frames;

    /// <summary>The project file chosen by the user, or null if the hub was closed.</summary>
    public string? SelectedProject { get; private set; }

    public void Load(GameWindow window)
    {
        _window = window;
        _device = VulkanGraphicsDevice.Create(window.VulkanSurface, window.FramebufferWidth, window.FramebufferHeight, loggerFactory.CreateLogger("Graphics"), VulkanDeviceOptions.ForApplication("Editor", window.Options.VSync, window.Options.GraphicsValidation));
        _renderer = new Renderer2D(_device);
        _imgui = new ImGuiRenderer(_device, _renderer.DefaultMaterial, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"), 17f);
        _logo = _device.CreateTexture(Graphics.Svg.EngineLogo.Render(520), SamplerState.LinearClamp, "MNVTEngine logo");
        Gui.ApplyDarkTheme();
        _recent = recent.Load();
        try
        {
            _engineRoot = EngineLocation.FindRoot();
            _templates = ProjectTemplates.List(EngineLocation.TemplatesDirectory(_engineRoot));
            _template = Math.Max(0, _templates.ToList().FindIndex(t => t.Id == "basic-2d"));
        }
        catch (DirectoryNotFoundException ex)
        {
            _error = $"Templates unavailable: {ex.Message}";
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

        if (ImGui.BeginTable("##hub-columns", 2, ImGuiTableFlags.BordersInnerV))
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
        if (_templates.Count > 0)
        {
            var labels = _templates.Select(t => t.Id).ToArray();
            ImGui.Combo("Template", ref _template, labels, labels.Length);
            Gui.Muted(_templates[_template].Description);
        }

        Gui.Muted($"Creates {Path.Combine(_location, _name)}");
        if (ImGui.Button("Create project", new SysVector2(160, 0)) && _engineRoot is not null && _templates.Count > 0)
        {
            try
            {
                Choose(ProjectTemplates.Create(_templates[_template], _name, Path.Combine(_location, _name), _engineRoot));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SerializationException)
            {
                _error = ex.Message;
            }
        }
    }

    private void DrawOpen()
    {
        ImGui.SeparatorText("Open project");
        ImGui.InputText("Folder or .mnvtproj", ref _openPath, 400);
        if (ImGui.Button("Open", new SysVector2(160, 0)))
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
            SelectedProject = GameProject.Load(projectFile).ProjectFile; // validate before closing the hub
            _window.Close();
        }
        catch (Exception ex) when (ex is SerializationException or FileNotFoundException)
        {
            _error = ex.Message;
        }
    }
}
