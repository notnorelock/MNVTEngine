using System.Runtime.InteropServices;

namespace MNVTEngine.Editor;

/// <summary>Docking layout construction. ImGui.NET does not wrap the DockBuilder API, so it is bound from cimgui directly.</summary>
internal static partial class DockBuilder
{
    public const int DockSpaceFlag = 1 << 10; // ImGuiDockNodeFlags_DockSpace (internal)

    public enum Direction
    {
        Left = 0,
        Right = 1,
        Up = 2,
        Down = 3,
    }

    [LibraryImport("cimgui", EntryPoint = "igDockBuilderGetNode")]
    public static partial nint GetNode(uint nodeId);

    [LibraryImport("cimgui", EntryPoint = "igDockBuilderAddNode")]
    public static partial uint AddNode(uint nodeId, int flags);

    [LibraryImport("cimgui", EntryPoint = "igDockBuilderRemoveNode")]
    public static partial void RemoveNode(uint nodeId);

    // ImVec2 is passed by value; LibraryImport cannot marshal structs without disabling runtime marshalling assembly-wide.
    [DllImport("cimgui", EntryPoint = "igDockBuilderSetNodeSize")]
    public static extern void SetNodeSize(uint nodeId, System.Numerics.Vector2 size);

    [LibraryImport("cimgui", EntryPoint = "igDockBuilderSplitNode")]
    public static partial uint SplitNode(uint nodeId, Direction direction, float ratio, out uint idAtDirection, out uint idAtOpposite);

    [LibraryImport("cimgui", EntryPoint = "igDockBuilderDockWindow", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void DockWindow(string windowName, uint nodeId);

    [LibraryImport("cimgui", EntryPoint = "igDockBuilderFinish")]
    public static partial void Finish(uint nodeId);
}
