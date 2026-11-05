using System.Runtime.InteropServices;

namespace MNVTEngine.Editor;

/// <summary>
/// The editor's menu bar as a native Win32 menu (HMENU) on the GLFW window: the system's look, fonts and keyboard
/// handling. Each top-level menu is filled when it opens (WM_INITMENUPOPUP), so enabled and checked states are current.
/// A chosen item runs at the start of the next frame (<see cref="RunPending"/>), not inside the window procedure.
/// Alt+letter opens a menu (File is Alt+F); Alt alone does not, because Alt + drag orbits the 3D Scene view.
/// </summary>
internal sealed unsafe partial class NativeMenuBar : IDisposable
{
    private const int GwlpWndProc = -4;
    private const uint WmCommand = 0x0111;
    private const uint WmSysCommand = 0x0112;
    private const uint WmInitMenuPopup = 0x0117;
    private const nint ScKeyMenu = 0xF100;
    private const uint MfString = 0x0, MfGrayed = 0x1, MfChecked = 0x8, MfPopup = 0x10, MfSeparator = 0x800, MfByPosition = 0x400;

    private static NativeMenuBar? s_instance; // the window procedure is static: one editor window per process

    private readonly nint _hwnd;
    private readonly nint _bar;
    private readonly nint _previousProc;
    private readonly List<(nint Popup, TopMenu Menu)> _menus = [];
    private readonly Dictionary<int, Action> _commands = [];
    private readonly Queue<Action> _pending = [];
    private int _nextCommand = 1;

    private NativeMenuBar(nint hwnd, IReadOnlyList<TopMenu> menus)
    {
        _hwnd = hwnd;
        _bar = CreateMenu();
        foreach (var menu in menus)
        {
            var popup = CreatePopupMenu();
            AppendMenuW(_bar, MfPopup, popup, "&" + Escape(menu.Title));
            _menus.Add((popup, menu));
        }

        s_instance = this;
        _previousProc = SetWindowLongPtrW(hwnd, GwlpWndProc, (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc);
        SetMenu(hwnd, _bar);
        DrawMenuBar(hwnd);
    }

    /// <summary>The native menu bar on Windows; null elsewhere (the caller draws an ImGui menu bar instead).</summary>
    public static NativeMenuBar? TryCreate(nint hwnd, IReadOnlyList<TopMenu> menus) =>
        OperatingSystem.IsWindows() && hwnd != 0 && s_instance is null ? new NativeMenuBar(hwnd, menus) : null;

    /// <summary>Runs the items chosen since the last call.</summary>
    public void RunPending()
    {
        while (_pending.TryDequeue(out var action))
        {
            action();
        }
    }

    public void Dispose()
    {
        if (s_instance != this)
        {
            return;
        }

        SetWindowLongPtrW(_hwnd, GwlpWndProc, _previousProc);
        SetMenu(_hwnd, 0);
        DestroyMenu(_bar);
        s_instance = null;
    }

    /// <summary>The menu's text: a literal '&amp;' doubled (single ones mark the Alt+letter key), the shortcut after a tab.</summary>
    internal static string ItemText(MenuEntry entry) => entry.Shortcut is { } shortcut ? $"{Escape(entry.Label)}\t{shortcut}" : Escape(entry.Label);

    private static string Escape(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    private void Fill(nint popup, IReadOnlyList<MenuEntry> entries)
    {
        while (GetMenuItemCount(popup) > 0)
        {
            DeleteMenu(popup, 0, MfByPosition); // destroys submenus too
        }

        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                AppendMenuW(popup, MfSeparator, 0, null);
            }
            else if (entry.Children is { } children)
            {
                var submenu = CreatePopupMenu();
                Fill(submenu, children);
                AppendMenuW(popup, MfPopup | (entry.Enabled ? 0 : MfGrayed), submenu, Escape(entry.Label));
            }
            else
            {
                var id = _nextCommand++;
                if (entry.Action is { } action)
                {
                    _commands[id] = action;
                }

                AppendMenuW(popup, MfString | (entry.Enabled ? 0 : MfGrayed) | (entry.Checked ? MfChecked : 0), id, ItemText(entry));
            }
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        var self = s_instance!;
        switch (message)
        {
            case WmInitMenuPopup:
                foreach (var (popup, menu) in self._menus)
                {
                    if (popup == wParam)
                    {
                        // One top-level menu is open at a time: earlier commands are gone.
                        self._commands.Clear();
                        self._nextCommand = 1;
                        self.Fill(popup, menu.Items());
                        return 0;
                    }
                }

                break;
            case WmCommand when (wParam >> 16) == 0 && lParam == 0:
                if (self._commands.TryGetValue((int)(wParam & 0xFFFF), out var action))
                {
                    self._pending.Enqueue(action);
                }

                return 0;
            case WmSysCommand when (wParam & 0xFFF0) == ScKeyMenu:
                // lParam is the letter for Alt+letter; 0 for Alt alone or F10, which GLFW swallows (kept).
                return lParam != 0 ? DefWindowProcW(hwnd, message, wParam, lParam) : 0;
        }

        return CallWindowProcW(self._previousProc, hwnd, message, wParam, lParam);
    }

    [LibraryImport("user32")]
    private static partial nint CreateMenu();

    [LibraryImport("user32")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nint idOrSubmenu, string? text);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteMenu(nint menu, uint position, uint flags);

    [LibraryImport("user32")]
    private static partial int GetMenuItemCount(nint menu);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenu(nint hwnd, nint menu);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawMenuBar(nint hwnd);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [LibraryImport("user32")]
    private static partial nint CallWindowProcW(nint previous, nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32")]
    private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
}
