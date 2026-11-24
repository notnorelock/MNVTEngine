// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MNVTEngine.Obfuscation.Transforms;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace MNVTEngine.Obfuscation.Tests;

/// <summary>Flattening rewrites a method's control flow into a state machine; the method must still compute the same result.</summary>
public sealed class ControlFlowTransformTests
{
    [Fact]
    public void FlattensALoop_AndItStillComputesTheSameResult()
    {
        var (invoke, transform) = BuildAndFlatten(BuildSum, "Sum");
        Assert.Equal(1, transform.Transformed); // the method was flattened, not skipped
        Assert.Equal(0, transform.Skipped);
        Assert.Equal(45, invoke([10])); // 0+1+...+9
        Assert.Equal(0, invoke([0]));
        Assert.Equal(4950, invoke([100]));
    }

    [Fact]
    public void FlattensABranch_AndItStillComputesTheSameResult()
    {
        var (invoke, transform) = BuildAndFlatten(BuildMax, "Max2");
        Assert.Equal(1, transform.Transformed);
        Assert.Equal(7, invoke([3, 7]));
        Assert.Equal(9, invoke([9, 2]));
        Assert.Equal(5, invoke([5, 5]));
    }

    private static (Func<int[], int> Invoke, ControlFlowTransform Transform) BuildAndFlatten(Action<ModuleDefinition, TypeDefinition> build, string method)
    {
        var module = ModuleDefinition.CreateModule("Flat", ModuleKind.Dll);
        var type = new TypeDefinition("Flat", "Ops", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        module.Types.Add(type);
        build(module, type);

        var transform = new ControlFlowTransform(5000, seed: 1234);
        transform.Rewrite(type.Methods.Single(m => m.Name == method));

        using var stream = new MemoryStream();
        module.Write(stream);
        var run = Assembly.Load(stream.ToArray()).GetType("Flat.Ops")!.GetMethod(method)!;
        return (args => (int)run.Invoke(null, args.Cast<object>().ToArray())!, transform);
    }

    // static int Sum(int n) { int s = 0; for (int i = 0; i < n; i++) s += i; return s; }
    private static void BuildSum(ModuleDefinition module, TypeDefinition type)
    {
        var sum = new MethodDefinition("Sum", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        sum.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        type.Methods.Add(sum);
        var s = new VariableDefinition(module.TypeSystem.Int32);
        var i = new VariableDefinition(module.TypeSystem.Int32);
        sum.Body.Variables.Add(s);
        sum.Body.Variables.Add(i);

        var bodyStart = Instruction.Create(OpCodes.Ldloc, s);
        var check = Instruction.Create(OpCodes.Ldloc, i);
        Instruction[] stream =
        [
            Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stloc, s),
            Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stloc, i),
            Instruction.Create(OpCodes.Br, check),
            bodyStart, Instruction.Create(OpCodes.Ldloc, i), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Stloc, s),
            Instruction.Create(OpCodes.Ldloc, i), Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Stloc, i),
            check, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Blt, bodyStart),
            Instruction.Create(OpCodes.Ldloc, s), Instruction.Create(OpCodes.Ret),
        ];
        var il = sum.Body.GetILProcessor();
        foreach (var instruction in stream)
        {
            il.Append(instruction);
        }
    }

    // static int Max2(int a, int b) { if (a > b) return a; return b; }
    private static void BuildMax(ModuleDefinition module, TypeDefinition type)
    {
        var max = new MethodDefinition("Max2", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        max.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        max.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        type.Methods.Add(max);

        var returnA = Instruction.Create(OpCodes.Ldarg_0);
        var pushB = Instruction.Create(OpCodes.Ldarg_1);
        Instruction[] stream =
        [
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Bgt, returnA),
            pushB, Instruction.Create(OpCodes.Ret),
            returnA, Instruction.Create(OpCodes.Ret),
        ];
        var il = max.Body.GetILProcessor();
        foreach (var instruction in stream)
        {
            il.Append(instruction);
        }
    }
}
