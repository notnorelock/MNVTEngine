// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Extensions;

namespace MNVTEngine.Editor;

/// <summary>
/// <see cref="IEditorGui"/> over ImGui for extensions. Text never goes through a printf format (the <see cref="Gui"/>
/// wrappers), so a '%' in an extension's text is shown, not interpreted.
/// </summary>
internal sealed class ImGuiEditorGui : IEditorGui
{
    private static readonly System.Numerics.Vector4 Red = new(1f, 0.45f, 0.4f, 1f);
    private static readonly System.Numerics.Vector4 Green = new(0.45f, 0.85f, 0.55f, 1f);

    public void Heading(string text)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(text);
        ImGui.Separator();
    }

    public void Text(string text) => ImGui.TextUnformatted(text);

    public void Muted(string text) => Gui.Muted(text);

    public void ErrorText(string text) => Gui.TextColored(Red, text);

    public void SuccessText(string text) => Gui.TextColored(Green, text);

    public void Paragraph(string text) => Gui.TextWrapped(text);

    public bool Button(string label, bool enabled = true)
    {
        ImGui.BeginDisabled(!enabled);
        var clicked = ImGui.Button(label);
        ImGui.EndDisabled();
        return clicked && enabled;
    }

    public bool TextField(string label, ref string value, bool password = false, int maxLength = 256)
    {
        value ??= "";
        return ImGui.InputText(label, ref value, (uint)Math.Clamp(maxLength, 1, 65536), password ? ImGuiInputTextFlags.Password : ImGuiInputTextFlags.None);
    }

    public bool Checkbox(string label, ref bool value) => ImGui.Checkbox(label, ref value);

    public bool Selectable(string label, bool selected) => ImGui.Selectable(label, selected);

    public bool Section(string label, bool openByDefault = true) =>
        ImGui.CollapsingHeader(label, openByDefault ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);

    public void SameLine() => ImGui.SameLine();

    public void Separator() => ImGui.Separator();

    public void Space() => ImGui.Spacing();

    public void Progress(float fraction, string? caption = null) =>
        ImGui.ProgressBar(Math.Clamp(fraction, 0f, 1f), new System.Numerics.Vector2(-1f, 0f), caption ?? "");
}
