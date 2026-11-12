using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Meshes;

/// <summary>
/// Mesh deformation on the CPU: blend shapes (<see cref="Morph"/>) and linear blend skinning (<see cref="Apply"/>).
/// Linear blend skinning: each vertex moves with up to four joints, weighted. The result is in the space the
/// joint matrices map to (world space when they are joint world × inverse bind), so a skinned mesh is drawn with an
/// identity world matrix. Normals and tangents use the same matrices (renormalized).
/// </summary>
public static class Skinning
{
    /// <param name="joints">Four joint indices per vertex.</param>
    /// <param name="weights">Four weights per vertex (summing to 1).</param>
    /// <param name="jointMatrices">Per joint: its world matrix × its inverse bind matrix.</param>
    /// <param name="bindPose">The mesh's vertices as uploaded (<see cref="GpuMesh.Interleave"/>).</param>
    /// <param name="output">Receives the skinned vertices; the same length as <paramref name="bindPose"/>.</param>
    /// <returns>The box around the skinned positions.</returns>
    public static Bounds Apply(ReadOnlySpan<MeshVertex> bindPose, ReadOnlySpan<ushort> joints, ReadOnlySpan<float> weights, ReadOnlySpan<Matrix4x4> jointMatrices, Span<MeshVertex> output)
    {
        if (output.Length != bindPose.Length || joints.Length < bindPose.Length * 4 || weights.Length < bindPose.Length * 4)
        {
            throw new ArgumentException("Skinning streams do not match the vertex count.", nameof(output));
        }

        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (var v = 0; v < bindPose.Length; v++)
        {
            var skin = default(Matrix4x4);
            for (var i = 0; i < 4; i++)
            {
                var weight = weights[(v * 4) + i];
                if (weight == 0f)
                {
                    continue;
                }

                var joint = joints[(v * 4) + i];
                if (joint >= jointMatrices.Length)
                {
                    throw new ArgumentException($"Vertex {v} uses joint {joint}, but there are {jointMatrices.Length}.", nameof(joints));
                }

                Accumulate(ref skin, jointMatrices[joint], weight);
            }

            var source = bindPose[v];
            var position = skin.MultiplyPoint3x4(source.Position);
            var tangent = skin.MultiplyVector(new Vector3(source.Tangent.X, source.Tangent.Y, source.Tangent.Z)).Normalized;
            output[v] = source with
            {
                Position = position,
                Normal = skin.MultiplyVector(source.Normal).Normalized,
                Tangent = new Vector4(tangent.X, tangent.Y, tangent.Z, source.Tangent.W),
            };
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        return bindPose.Length == 0 ? default : Bounds.FromMinMax(min, max);
    }

    /// <summary>
    /// Blend shapes: each vertex moves by the weighted sum of the shapes' offsets (normals and tangents too, then
    /// renormalized). Shapes with weight 0 cost nothing. Returns the box around the result.
    /// </summary>
    /// <param name="shapes">Per shape: position offsets, and optional normal and tangent offsets (one per vertex).</param>
    public static Bounds Morph(ReadOnlySpan<MeshVertex> bindPose, IReadOnlyList<(Vector3[] Positions, Vector3[]? Normals, Vector3[]? Tangents)> shapes, ReadOnlySpan<float> weights, Span<MeshVertex> output)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        if (output.Length != bindPose.Length)
        {
            throw new ArgumentException("The output does not match the vertex count.", nameof(output));
        }

        bindPose.CopyTo(output);
        var normalsChanged = false;
        for (var s = 0; s < shapes.Count && s < weights.Length; s++)
        {
            var w = weights[s];
            if (w == 0f)
            {
                continue;
            }

            var (positions, normals, tangents) = shapes[s];
            if (positions.Length != bindPose.Length)
            {
                throw new ArgumentException($"Blend shape {s} has {positions.Length} offsets for {bindPose.Length} vertices.", nameof(shapes));
            }

            for (var v = 0; v < output.Length; v++)
            {
                ref var vertex = ref output[v];
                vertex.Position += positions[v] * w;
                if (normals is not null)
                {
                    vertex.Normal += normals[v] * w;
                }

                if (tangents is not null)
                {
                    var t = tangents[v] * w;
                    vertex.Tangent = new Vector4(vertex.Tangent.X + t.X, vertex.Tangent.Y + t.Y, vertex.Tangent.Z + t.Z, vertex.Tangent.W);
                }
            }

            normalsChanged |= normals is not null || tangents is not null;
        }

        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (var v = 0; v < output.Length; v++)
        {
            ref var vertex = ref output[v];
            if (normalsChanged)
            {
                vertex.Normal = vertex.Normal.Normalized;
                var t = new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z).Normalized;
                vertex.Tangent = new Vector4(t.X, t.Y, t.Z, vertex.Tangent.W);
            }

            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        return output.Length == 0 ? default : Bounds.FromMinMax(min, max);
    }

    private static void Accumulate(ref Matrix4x4 sum, in Matrix4x4 m, float w)
    {
        sum.M00 += m.M00 * w; sum.M10 += m.M10 * w; sum.M20 += m.M20 * w; sum.M30 += m.M30 * w;
        sum.M01 += m.M01 * w; sum.M11 += m.M11 * w; sum.M21 += m.M21 * w; sum.M31 += m.M31 * w;
        sum.M02 += m.M02 * w; sum.M12 += m.M12 * w; sum.M22 += m.M22 * w; sum.M32 += m.M32 * w;
        sum.M03 += m.M03 * w; sum.M13 += m.M13 * w; sum.M23 += m.M23 * w; sum.M33 += m.M33 * w;
    }
}
