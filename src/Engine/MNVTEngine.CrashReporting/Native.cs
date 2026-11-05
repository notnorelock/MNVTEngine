using System.Runtime.InteropServices;

namespace MNVTEngine.CrashReporting;

/// <summary>The Win32 calls the crash handler and its window use (Windows only; callers check the OS).</summary>
internal static unsafe partial class Native
{
    public const uint WsOverlapped = 0x00000000, WsCaption = 0x00C00000, WsSysMenu = 0x00080000, WsMinimizeBox = 0x00020000, WsVisible = 0x10000000, WsChild = 0x40000000, WsTabStop = 0x00010000;
    public const uint BsPushButton = 0x0, BsDefPushButton = 0x1;
    public const uint WmDestroy = 0x0002, WmPaint = 0x000F, WmClose = 0x0010, WmCommand = 0x0111, WmTimer = 0x0113, WmSetFont = 0x0030, WmApp = 0x8000;
    public const uint PbmSetRange32 = 0x0406, PbmSetPos = 0x0402;
    public const int SwShow = 5, SwHide = 0;
    public const int SmCxScreen = 0, SmCyScreen = 1;
    public const int ColorWindow = 5;
    public const int DtCenter = 0x1, DtVCenter = 0x4, DtSingleLine = 0x20, DtWordBreak = 0x10, DtEndEllipsis = 0x8000, DtPathEllipsis = 0x4000;
    public const int Transparent = 1;
    public const uint FileTypeDisk = 1, FileTypePipe = 3;
    public const int StdOutput = -11, StdError = -12;
    public const uint IccProgressClass = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaintStruct
    {
        public nint Hdc;
        public int Erase;
        public Rect Paint;
        public int Restore;
        public int IncUpdate;
        public fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ActCtx
    {
        public uint Size;
        public uint Flags;
        public char* Source;
        public ushort ProcessorArchitecture;
        public ushort LangId;
        public char* AssemblyDirectory;
        public char* ResourceName;
        public char* ApplicationName;
        public nint Module;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InitCommonControls
    {
        public uint Size;
        public uint Classes;
    }

    [LibraryImport("kernel32")]
    public static partial void RtlZeroMemory(nint destination, nint length);

    [LibraryImport("kernel32")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string? name);

    [LibraryImport("kernel32")]
    public static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32")]
    public static partial uint GetFileType(nint file);

    [LibraryImport("kernel32", SetLastError = true)]
    public static partial nint CreateActCtxW(ActCtx* context);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ActivateActCtx(nint context, out nuint cookie);

    [LibraryImport("comctl32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitCommonControlsEx(InitCommonControls* init);

    [LibraryImport("user32", SetLastError = true)]
    public static partial ushort RegisterClassExW(WndClassEx* windowClass);

    [LibraryImport("user32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint exStyle, string className, string? title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32")]
    public static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32")]
    public static partial int GetMessageW(Msg* message, nint window, uint min, uint max);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(Msg* message);

    [LibraryImport("user32")]
    public static partial nint DispatchMessageW(Msg* message);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsDialogMessageW(nint dialog, Msg* message);

    [LibraryImport("user32")]
    public static partial void PostQuitMessage(int code);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32")]
    public static partial nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint window);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32")]
    public static partial nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint window, nuint id);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint window, Rect* rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32")]
    public static partial nint BeginPaint(nint window, PaintStruct* paint);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint window, PaintStruct* paint);

    [LibraryImport("user32")]
    public static partial nint LoadCursorW(nint instance, nint name);

    [LibraryImport("user32")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustWindowRectEx(Rect* rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint window, Rect* rect);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(nint window, nint hdc, uint flags);

    [LibraryImport("user32")]
    public static partial void FillRect(nint hdc, Rect* rect, nint brush); // returns nonzero on success; drawing failures are not actionable

    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawTextW(nint hdc, string text, int length, Rect* rect, int format);

    [LibraryImport("user32")]
    public static partial nint GetSysColorBrush(int index);

    [LibraryImport("gdi32")]
    public static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32")]
    public static partial nint CreatePen(int style, int width, uint color);

    [LibraryImport("gdi32")]
    public static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RoundRect(nint hdc, int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Polygon(nint hdc, Point* points, int count);

    [LibraryImport("gdi32", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [LibraryImport("gdi32")]
    public static partial void SetBkMode(nint hdc, int mode); // the previous mode is not needed

    [LibraryImport("gdi32")]
    public static partial void SetTextColor(nint hdc, uint color); // the previous color is not needed

    [LibraryImport("gdi32")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32")]
    public static partial nint CreateDIBSection(nint hdc, BitmapInfoHeader* info, uint usage, void** bits, nint section, uint offset);

    [LibraryImport("dbghelp", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MiniDumpWriteDump(nint process, uint processId, nint file, uint dumpType, nint exception, nint userStream, nint callback);

    /// <summary>A COLORREF from 0xRRGGBB.</summary>
    public static uint Rgb(uint rgb) => ((rgb >> 16) & 0xFF) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16);
}
