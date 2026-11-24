// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace MNVTEngine.Obfuscation.Transforms;

/// <summary>
/// Hides integer literals from a plain disassembly by replacing <c>ldc.i4 N</c> (and <c>ldc.i8</c>) with an
/// arithmetic identity: <c>(N ^ k) ^ k</c> with a per-site key. The two keys are separate instructions, so
/// the original value is not a literal in the stream. This only raises the reading cost a little; a
/// constant-folding decompiler undoes it. Loads used where the verifier needs a constant (array sizes in a
/// localloc, switch cases, no — those are not ldc operands) are left alone; ldc feeding a known
/// constant-only context is rare and the transform is value-preserving regardless.
/// </summary>
internal sealed class ConstantTransform
{
    private readonly Random _random;
    public int Replaced { get; private set; }

    public ConstantTransform(int seed) => _random = new Random(seed);

    public void Rewrite(MethodDefinition method)
    {
        if (!method.HasBody) return;
        Mono.Cecil.Cil.MethodBody body = method.Body;
        if (!body.Instructions.Any(IsTarget)) return;
        body.SimplifyMacros();
        ILProcessor il = body.GetILProcessor();
        foreach (Instruction ins in body.Instructions.ToList())
        {
            if (ins.OpCode.Code == Code.Ldc_I4 && ins.Operand is int v)
            {
                int key = _random.Next(int.MinValue, int.MaxValue);
                ins.OpCode = OpCodes.Ldc_I4;
                ins.Operand = v ^ key;
                Instruction k1 = il.Create(OpCodes.Ldc_I4, key);
                il.InsertAfter(ins, k1);
                il.InsertAfter(k1, il.Create(OpCodes.Xor));
                Replaced++;
            }
            else if (ins.OpCode.Code == Code.Ldc_I8 && ins.Operand is long lv)
            {
                long key = unchecked(((long)_random.Next() << 32) ^ _random.Next());
                ins.OpCode = OpCodes.Ldc_I8;
                ins.Operand = lv ^ key;
                Instruction k1 = il.Create(OpCodes.Ldc_I8, key);
                il.InsertAfter(ins, k1);
                il.InsertAfter(k1, il.Create(OpCodes.Xor));
                Replaced++;
            }
        }
        body.OptimizeMacros();
    }

    // ldc.i4 feeding newarr/switch is still valid as a runtime value, so no special case is needed.
    private static bool IsTarget(Instruction i) =>
        (i.OpCode.Code == Code.Ldc_I4 && i.Operand is int) || (i.OpCode.Code == Code.Ldc_I8 && i.Operand is long);
}
