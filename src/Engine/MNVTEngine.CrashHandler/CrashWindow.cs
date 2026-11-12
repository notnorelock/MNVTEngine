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

        /// <summary>The wordmark as a premultiplied bitmap selected into its own memory DC.</summary>
        public (nint Dc, nint Bitmap, int Width, int Height)? Logo { get; set; }
    }

    /// <summary>
    /// A window only for people: not on CI, not with <c>MNVT_CRASH_UI=0</c>, not when both stdout and stderr go to pipes or
    /// files (a test runner or a script). <c>MNVT_CRASH_UI=1</c> forces it.
    /// </summary>
    /// <param name="quiet">The game was run by a tool (redirected output): no window unless MNVT_CRASH_UI=1 asks for one.</param>
    public static bool ShouldShow(bool quiet = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        switch (Environment.GetEnvironmentVariable(CrashProtocol.WindowVariable))
        {
            case "0":
                return false;
            case "1":
                return true;
        }

        if (quiet || !Environment.UserInteractive || Environment.GetEnvironmentVariable("CI") is not null || Environment.GetEnvironmentVariable("TF_BUILD") is not null)
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
            Screenshot = Environment.GetEnvironmentVariable(CrashProtocol.ScreenshotVariable),
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
        if (state.Logo is { } logo)
        {
            Native.DeleteDC(logo.Dc);
            Native.DeleteObject(logo.Bitmap);
        }

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
        state.Logo = LoadLogo();
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
        DrawLogo(hdc, state, state.Result is not null && state.Shown >= 1d ? 30 : 64);
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

    /// <summary>The MNVTEngine wordmark, centered at <paramref name="y"/>, with a red "!" badge at its top-left corner.</summary>
    private static void DrawLogo(nint hdc, State state, int y)
    {
        var x = ClientWidth / 2;
        if (state.Logo is { } logo)
        {
            x -= logo.Width / 2;
            Native.AlphaBlend(hdc, x, y, logo.Width, logo.Height, logo.Dc, 0, 0, logo.Width, logo.Height, 0x01FF0000u); // premultiplied, opaque overall
        }

        // The badge: red square with a white "!", over the wordmark's top-left corner like a notification.
        var red = Native.CreateSolidBrush(Native.Rgb(0xE81123));
        var badge = new Native.Rect { Left = x - 20, Top = y - 18, Right = x + 6, Bottom = y + 8 };
        Native.FillRect(hdc, &badge, red);
        var font = Native.CreateFontW(-24, 0, 0, 0, 900, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var oldFont = Native.SelectObject(hdc, font);
        Native.SetBkMode(hdc, Native.Transparent);
        Native.SetTextColor(hdc, Native.Rgb(0xFFFFFF));
        Native.DrawTextW(hdc, "!", 1, &badge, Native.DtCenter | Native.DtVCenter | Native.DtSingleLine);
        Native.SelectObject(hdc, oldFont);
        Native.DeleteObject(font);
        Native.DeleteObject(red);
    }

    /// <summary>Decodes the embedded wordmark into a premultiplied DIB (what AlphaBlend needs); null if it cannot be read.</summary>
    private static (nint Dc, nint Bitmap, int Width, int Height)? LoadLogo()
    {
        using var stream = typeof(CrashWindow).Assembly.GetManifestResourceStream("MNVTEngine.CrashReporting.logo-dark.png");
        if (stream is null)
        {
            return null;
        }

        var image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        var header = new Native.BitmapInfoHeader { Size = (uint)sizeof(Native.BitmapInfoHeader), Width = image.Width, Height = -image.Height, Planes = 1, BitCount = 32 };
        void* bits;
        var dc = Native.CreateCompatibleDC(0);
        var bitmap = Native.CreateDIBSection(dc, &header, 0, &bits, 0, 0);
        var pixels = new Span<byte>(bits, image.Width * image.Height * 4);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = image.Data[i + 3];
            pixels[i] = (byte)(image.Data[i + 2] * a / 255); // BGRA, premultiplied
            pixels[i + 1] = (byte)(image.Data[i + 1] * a / 255);
            pixels[i + 2] = (byte)(image.Data[i] * a / 255);
            pixels[i + 3] = a;
        }

        Native.SelectObject(dc, bitmap);
        return (dc, bitmap, image.Width, image.Height);
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
