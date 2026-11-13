using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.DebugUI.Panels;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// The Mixer window: the active scene's AudioMixer as a mixing console (channel strips with insert effects, faders and
/// meters; see <see cref="AudioMixerView"/>). Meters are live in Play mode; edits in edit mode are undoable scene changes.
/// </summary>
internal sealed class MixerPanel(EditorContext context)
{
    private readonly AudioMixerView _view = new();

    public void Draw()
    {
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(760f, 420f), ImGuiCond.FirstUseEver); // layouts saved before the Mixer existed do not place it
        if (ImGui.Begin(EditorWindows.Title("Mixer")))
        {
            var scene = context.PlayMode.ActiveScene;
            var mixer = scene.FindObjectsOfType<AudioMixer>().FirstOrDefault(m => m.IsActiveAndEnabled);
            if (mixer is null)
            {
                Gui.Muted("The scene has no AudioMixer. Add one (Add Component > AudioMixer) to get groups, effects and meters.");
            }
            else
            {
                var device = AudioSystem.For(scene)?.Device;
                if (device is null)
                {
                    Gui.Muted("Meters run in Play mode.");
                }

                if (_view.Draw(mixer, device) && !context.PlayMode.IsPlaying)
                {
                    context.Changed("Edit Mixer");
                }
            }
        }

        ImGui.End();
    }
}
