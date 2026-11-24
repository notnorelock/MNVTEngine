// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using Mono.Cecil;
using Mono.Cecil.Cil;
using MNVTEngine.Obfuscation.Reporting;

namespace MNVTEngine.Obfuscation.Validation;

/// <summary>
/// Re-reads a written assembly to catch a corrupt rewrite before it ships: the module must load, every type
/// and method body must decode, no two members of a type may share a name and signature, and every branch and
/// handler must point inside its method. This is a sanity check, not a full IL verifier.
/// </summary>
internal static class OutputValidator
{
    /// <summary>Whether a file looks like a managed assembly worth validating.</summary>
    public static bool Looks(string path)
    {
        try
        {
            using FileStream s = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(s);
            return pe.HasMetadata;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Validate(string path, IAssemblyResolver resolver, DiagnosticBag diag)
    {
        string name = Path.GetFileName(path);
        AssemblyDefinition asm;
        try
        {
            asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver, InMemory = true, ReadingMode = ReadingMode.Immediate });
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or InvalidOperationException)
        {
            diag.Error(DiagnosticCodes.OutputUnreadable, $"The written assembly does not load: {e.Message}", name);
            return;
        }
        using (asm)
        {
            foreach (TypeDefinition t in asm.MainModule.GetTypes())
            {
                CheckDuplicates(t, name, diag);
                foreach (MethodDefinition m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    try
                    {
                        CheckBody(m);
                    }
                    catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException)
                    {
                        diag.Error(DiagnosticCodes.OutputUnreadable, $"A method body does not decode after rewriting: {e.Message}", name, m.FullName);
                    }
                }
            }
        }
    }

    private static void CheckDuplicates(TypeDefinition t, string assembly, DiagnosticBag diag)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (MethodDefinition m in t.Methods)
            // Overloads can differ by return type alone (conversion operators), so the key includes it, as the
            // CLR's method signature does.
            if (!seen.Add("M:" + m.Name + "|" + m.ReturnType.FullName + "|" + string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) + "|" + m.GenericParameters.Count + "|" + (m.CallingConvention)))
                diag.Error(DiagnosticCodes.DuplicateMember, $"Two methods now share the name and signature '{m.Name}'.", assembly, t.FullName);
        seen.Clear();
        foreach (FieldDefinition f in t.Fields)
            if (!seen.Add("F:" + f.Name))
                diag.Error(DiagnosticCodes.DuplicateMember, $"Two fields now share the name '{f.Name}'.", assembly, t.FullName);
    }

    private static void CheckBody(MethodDefinition m)
    {
        Mono.Cecil.Cil.MethodBody body = m.Body;
        var instructions = new HashSet<Instruction>(body.Instructions);
        foreach (Instruction i in body.Instructions)
        {
            if (i.Operand is Instruction target && !instructions.Contains(target))
                throw new InvalidOperationException($"a branch at IL_{i.Offset:x4} targets an instruction outside the method");
            if (i.Operand is Instruction[] targets)
                foreach (Instruction tr in targets)
                    if (!instructions.Contains(tr)) throw new InvalidOperationException($"a switch at IL_{i.Offset:x4} targets an instruction outside the method");
        }
        foreach (ExceptionHandler h in body.ExceptionHandlers)
        {
            if (h.TryStart != null && !instructions.Contains(h.TryStart)) throw new InvalidOperationException("an exception handler's try start is missing");
            if (h.HandlerStart != null && !instructions.Contains(h.HandlerStart)) throw new InvalidOperationException("an exception handler's handler start is missing");
        }
    }
}
