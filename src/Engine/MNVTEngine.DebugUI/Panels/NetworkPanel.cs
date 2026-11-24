// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Globalization;
using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.DebugUI.Panels;

/// <summary>Client-side networking state: for each <see cref="NetworkClient"/> in the scene, its connection state,
/// server, replicated object count and message counters, with connect/disconnect buttons.</summary>
internal sealed class NetworkPanel() : DebugPanel("Network")
{
    private static readonly SysVector4 Good = new(0.45f, 0.85f, 0.5f, 1f);
    private static readonly SysVector4 Bad = new(1f, 0.55f, 0.35f, 1f);

    protected override void Draw()
    {
        if (Scene is not { } scene)
        {
            Gui.TextDisabled("No scene is running.");
            return;
        }

        var clients = scene.FindObjectsOfType<NetworkClient>().ToList();
        if (clients.Count == 0)
        {
            Gui.TextDisabled("No NetworkClient component in the scene.");
            return;
        }

        foreach (NetworkClient client in clients)
        {
            ImGui.SeparatorText(client.GameObject.Name);
            Row("State", client.State.ToString(), client.IsConnected ? Good : Bad);
            Row("Server", $"{client.Host}:{client.Port.ToString(CultureInfo.InvariantCulture)}");
            Row("Objects", client.SpawnedObjectCount.ToString(CultureInfo.InvariantCulture));
            Row("Messages in", client.MessagesReceived.ToString(CultureInfo.InvariantCulture));
            Row("Messages out", client.MessagesSent.ToString(CultureInfo.InvariantCulture));

            if (client.IsConnected)
            {
                if (ImGui.Button($"Disconnect##{client.Id}"))
                {
                    client.Disconnect();
                }
            }
            else if (ImGui.Button($"Connect##{client.Id}"))
            {
                client.Connect();
            }
        }
    }

    private static void Row(string label, string value, SysVector4? color = null)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(ImGui.GetFontSize() * 9);
        if (color is { } c)
        {
            Gui.TextColored(c, value);
        }
        else
        {
            ImGui.TextUnformatted(value);
        }
    }
}
