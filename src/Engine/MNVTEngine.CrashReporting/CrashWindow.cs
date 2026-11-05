using System.Diagnostics;
using System.Runtime.InteropServices;
using StbImageWriteSharp;

namespace MNVTEngine.CrashReporting;

/// <summary>
/// The crash handler's window (Windows): "&lt;Product&gt; - MNVTEngine &lt;version&gt;", the engine's logo with a red
/// "!" badge and a progress bar while the report is written; then where the report is, with Open Folder and Close.
/// Plain Win32 with themed common controls: the handler process loads nothing else.
/// </summary>
internal static unsafe class CrashWindow
{
    private const int ClientWidth = 390;
    private const int ClientHeight = 222;
    private const int OpenId = 1, CloseId = 2;
    private const uint ProgressMessage = Native.WmApp + 1, DoneMessage = Native.WmApp + 2;
    private const nuint AnimationTimer = 1;

    private static State? _state;

    private sealed class State
    {
        public required string Title { get; init; }

        public required string Product { get; init; }

        public nint Window { get; set; }

        public nint Progress { get; set; }

        public nint Open { get; set; }

        public nint Close { get; set; }

        public nint Font { get; set; }

        public nint BoldFont { get; set; }

        /// <summary>The worker's progress (0–1) and what the bar shows; the bar moves at most so fast that it can be seen.</summary>
        public double Target { get; set; }

        public double Shown { get; set; }

        public CrashReportResult? Result { get; set; }

        public string? Screenshot { get; init; }

        public bool ProgressShot { get; set; }
    }

    /// <summary>
    /// A window only for people: not on CI, not with <c>MNVT_CRASH_UI=0</c>, not when both stdout and stderr go to pipes or
    /// files (a test runner or a script). <c>MNVT_CRASH_UI=1</c> forces it.
    /// </summary>
    public static bool ShouldShow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        switch (Environment.GetEnvironmentVariable(CrashReporter.WindowVariable))
        {
            case "0":
                return false;
            case "1":
                return true;
        }

        if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("CI") is not null || Environment.GetEnvironmentVariable("TF_BUILD") is not null)
        {
            return false;
        }

        static bool Redirected(int handle) => Native.GetFileType(Native.GetStdHandle(handle)) is Native.FileTypeDisk or Native.FileTypePipe;
        return !(Redirected(Native.StdOutput) && Redirected(Native.StdError));
    }

    /// <summary>Shows the window while <paramref name="report"/> is collected; returns when it is closed.</summary>
    public static CrashReportResult Show(CrashReporterOptions options, CrashReport report)
    {
        EnableVisualStyles();
        var init = new Native.InitCommonControls { Size = (uint)sizeof(Native.InitCommonControls), Classes = Native.IccProgressClass };
        Native.InitCommonControlsEx(&init);
        var state = new State
        {
            Title = $"{options.ProductName} - MNVTEngine {options.EngineVersion}",
            Product = options.ProductName,
            Screenshot = Environment.GetEnvironmentVariable(CrashReporter.ScreenshotVariable),
        };
        _state = state;
        Create(state);

        var worker = new Thread(() =>
        {
            var progress = new Progress(state);
            CrashReportResult result;
            try
            {
                result = report.Collect(progress);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result = new CrashReportResult($"(the report could not be written: {ex.Message})", false, null);
            }

            state.Result = result;
            Native.PostMessageW(state.Window, DoneMessage, 0, 0);
        }) { IsBackground = true, Name = "Crash report" };
        worker.Start();

        Native.Msg message;
        while (Native.GetMessageW(&message, 0, 0, 0) > 0)
        {
            if (!Native.IsDialogMessageW(state.Window, &message)) // Tab and Enter between the buttons
            {
                Native.TranslateMessage(&message);
                Native.DispatchMessageW(&message);
            }
        }

        worker.Join();
        Native.DeleteObject(state.Font);
        Native.DeleteObject(state.BoldFont);
        _state = null;
        return state.Result!;
    }

    private sealed class Progress(State state) : IProgress<double>
    {
        public void Report(double value)
        {
            state.Target = Math.Clamp(value, 0d, 1d);
            Native.PostMessageW(state.Window, ProgressMessage, 0, 0);
        }
    }

    private static void Create(State state)
    {
        var instance = Native.GetModuleHandleW(null);
        fixed (char* className = "MNVTEngineCrashWindow")
        {
            var windowClass = new Native.WndClassEx
            {
                Size = (uint)sizeof(Native.WndClassEx),
                WndProc = &WindowProc,
                Instance = instance,
                Cursor = Native.LoadCursorW(0, 32512), // IDC_ARROW
                Background = Native.GetSysColorBrush(Native.ColorWindow),
                ClassName = className,
            };
            Native.RegisterClassExW(&windowClass);
        }

        const uint style = Native.WsOverlapped | Native.WsCaption | Native.WsSysMenu | Native.WsMinimizeBox | 0x02000000; // WS_CLIPCHILDREN: repaints keep the buttons
        var frame = new Native.Rect { Right = ClientWidth, Bottom = ClientHeight };
        Native.AdjustWindowRectEx(&frame, style, false, 0);
        var (width, height) = (frame.Right - frame.Left, frame.Bottom - frame.Top);
        var x = (Native.GetSystemMetrics(Native.SmCxScreen) - width) / 2;
        var y = (Native.GetSystemMetrics(Native.SmCyScreen) - height) / 2;
        state.Window = Native.CreateWindowExW(0x00040000 /* WS_EX_APPWINDOW */, "MNVTEngineCrashWindow", state.Title, style, x, y, width, height, 0, 0, instance, 0);
        state.Font = Native.CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        state.BoldFont = Native.CreateFontW(-15, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        state.Progress = Native.CreateWindowExW(0, "msctls_progress32", null, Native.WsChild | Native.WsVisible, 24, 190, ClientWidth - 48, 12, state.Window, 0, instance, 0);
        Native.SendMessageW(state.Progress, Native.PbmSetRange32, 0, 1000);
        state.Open = Button(state, "Open Folder", ClientWidth - 24 - 88 - 8 - 88, OpenId, Native.BsDefPushButton);
        state.Close = Button(state, "Close", ClientWidth - 24 - 88, CloseId, Native.BsPushButton);
        Native.ShowWindow(state.Window, Native.SwShow);
        Native.SetForegroundWindow(state.Window);
        Native.SetTimer(state.Window, AnimationTimer, 16, 0);
    }

    private static nint Button(State state, string text, int x, int id, uint style)
    {
        var button = Native.CreateWindowExW(0, "BUTTON", text, Native.WsChild | Native.WsTabStop | style, x, ClientHeight - 24 - 26, 88, 26, state.Window, id, Native.GetModuleHandleW(null), 0);
        Native.SendMessageW(button, Native.WmSetFont, state.Font, 1);
        return button;
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (_state is not { } state)
        {
            return Native.DefWindowProcW(window, message, wParam, lParam);
        }

        switch (message)
        {
            case Native.WmPaint:
                Paint(state, window);
                return 0;
            case ProgressMessage:
                return 0; // the animation timer moves the bar towards the new target
            case Native.WmTimer:
                Animate(state);
                return 0;
            case DoneMessage:
                Animate(state);
                return 0;
            case Native.WmCommand when (int)(wParam & 0xFFFF) == OpenId && state.Result is { } result && Directory.Exists(result.Folder):
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{result.Folder}\"") { UseShellExecute = false })?.Dispose();
                return 0;
            case Native.WmCommand when (int)(wParam & 0xFFFF) == CloseId:
            case Native.WmClose when state.Result is not null:
                Native.DestroyWindow(window);
                return 0;
            case Native.WmClose:
                return 0; // still writing the report
            case Native.WmDestroy:
                Native.KillTimer(window, AnimationTimer);
                Native.PostQuitMessage(0);
                return 0;
        }

        return Native.DefWindowProcW(window, message, wParam, lParam);
    }

    /// <summary>Moves the bar (about a second for a whole report) and switches to the result once both are done.</summary>
    private static void Animate(State state)
    {
        if (state.Shown >= 1d && state.Result is not null)
        {
            return;
        }

        state.Shown = Math.Min(state.Target, state.Shown + 0.02);
        Native.SendMessageW(state.Progress, Native.PbmSetPos, (nint)(state.Shown * 1000), 0);
        if (state.Screenshot is { } shot && !state.ProgressShot && state.Shown >= 0.4)
        {
            state.ProgressShot = true;
            Capture(state.Window, Path.ChangeExtension(shot, null) + "-progress.png");
        }

        if (state.Shown >= 1d && state.Result is not null)
        {
            Native.KillTimer(state.Window, AnimationTimer);
            Native.ShowWindow(state.Progress, Native.SwHide);
            Native.ShowWindow(state.Open, Native.SwShow);
            Native.ShowWindow(state.Close, Native.SwShow);
            Native.InvalidateRect(state.Window, null, true);
            if (state.Screenshot is { } final)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(300); // after the repaint
                    Capture(state.Window, final);
                    Native.PostMessageW(state.Window, Native.WmCommand, CloseId, 0);
                });
            }
        }
    }

    private static void Paint(State state, nint window)
    {
        Native.PaintStruct paint;
        var hdc = Native.BeginPaint(window, &paint);
        DrawLogo(hdc, (ClientWidth / 2) - 48, state.Result is not null && state.Shown >= 1d ? 14 : 40);
        if (state.Result is { } result && state.Shown >= 1d)
        {
            Native.SetBkMode(hdc, Native.Transparent);
            Native.SetTextColor(hdc, Native.Rgb(0x1F1F1F));
            Native.SelectObject(hdc, state.BoldFont);
            var line = new Native.Rect { Left = 24, Top = 116, Right = ClientWidth - 24, Bottom = 138 };
            Native.DrawTextW(hdc, $"{state.Product} has crashed.", -1, &line, Native.DtCenter | Native.DtSingleLine | Native.DtEndEllipsis);
            Native.SelectObject(hdc, state.Font);
            Native.SetTextColor(hdc, Native.Rgb(0x505050));
            var detail = new Native.Rect { Left = 24, Top = 140, Right = ClientWidth - 24, Bottom = 172 };
            var dump = result.HasDump ? "A crash report with a memory dump was saved to:" : "A crash report was saved to:";
            Native.DrawTextW(hdc, dump, -1, &detail, Native.DtCenter | Native.DtSingleLine);
            detail.Top += 16;
            Native.DrawTextW(hdc, result.Folder, -1, &detail, Native.DtCenter | Native.DtSingleLine | Native.DtPathEllipsis);
        }

        Native.EndPaint(window, &paint);
    }

    /// <summary>The MNVTEngine mark (an "M" on a dark tile with a cyan bar) with a red "!" badge, 96×96 at (x, y).</summary>
    private static void DrawLogo(nint hdc, int x, int y)
    {
        var tile = Native.CreateSolidBrush(Native.Rgb(0x262A33));
        var tilePen = Native.CreatePen(0, 1, Native.Rgb(0x262A33));
        var oldBrush = Native.SelectObject(hdc, tile);
        var oldPen = Native.SelectObject(hdc, tilePen);
        Native.RoundRect(hdc, x + 10, y + 10, x + 96, y + 96, 22, 22);

        var white = Native.CreateSolidBrush(Native.Rgb(0xFFFFFF));
        var whitePen = Native.CreatePen(0, 1, Native.Rgb(0xFFFFFF));
        Native.SelectObject(hdc, white);
        Native.SelectObject(hdc, whitePen);
        var (ox, oy) = (x + 10, y + 10);
        Native.Point* m = stackalloc Native.Point[12];
        ReadOnlySpan<int> shape = [17, 64, 17, 22, 30, 22, 43, 42, 56, 22, 69, 22, 69, 64, 58, 64, 58, 38, 43, 59, 28, 38, 28, 64];
        for (var i = 0; i < 12; i++)
        {
            m[i] = new Native.Point { X = ox + shape[i * 2], Y = oy + shape[(i * 2) + 1] };
        }

        Native.Polygon(hdc, m, 12);

        var accent = Native.CreateSolidBrush(Native.Rgb(0x2EC4F1));
        var accentPen = Native.CreatePen(0, 1, Native.Rgb(0x2EC4F1));
        Native.SelectObject(hdc, accent);
        Native.SelectObject(hdc, accentPen);
        var bar = new Native.Rect { Left = ox + 17, Top = oy + 69, Right = ox + 69, Bottom = oy + 74 };
        Native.FillRect(hdc, &bar, accent);

        // The badge: red square with a white "!", over the tile's top-left corner like a notification.
        var red = Native.CreateSolidBrush(Native.Rgb(0xE81123));
        var badge = new Native.Rect { Left = x - 6, Top = y - 2, Right = x + 22, Bottom = y + 26 };
        Native.FillRect(hdc, &badge, red);
        var font = Native.CreateFontW(-26, 0, 0, 0, 900, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var oldFont = Native.SelectObject(hdc, font);
        Native.SetBkMode(hdc, Native.Transparent);
        Native.SetTextColor(hdc, Native.Rgb(0xFFFFFF));
        Native.DrawTextW(hdc, "!", 1, &badge, Native.DtCenter | Native.DtVCenter | Native.DtSingleLine);

        Native.SelectObject(hdc, oldFont);
        Native.SelectObject(hdc, oldBrush);
        Native.SelectObject(hdc, oldPen);
        foreach (var obj in new[] { tile, tilePen, white, whitePen, accent, accentPen, red, font })
        {
            Native.DeleteObject(obj);
        }
    }

    /// <summary>Saves the whole window (title bar included) as a PNG: <c>MNVT_CRASH_UI_SCREENSHOT</c>, for tests and docs.</summary>
    private static void Capture(nint window, string path)
    {
        Native.Rect bounds;
        Native.GetWindowRect(window, &bounds);
        var (width, height) = (bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        var header = new Native.BitmapInfoHeader { Size = (uint)sizeof(Native.BitmapInfoHeader), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        void* bits;
        var memory = Native.CreateCompatibleDC(0);
        var bitmap = Native.CreateDIBSection(memory, &header, 0, &bits, 0, 0);
        var old = Native.SelectObject(memory, bitmap);
        Native.PrintWindow(window, memory, 2); // PW_RENDERFULLCONTENT
        var pixels = new byte[width * height * 4];
        Marshal.Copy((nint)bits, pixels, 0, pixels.Length);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2], pixels[i + 3]) = (pixels[i + 2], pixels[i], 255); // BGRA to RGBA, opaque
        }

        Native.SelectObject(memory, old);
        Native.DeleteObject(bitmap);
        Native.DeleteDC(memory);
        using var file = File.Create(path);
        new ImageWriter().WritePng(pixels, width, height, ColorComponents.RedGreenBlueAlpha, file);
    }

    /// <summary>Themed controls (the green progress bar, modern buttons) need common controls 6, which a manifest selects.</summary>
    private static void EnableVisualStyles()
    {
        var manifest = Path.Combine(Path.GetTempPath(), "MNVTEngine.CrashWindow.manifest");
        File.WriteAllText(manifest, """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <dependency><dependentAssembly>
                <assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0" processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*" />
              </dependentAssembly></dependency>
            </assembly>
            """);
        fixed (char* source = manifest)
        {
            var context = new Native.ActCtx { Size = (uint)sizeof(Native.ActCtx), Source = source };
            var handle = Native.CreateActCtxW(&context);
            if (handle != -1)
            {
                Native.ActivateActCtx(handle, out _); // for this thread, for the process's lifetime
            }
        }
    }
}
