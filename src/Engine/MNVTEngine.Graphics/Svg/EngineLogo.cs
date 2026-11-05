namespace MNVTEngine.Graphics.Svg;

/// <summary>
/// The MNVTEngine wordmark (docs/images/mnvtengine-logo.svg, embedded in this assembly): white artwork, 711 x 172 SVG
/// units, for splash branding, the Project Hub and the like.
/// </summary>
public static class EngineLogo
{
    private const string Resource = "MNVTEngine.Graphics.mnvtengine-logo.svg";

    /// <summary>The SVG document.</summary>
    public static byte[] Svg()
    {
        using var stream = typeof(EngineLogo).Assembly.GetManifestResourceStream(Resource) ?? throw new InvalidOperationException($"The engine logo resource '{Resource}' is missing from the build.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The wordmark <paramref name="width"/> pixels wide (height by the aspect ratio), white or in <paramref name="color"/>.</summary>
    public static Image Render(int width, Mathematics.Color? color = null) =>
        SvgRasterizer.Render(Svg(), "mnvtengine-logo.svg", width / 711f, color).Image;
}
