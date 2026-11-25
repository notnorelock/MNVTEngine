// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine;
using MNVTEngine.Mathematics;
using MNVTEngine.Reactivity;
using MNVTEngine.UI;
using MNVTEngine.UI.Widgets;

namespace UIShowcase;

/// <summary>
/// The design system built in code with <see cref="V"/>: text roles, button variants, a form with validation, tabs
/// over pages, a keyed list, toasts and a dialog. Everything reactive is a signal; nothing is updated by hand.
/// </summary>
public sealed class DesignGallery : Component
{
    private sealed record Track(int Id, string Title, string Artist);

    private UIContext? _ui;

    public override void Start()
    {
        var ui = _ui = UI.CreateContext().UseDesign();
        var name = Signal.Create("");
        var sound = Signal.Create(true);
        var volume = Signal.Create(0.6f);
        var tab = Signal.Create(0);
        var saving = Signal.Create(false);
        var tracks = Signal.Create<IReadOnlyList<Track>>(
        [
            new(1, "Aurora", "Nordlys"), new(2, "Midnight Run", "Kasa"), new(3, "Glass Rooms", "Vela"),
        ]);
        var next = 4;
        string? NameError() => name.Value.Length > 16 ? "At most 16 characters." : null;

        ui.Mount(() => V.Row(
                V.Card(
                        V.Label("Design system"),
                        V.Display("Borealis lounge"),
                        V.Muted("Zażółć gęślą jaźń: Manrope and Space Grotesk, Lucide icons.").Wrap(),
                        V.Row(
                            V.Button("Join", onClick: () => Toasts.Show(ui, "Joined the room.", ToastKind.Success), icon: Icons.LogIn),
                            V.Button("Settings", ButtonVariant.Secondary, icon: Icons.Settings),
                            V.Button("Leave", ButtonVariant.Danger, OpenLeaveDialog),
                            V.IconButton(Icons.Heart, "Like"))
                        .Gap(8),
                        V.Row(V.Button("Go live", ButtonVariant.Live, icon: Icons.Radio), V.Button("Quiet", ButtonVariant.Quiet)).Gap(8),
                        V.Divider(),
                        V.FormField("Display name", V.TextField(name, "Type a name"), NameError),
                        V.Switch(sound, "Sound effects"),
                        V.Row(V.Icon(Icons.Volume2), V.Slider(volume).Grow(), V.Small("").Text(() => $"{volume.Value:P0}").Width(44)).Gap(10).FillWidth(),
                        V.Button("Save", onClick: Save).Width(140).Enabled(() => NameError() is null).Bind(() => saving.Value, (b, v) => b.Loading = v))
                    .Width(460).Padding(28).Gap(14),
                V.Card(
                        V.Tabs(tab, "Queue", "History", "Empty"),
                        V.Pages(() => tab.Value,
                            () => V.Column(
                                    V.For(() => tracks.Value, t => t.Id, (track, index) =>
                                        V.ListItem(track.Value.Title, track.Value.Artist)
                                            .Leading(V.Icon(Icons.Music))
                                            .Trailing(V.IconButton(Icons.X, "Remove", () => tracks.Value = [.. tracks.Value.Where(t => t.Id != track.Value.Id)])))
                                        .Gap(2),
                                    V.Button("Add track", ButtonVariant.Secondary, () => tracks.Value = [.. tracks.Value, new(next, $"Track {next++}", "Unknown")], Icons.Plus))
                                .Gap(10),
                            () => V.Column(V.Text("Played tonight"), V.Muted("Nothing yet.")).Gap(6),
                            () => V.EmptyState(Icons.Inbox, "No requests", "Requests from the audience appear here.")))
                    .Width(420).Padding(24).Gap(14))
            .Gap(24).Fill().Center());

        void Save()
        {
            saving.Value = true;
            _ui!.Root.After(1.2f, () =>
            {
                saving.Value = false;
                Toasts.Show(ui, $"Saved {(name.Value.Length == 0 ? "settings" : name.Value)}.", ToastKind.Info);
            });
        }

        void OpenLeaveDialog()
        {
            Dialog? dialog = null;
            dialog = Dialog.Open(ui, "Leave the room?", V.Muted("You can come back at any time; your place in the queue is lost.").Wrap(), true,
                V.Button("Stay", ButtonVariant.Quiet, () => dialog!.Close()),
                V.Button("Leave", ButtonVariant.Danger, () =>
                {
                    dialog!.Close();
                    Toasts.Show(ui, "Left the room.", ToastKind.Warning);
                }));
        }
    }

    public override void Update()
    {
        if (Input.GetKeyDown(Key.F1))
        {
            SceneManager.LoadScene("Scenes/Main.mnvt");
        }
    }

    public override void OnDestroy() => _ui?.Dispose();
}
