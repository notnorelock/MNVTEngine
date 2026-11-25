// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MNVTEngine.Obfuscation.Configuration;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace MNVTEngine.Obfuscation.Tests;

/// <summary>Renaming an interface method renames every reference to it, including the ones explicit implementations hold.</summary>
public sealed class ExplicitImplementationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-obf-explicit-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ExplicitImplementationOfAGenericInterface_StillLoadsAfterRenaming()
    {
        var input = Directory.CreateDirectory(Path.Combine(_root, "in")).FullName;
        var output = Path.Combine(_root, "out");
        WriteFixture(Path.Combine(input, "Explicit.dll"));

        var result = new ObfuscationEngine(new ObfuscatorConfig
        {
            InputDirectory = input,
            OutputDirectory = output,
            ReferenceDirectories = { Path.GetDirectoryName(typeof(object).Assembly.Location)! },
            Features = { ObfuscateStrings = false },
            Renaming = { Seed = "explicit" },
        }).Protect();
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Items));

        var context = new AssemblyLoadContext("explicit", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(File.ReadAllBytes(Path.Combine(output, "Explicit.dll"))));
            var types = assembly.GetTypes(); // threw MissingMethodException when the implementation named the old method
            Assert.DoesNotContain(types, t => t.Name is "IHost`1" or "Host"); // they were renamed
            var host = types.Single(t => t.IsClass && t.GetInterfaces().Length == 1);
            var implemented = host.GetInterfaceMap(host.GetInterfaces()[0]).InterfaceMethods.Single();
            Assert.NotEqual("Get", implemented.Name);
        }
        finally
        {
            context.Unload();
        }
    }

    // internal interface IHost<T> { int Get(T value); }
    // internal sealed class Host : IHost<string> { int IHost<string>.Get(string value) => 42; }
    private static void WriteFixture(string path)
    {
        using var module = ModuleDefinition.CreateModule("Explicit", new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = new DefaultAssemblyResolver() });
        var types = module.TypeSystem;

        var iface = new TypeDefinition("Fixture", "IHost`1", TypeAttributes.NotPublic | TypeAttributes.Interface | TypeAttributes.Abstract);
        var t = new GenericParameter("T", iface);
        iface.GenericParameters.Add(t);
        var get = new MethodDefinition("Get", MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot, types.Int32);
        get.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, t));
        iface.Methods.Add(get);
        module.Types.Add(iface);

        var closed = new GenericInstanceType(iface) { GenericArguments = { types.String } };
        var host = new TypeDefinition("Fixture", "Host", TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, types.Object);
        host.Interfaces.Add(new InterfaceImplementation(closed));

        var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, types.Void);
        var il = ctor.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, new MethodReference(".ctor", types.Void, types.Object) { HasThis = true });
        il.Emit(OpCodes.Ret);
        host.Methods.Add(ctor);

        // The implemented method, referenced through the closed interface: a MemberRef on a TypeSpec.
        var reference = new MethodReference("Get", types.Int32, closed) { HasThis = true };
        reference.Parameters.Add(new ParameterDefinition(iface.GenericParameters[0]));
        var impl = new MethodDefinition("Fixture.IHost<System.String>.Get", MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot, types.Int32);
        impl.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, types.String));
        impl.Overrides.Add(reference);
        il = impl.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4, 42);
        il.Emit(OpCodes.Ret);
        host.Methods.Add(impl);
        module.Types.Add(host);

        module.Write(path);
    }
}
