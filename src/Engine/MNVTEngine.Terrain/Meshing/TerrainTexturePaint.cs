namespace MNVTEngine.Terrain.Meshing;

public enum TextureBrushShape
{
    SoftRound,
    MediumRound,
    HardRound,
    SoftSquare,
    HardSquare,
    Noise,
    Speckle,
    Ridge,
}

public sealed record TexturePaintStroke(double U, double V, double RadiusU, double RadiusV, int Layer, double Opacity, double Hardness, double Rotation, TextureBrushShape Shape, int Seed = 0);

/// <summary>A compact RGBA splat map: each texel's four bytes (one per texture layer) always sum to 255.</summary>
public sealed class TerrainTexturePaint
{
    public const int LayerCount = 4;
    public const int DefaultResolution = 128;

    /// <exception cref="ArgumentOutOfRangeException">The resolution is not an integer from 16 to 512.</exception>
    public TerrainTexturePaint(int resolution = DefaultResolution, byte[]? data = null)
    {
        if (resolution is < 16 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Terrain control-map resolution must be an integer from 16 to 512.");
        }

        Resolution = resolution;
        var length = resolution * resolution * LayerCount;
        Data = data?.Length == length ? (byte[])data.Clone() : new byte[length];
        if (data is null)
        {
            for (var offset = 0; offset < length; offset += 4)
            {
                Data[offset] = 255;
            }
        }
    }

    public int Resolution { get; }

    public byte[] Data { get; }

    public static string ShapeName(TextureBrushShape shape) => shape switch
    {
        TextureBrushShape.SoftRound => "soft-round",
        TextureBrushShape.MediumRound => "medium-round",
        TextureBrushShape.HardRound => "hard-round",
        TextureBrushShape.SoftSquare => "soft-square",
        TextureBrushShape.HardSquare => "hard-square",
        TextureBrushShape.Noise => "noise",
        TextureBrushShape.Speckle => "speckle",
        _ => "ridge",
    };

    public bool Paint(TexturePaintStroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        var layer = Math.Clamp(stroke.Layer, 0, 3);
        var radiusU = Math.Max(0.5 / Resolution, stroke.RadiusU);
        var radiusV = Math.Max(0.5 / Resolution, stroke.RadiusV);
        var opacity = Clamp01(stroke.Opacity);
        var hardness = Clamp01(stroke.Hardness);
        var cos = Math.Cos(stroke.Rotation);
        var sin = Math.Sin(stroke.Rotation);
        var minX = Math.Max(0, (int)Math.Floor((stroke.U - radiusU) * Resolution));
        var maxX = Math.Min(Resolution - 1, (int)Math.Ceiling((stroke.U + radiusU) * Resolution));
        var minY = Math.Max(0, (int)Math.Floor((stroke.V - radiusV) * Resolution));
        var maxY = Math.Min(Resolution - 1, (int)Math.Ceiling((stroke.V + radiusV) * Resolution));
        var changed = false;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var du = (((x + 0.5) / Resolution) - stroke.U) / radiusU;
                var dv = (((y + 0.5) / Resolution) - stroke.V) / radiusV;
                var rx = (du * cos) - (dv * sin);
                var ry = (du * sin) + (dv * cos);
                var isSquare = stroke.Shape is TextureBrushShape.SoftSquare or TextureBrushShape.HardSquare;
                var distance = isSquare ? Math.Max(Math.Abs(rx), Math.Abs(ry)) : Math.Sqrt((rx * rx) + (ry * ry));
                if (distance > 1)
                {
                    continue;
                }

                var effectiveHardness = stroke.Shape == TextureBrushShape.MediumRound ? Math.Max(0.45, hardness)
                    : stroke.Shape is TextureBrushShape.HardRound or TextureBrushShape.HardSquare ? 0.98
                    : hardness;
                var falloff = 1 - Smoothstep(effectiveHardness, 1, distance);
                if (stroke.Shape == TextureBrushShape.Noise)
                {
                    falloff *= 0.35 + (HashNoise(x, y, stroke.Seed) * 0.65);
                }
                else if (stroke.Shape == TextureBrushShape.Speckle)
                {
                    falloff *= HashNoise(x, y, stroke.Seed) > 0.58 ? 1 : 0.08;
                }
                else if (stroke.Shape == TextureBrushShape.Ridge)
                {
                    var noise = HashNoise(x, y, stroke.Seed);
                    falloff *= 0.18 + (Math.Abs(Math.Sin(((rx + ry) * 12) + (noise * 2.5))) * 0.82);
                }

                var blend = Clamp01(opacity * falloff);
                if (blend <= 0.0001)
                {
                    continue;
                }

                var offset = ((y * Resolution) + x) * 4;
                var previous = Data[layer + offset];
                var target = (int)Geometry.MathUtil.JsRound(previous + ((255 - previous) * blend));
                if (target == previous)
                {
                    continue;
                }

                var oldOtherTotal = 255 - previous;
                var newOtherTotal = 255 - target;
                for (var channel = 0; channel < 4; channel++)
                {
                    if (channel == layer)
                    {
                        continue;
                    }

                    Data[offset + channel] = oldOtherTotal > 0
                        ? (byte)Geometry.MathUtil.JsRound(Data[offset + channel] * newOtherTotal / (double)oldOtherTotal)
                        : (byte)0;
                }

                Data[offset + layer] = (byte)target;
                FixSum(offset, layer);
                changed = true;
            }
        }

        return changed;
    }

    public void Clear(int layer = 0)
    {
        var channel = Math.Clamp(layer, 0, 3);
        Array.Clear(Data);
        for (var offset = 0; offset < Data.Length; offset += 4)
        {
            Data[offset + channel] = 255;
        }
    }

    /// <summary>Run-length encoded (count, r, g, b, a) in base64, as TerrainHeaven saves it.</summary>
    public string EncodeRle()
    {
        var output = new List<byte>();
        for (var offset = 0; offset < Data.Length;)
        {
            var count = 1;
            while (count < 255 && offset + (count * 4) < Data.Length && RgbaEqual(offset, offset + (count * 4)))
            {
                count++;
            }

            output.Add((byte)count);
            output.AddRange(Data.AsSpan(offset, 4).ToArray());
            offset += count * 4;
        }

        return Convert.ToBase64String([.. output]);
    }

    /// <exception cref="FormatException">The data is not valid base64 or does not fill the map.</exception>
    public static TerrainTexturePaint DecodeRle(int resolution, string encoded)
    {
        var output = new byte[resolution * resolution * 4];
        var binary = Convert.FromBase64String(encoded);
        var outputOffset = 0;
        for (var index = 0; index + 4 < binary.Length && outputOffset < output.Length; index += 5)
        {
            var count = binary[index];
            for (var repeat = 0; repeat < count && outputOffset < output.Length; repeat++)
            {
                binary.AsSpan(index + 1, 4).CopyTo(output.AsSpan(outputOffset));
                outputOffset += 4;
            }
        }

        if (outputOffset != output.Length)
        {
            throw new FormatException("Invalid terrain control-map data.");
        }

        return new TerrainTexturePaint(resolution, output);
    }

    private void FixSum(int offset, int preferredChannel)
    {
        var sum = 0;
        for (var channel = 0; channel < 4; channel++)
        {
            sum += Data[offset + channel];
        }

        var corrected = Data[offset + preferredChannel] + 255 - sum;
        Data[offset + preferredChannel] = (byte)Math.Clamp(corrected, 0, 255);
    }

    private bool RgbaEqual(int a, int b) =>
        Data[a] == Data[b] && Data[a + 1] == Data[b + 1] && Data[a + 2] == Data[b + 2] && Data[a + 3] == Data[b + 3];

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    private static double Smoothstep(double a, double b, double value)
    {
        if (a >= b)
        {
            return value >= b ? 1 : 0;
        }

        var t = Clamp01((value - a) / (b - a));
        return t * t * (3 - (2 * t));
    }

    /// <summary>The JavaScript hash (Math.imul and unsigned shifts) bit for bit.</summary>
    internal static double HashNoise(int x, int y, int seed)
    {
        var value = unchecked(((x ^ seed) * 374761393) + ((y + seed) * 668265263));
        value = unchecked((value ^ (int)((uint)value >> 13)) * 1274126177);
        return (uint)(value ^ (int)((uint)value >> 16)) / (double)0xffffffff;
    }
}
