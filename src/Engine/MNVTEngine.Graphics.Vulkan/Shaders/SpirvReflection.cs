using System.Runtime.InteropServices;
using System.Text;

namespace MNVTEngine.Graphics.Vulkan.Shaders;

/// <summary>One member of a uniform block. <see cref="Kind"/> is null for types materials cannot set (arrays, vec3, structs...).</summary>
/// <param name="ArrayLength">For arrays (such as <c>mat4 uJoints[128]</c>): the element count; 0 otherwise.</param>
/// <param name="ArrayStride">For arrays: bytes from one element to the next.</param>
/// <param name="ElementKind">For arrays: the kind of each element, when it is one materials know.</param>
internal sealed record SpirvUniformMember(string Name, int Offset, int Size, UniformKind? Kind, int ArrayLength = 0, int ArrayStride = 0, UniformKind? ElementKind = null);

internal sealed record SpirvUniformBlock(uint Set, uint Binding, int Size, IReadOnlyList<SpirvUniformMember> Members);

internal sealed record SpirvSampler(string Name, uint Set, uint Binding);

/// <param name="LocationWord">Index of the Location literal in the module's words, for re-linking.</param>
internal sealed record SpirvInterfaceVariable(string Name, uint Location, int LocationWord);

/// <summary>
/// The parts of a SPIR-V module the device needs: uniform blocks with member offsets, combined image samplers, and the
/// locations of stage inputs and outputs. A small decoder of the instructions involved, not a general SPIR-V library.
/// </summary>
internal sealed class SpirvModule
{
    private const uint MagicNumber = 0x07230203;

    private SpirvModule(uint[] words, List<SpirvUniformBlock> blocks, List<SpirvSampler> samplers, List<SpirvInterfaceVariable> inputs, List<SpirvInterfaceVariable> outputs, List<string> unsupported)
    {
        Words = words;
        UniformBlocks = blocks;
        Samplers = samplers;
        Inputs = inputs;
        Outputs = outputs;
        UnsupportedResources = unsupported;
    }

    public uint[] Words { get; }

    public IReadOnlyList<SpirvUniformBlock> UniformBlocks { get; }

    public IReadOnlyList<SpirvSampler> Samplers { get; }

    public IReadOnlyList<SpirvInterfaceVariable> Inputs { get; }

    public IReadOnlyList<SpirvInterfaceVariable> Outputs { get; }

    /// <summary>Descriptors the device cannot bind (storage buffers, separate images, push constants...), by name.</summary>
    public IReadOnlyList<string> UnsupportedResources { get; }

    public static SpirvModule Parse(uint[] words)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Length < 5 || words[0] != MagicNumber)
        {
            throw new InvalidDataException("Not a SPIR-V module.");
        }

        var names = new Dictionary<uint, string>();
        var memberNames = new Dictionary<(uint, uint), string>();
        var decorations = new Dictionary<(uint Id, uint Decoration), (uint Value, int Word)>();
        var memberOffsets = new Dictionary<(uint, uint), int>();
        var matrixStrides = new Dictionary<(uint, uint), int>();
        var arrayStrides = new Dictionary<uint, int>();
        var types = new Dictionary<uint, SpirvType>();
        var constants = new Dictionary<uint, uint>();
        var variables = new List<(uint Type, uint Id, uint Storage)>();

        for (var i = 5; i < words.Length;)
        {
            var count = (int)(words[i] >> 16);
            var op = words[i] & 0xFFFF;
            if (count == 0 || i + count > words.Length)
            {
                throw new InvalidDataException($"Malformed SPIR-V instruction at word {i}.");
            }

            var w = words.AsSpan(i, count);
            switch (op)
            {
                case 5: // OpName
                    names[w[1]] = ReadString(w[2..]);
                    break;
                case 6: // OpMemberName
                    memberNames[(w[1], w[2])] = ReadString(w[3..]);
                    break;
                case 71 when count >= 3: // OpDecorate
                    decorations[(w[1], w[2])] = (count > 3 ? w[3] : 0, i + 3);
                    if (w[2] == 6 && count > 3)
                    {
                        arrayStrides[w[1]] = (int)w[3]; // ArrayStride
                    }

                    break;
                case 72 when count >= 5: // OpMemberDecorate
                    if (w[3] == 35)
                    {
                        memberOffsets[(w[1], w[2])] = (int)w[4]; // Offset
                    }
                    else if (w[3] == 7)
                    {
                        matrixStrides[(w[1], w[2])] = (int)w[4]; // MatrixStride
                    }

                    break;
                case 21: // OpTypeInt
                    types[w[1]] = new SpirvType(SpirvTypeKind.Int, (int)w[2] / 8, 0, 1, []);
                    break;
                case 22: // OpTypeFloat
                    types[w[1]] = new SpirvType(SpirvTypeKind.Float, (int)w[2] / 8, 0, 1, []);
                    break;
                case 23: // OpTypeVector
                    types[w[1]] = new SpirvType(SpirvTypeKind.Vector, 0, w[2], (int)w[3], []);
                    break;
                case 24: // OpTypeMatrix
                    types[w[1]] = new SpirvType(SpirvTypeKind.Matrix, 0, w[2], (int)w[3], []);
                    break;
                case 25: // OpTypeImage
                    types[w[1]] = new SpirvType(SpirvTypeKind.Image, 0, 0, 0, []);
                    break;
                case 26: // OpTypeSampler
                    types[w[1]] = new SpirvType(SpirvTypeKind.Sampler, 0, 0, 0, []);
                    break;
                case 27: // OpTypeSampledImage
                    types[w[1]] = new SpirvType(SpirvTypeKind.SampledImage, 0, w[2], 0, []);
                    break;
                case 28: // OpTypeArray
                    types[w[1]] = new SpirvType(SpirvTypeKind.Array, 0, w[2], 0, [w[3]]);
                    break;
                case 30: // OpTypeStruct
                    types[w[1]] = new SpirvType(SpirvTypeKind.Struct, 0, 0, 0, w[2..].ToArray());
                    break;
                case 32: // OpTypePointer
                    types[w[1]] = new SpirvType(SpirvTypeKind.Pointer, (int)w[2], w[3], 0, []);
                    break;
                case 43 when count >= 4: // OpConstant (32-bit is all array lengths need)
                    constants[w[2]] = w[3];
                    break;
                case 59: // OpVariable
                    variables.Add((w[1], w[2], w[3]));
                    break;
            }

            i += count;
        }

        var blocks = new List<SpirvUniformBlock>();
        var samplers = new List<SpirvSampler>();
        var inputs = new List<SpirvInterfaceVariable>();
        var outputs = new List<SpirvInterfaceVariable>();
        var unsupported = new List<string>();

        foreach (var (pointerType, id, storage) in variables)
        {
            if (!types.TryGetValue(pointerType, out var pointer) || pointer.Kind != SpirvTypeKind.Pointer || !types.TryGetValue(pointer.Element, out var type))
            {
                continue;
            }

            var name = names.GetValueOrDefault(id, $"%{id}");
            var set = decorations.TryGetValue((id, 34), out var s) ? s.Value : 0;
            var binding = decorations.TryGetValue((id, 33), out var b) ? b.Value : 0;
            switch (storage)
            {
                case 0 when type.Kind == SpirvTypeKind.SampledImage: // UniformConstant
                    samplers.Add(new SpirvSampler(name, set, binding));
                    break;
                case 0:
                    unsupported.Add($"'{name}' ({type.Kind})");
                    break;
                case 2 when type.Kind == SpirvTypeKind.Struct && decorations.ContainsKey((pointer.Element, 2)): // Uniform + Block
                    var members = new List<SpirvUniformMember>();
                    for (var m = 0u; m < type.Members.Length; m++)
                    {
                        var offset = memberOffsets.GetValueOrDefault((pointer.Element, m));
                        var memberType = types[type.Members[m]];
                        var size = SizeOf(memberType, types, constants, arrayStrides, type.Members[m], matrixStrides.GetValueOrDefault((pointer.Element, m), 16));
                        var memberName = memberNames.GetValueOrDefault((pointer.Element, m), $"member{m}");
                        members.Add(memberType.Kind == SpirvTypeKind.Array
                            ? new SpirvUniformMember(memberName, offset, size, null, (int)constants.GetValueOrDefault(memberType.Members[0], 1u), arrayStrides.GetValueOrDefault(type.Members[m]), KindOf(types[memberType.Element], types))
                            : new SpirvUniformMember(memberName, offset, size, KindOf(memberType, types)));
                    }

                    var blockSize = members.Count == 0 ? 0 : members.Max(m => m.Offset + m.Size);
                    blocks.Add(new SpirvUniformBlock(set, binding, (blockSize + 15) & ~15, members));
                    break;
                case 1 or 3 when !decorations.ContainsKey((id, 11)) && decorations.TryGetValue((id, 30), out var location): // Input/Output, not BuiltIn
                    (storage == 1 ? inputs : outputs).Add(new SpirvInterfaceVariable(name, location.Value, location.Word));
                    break;
                case 2 or 9 or 12: // Uniform without Block (buffer block), PushConstant, StorageBuffer
                    unsupported.Add($"'{(names.TryGetValue(pointer.Element, out var typeName) ? typeName : name)}' ({(storage == 9 ? "push constant" : "storage buffer")})");
                    break;
            }
        }

        return new SpirvModule(words, blocks, samplers, inputs, outputs, unsupported);
    }

    /// <summary>
    /// Emulates GL's link-by-name: rewrites the Location of every fragment input to that of the vertex output with the
    /// same name (shaderc assigns locations per stage in declaration order). Returns the problems; empty when linked.
    /// </summary>
    public static IReadOnlyList<string> LinkByName(SpirvModule vertex, SpirvModule fragment)
    {
        ArgumentNullException.ThrowIfNull(vertex);
        ArgumentNullException.ThrowIfNull(fragment);
        var errors = new List<string>();
        var outputs = vertex.Outputs.ToDictionary(o => o.Name, StringComparer.Ordinal);
        foreach (var input in fragment.Inputs)
        {
            if (outputs.TryGetValue(input.Name, out var output))
            {
                fragment.Words[input.LocationWord] = output.Location;
            }
            else
            {
                errors.Add($"Fragment input '{input.Name}' is not written by the vertex stage.");
            }
        }

        return errors;
    }

    private static int SizeOf(SpirvType type, Dictionary<uint, SpirvType> types, Dictionary<uint, uint> constants, Dictionary<uint, int> arrayStrides, uint typeId, int matrixStride) =>
        type.Kind switch
        {
            SpirvTypeKind.Int or SpirvTypeKind.Float => type.ScalarBytes,
            SpirvTypeKind.Vector => types[type.Element].ScalarBytes * type.Count,
            SpirvTypeKind.Matrix => matrixStride * type.Count,
            SpirvTypeKind.Array => arrayStrides.GetValueOrDefault(typeId) * (int)constants.GetValueOrDefault(type.Members[0], 1u),
            SpirvTypeKind.Struct => 16, // nested structs are not settable; reserve their first slot so the block size stays positive
            _ => 0,
        };

    private static UniformKind? KindOf(SpirvType type, Dictionary<uint, SpirvType> types) => type.Kind switch
    {
        SpirvTypeKind.Float when type.ScalarBytes == 4 => UniformKind.Float,
        SpirvTypeKind.Int when type.ScalarBytes == 4 => UniformKind.Int,
        SpirvTypeKind.Vector when types[type.Element] is { Kind: SpirvTypeKind.Float, ScalarBytes: 4 } && type.Count == 2 => UniformKind.Vector2,
        SpirvTypeKind.Vector when types[type.Element] is { Kind: SpirvTypeKind.Float, ScalarBytes: 4 } && type.Count == 4 => UniformKind.Vector4,
        SpirvTypeKind.Matrix when type.Count == 4 && types[type.Element] is { Kind: SpirvTypeKind.Vector, Count: 4 } => UniformKind.Matrix4x4,
        _ => null,
    };

    private static string ReadString(ReadOnlySpan<uint> words)
    {
        var bytes = MemoryMarshal.AsBytes(words);
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? bytes[..end] : bytes);
    }

    private enum SpirvTypeKind
    {
        Int,
        Float,
        Vector,
        Matrix,
        Image,
        Sampler,
        SampledImage,
        Array,
        Struct,
        Pointer,
    }

    /// <param name="ScalarBytes">Scalar width; for pointers the storage class.</param>
    /// <param name="Element">Component, column, image, array element or pointee type.</param>
    /// <param name="Members">Struct member types; for arrays the length constant id.</param>
    private sealed record SpirvType(SpirvTypeKind Kind, int ScalarBytes, uint Element, int Count, uint[] Members);
}
