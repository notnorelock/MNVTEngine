// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MNVTEngine.Obfuscation.Rules;

namespace MNVTEngine.Obfuscation.Transforms;

/// <summary>
/// Replaces <c>ldstr "text"</c> with <c>ldc.i4 id; call string Decoder::Get(int32)</c>. All literals of a
/// module live in one encrypted blob held by an injected internal class; each string is decoded once on first
/// use and cached (and interned, so reference equality behaves as before). The blob is a field with initial
/// data copied with RuntimeHelpers.InitializeArray, as C# array initializers are, which Mono, CoreCLR and
/// IL2CPP all support.
///
/// This hides literals from string dumps and static search. It is not strong encryption: the key and the
/// decoder are in the same module, and the decoded strings are in memory at run time.
/// </summary>
internal sealed class StringEncryption
{
    private readonly ModuleDefinition _module;
    private readonly bool _intern;
    private readonly int _minLength;
    private readonly IReadOnlyList<Glob> _keepPlain;
    private readonly Func<string, string> _name;
    private readonly uint _key;
    private readonly List<string> _strings = new();
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private MethodDefinition? _get;

    public TypeDefinition? DecoderType { get; private set; }
    public int Replaced { get; private set; }
    public int Distinct => _strings.Count;
    public int BlobBytes { get; private set; }

    /// <param name="name">Makes a unique name from a label (the decoder's type and members).</param>
    /// <param name="key">Keystream key; random per module unless the run is deterministic.</param>
    public StringEncryption(ModuleDefinition module, bool intern, int minLength, IReadOnlyList<Glob> keepPlain, Func<string, string> name, uint key)
    {
        _module = module;
        _intern = intern;
        _minLength = minLength;
        _keepPlain = keepPlain;
        _name = name;
        _key = key;
    }

    /// <summary>True when the literal should be encrypted.</summary>
    public bool Wants(string s) => s.Length >= _minLength && !_keepPlain.Any(g => g.IsMatch(s));

    /// <summary>Rewrites the literals of one method. Call <see cref="Finish"/> after all methods.</summary>
    public void Rewrite(MethodDefinition method)
    {
        if (!method.HasBody) return;
        Mono.Cecil.Cil.MethodBody body = method.Body;
        if (!body.Instructions.Any(i => i.OpCode.Code == Code.Ldstr && i.Operand is string s && Wants(s))) return;
        body.SimplifyMacros();
        ILProcessor il = body.GetILProcessor();
        foreach (Instruction ins in body.Instructions.ToList())
        {
            if (ins.OpCode.Code != Code.Ldstr || ins.Operand is not string s || !Wants(s)) continue;
            if (!_ids.TryGetValue(s, out int id))
            {
                id = _strings.Count;
                _strings.Add(s);
                _ids[s] = id;
            }
            // The ldstr instruction itself becomes the ldc.i4, so branches and handlers that target it still do.
            ins.OpCode = OpCodes.Ldc_I4;
            ins.Operand = id;
            il.InsertAfter(ins, il.Create(OpCodes.Call, GetMethod()));
            Replaced++;
        }
        body.OptimizeMacros();
    }

    private MethodReference GetMethod()
    {
        if (_get != null) return _get;
        // Created on first use; its body is written in Finish, once the strings are known.
        DecoderType = new TypeDefinition(string.Empty, _name("decoder"),
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.AutoLayout | TypeAttributes.AnsiClass,
            _module.TypeSystem.Object);
        _module.Types.Add(DecoderType);
        _get = new MethodDefinition(_name("get"), MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, _module.TypeSystem.String);
        _get.Parameters.Add(new ParameterDefinition("a", ParameterAttributes.None, _module.TypeSystem.Int32));
        DecoderType.Methods.Add(_get);
        return _get;
    }

    /// <summary>Encrypts the collected strings and writes the decoder.</summary>
    public void Finish()
    {
        if (_get == null || DecoderType == null) return;
        byte[] blob = BuildBlob();
        BlobBytes = blob.Length;
        TypeSystem ts = _module.TypeSystem;
        IMetadataScope corlib = ts.CoreLibrary;

        // The blob: a struct of the blob's size, an RVA field of it, and a byte[] filled from it once.
        var dataType = new TypeDefinition(string.Empty, _name("blob"),
            TypeAttributes.NestedPrivate | TypeAttributes.ExplicitLayout | TypeAttributes.Sealed | TypeAttributes.AnsiClass,
            new TypeReference("System", "ValueType", _module, corlib))
        {
            ClassSize = blob.Length,
            PackingSize = 1,
        };
        DecoderType.NestedTypes.Add(dataType);
        var rva = new FieldDefinition(_name("rva"), FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.InitOnly | FieldAttributes.HasFieldRVA, dataType) { InitialValue = blob };
        var data = new FieldDefinition(_name("data"), FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly, new ArrayType(ts.Byte));
        var cache = new FieldDefinition(_name("cache"), FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly, new ArrayType(ts.String));
        DecoderType.Fields.Add(rva);
        DecoderType.Fields.Add(data);
        DecoderType.Fields.Add(cache);

        var runtimeHelpers = new TypeReference("System.Runtime.CompilerServices", "RuntimeHelpers", _module, corlib);
        var initializeArray = new MethodReference("InitializeArray", ts.Void, runtimeHelpers) { HasThis = false };
        initializeArray.Parameters.Add(new ParameterDefinition(new TypeReference("System", "Array", _module, corlib)));
        initializeArray.Parameters.Add(new ParameterDefinition(new TypeReference("System", "RuntimeFieldHandle", _module, corlib, valueType: true)));
        var stringCtor = new MethodReference(".ctor", ts.Void, ts.String) { HasThis = true };
        stringCtor.Parameters.Add(new ParameterDefinition(new ArrayType(ts.Char)));
        var intern = new MethodReference("Intern", ts.String, ts.String) { HasThis = false };
        intern.Parameters.Add(new ParameterDefinition(ts.String));

        // static .cctor: data = new byte[N] { blob }; cache = new string[count];
        var cctor = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, ts.Void);
        DecoderType.Methods.Add(cctor);
        ILProcessor c = cctor.Body.GetILProcessor();
        c.Emit(OpCodes.Ldc_I4, blob.Length);
        c.Emit(OpCodes.Newarr, ts.Byte);
        c.Emit(OpCodes.Dup);
        c.Emit(OpCodes.Ldtoken, rva);
        c.Emit(OpCodes.Call, initializeArray);
        c.Emit(OpCodes.Stsfld, data);
        c.Emit(OpCodes.Ldc_I4, _strings.Count);
        c.Emit(OpCodes.Newarr, ts.String);
        c.Emit(OpCodes.Stsfld, cache);
        c.Emit(OpCodes.Ret);

        // static int B(int p): one decrypted byte.
        var b = new MethodDefinition(_name("byte"), MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Int32);
        b.Parameters.Add(new ParameterDefinition("a", ParameterAttributes.None, ts.Int32));
        b.Body.InitLocals = true;
        b.Body.Variables.Add(new VariableDefinition(ts.Int32));
        DecoderType.Methods.Add(b);
        ILProcessor bi = b.Body.GetILProcessor();
        bi.Emit(OpCodes.Ldarg_0);
        bi.Emit(OpCodes.Ldc_I4, unchecked((int)0x9E3779B1u));
        bi.Emit(OpCodes.Mul);
        bi.Emit(OpCodes.Ldc_I4, unchecked((int)_key));
        bi.Emit(OpCodes.Add);
        bi.Emit(OpCodes.Stloc_0);
        bi.Emit(OpCodes.Ldloc_0);
        bi.Emit(OpCodes.Ldloc_0);
        bi.Emit(OpCodes.Ldc_I4, 15);
        bi.Emit(OpCodes.Shr_Un);
        bi.Emit(OpCodes.Xor);
        bi.Emit(OpCodes.Ldc_I4, unchecked((int)0x85EBCA6Bu));
        bi.Emit(OpCodes.Mul);
        bi.Emit(OpCodes.Stloc_0);
        bi.Emit(OpCodes.Ldloc_0);
        bi.Emit(OpCodes.Ldloc_0);
        bi.Emit(OpCodes.Ldc_I4, 13);
        bi.Emit(OpCodes.Shr_Un);
        bi.Emit(OpCodes.Xor);
        bi.Emit(OpCodes.Stloc_0);
        bi.Emit(OpCodes.Ldsfld, data);
        bi.Emit(OpCodes.Ldarg_0);
        bi.Emit(OpCodes.Ldelem_U1);
        bi.Emit(OpCodes.Ldloc_0);
        bi.Emit(OpCodes.Xor);
        bi.Emit(OpCodes.Ldc_I4, 255);
        bi.Emit(OpCodes.And);
        bi.Emit(OpCodes.Ret);

        // static int W(int p): a little-endian int32 of decrypted bytes.
        var w = new MethodDefinition(_name("word"), MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Int32);
        w.Parameters.Add(new ParameterDefinition("a", ParameterAttributes.None, ts.Int32));
        DecoderType.Methods.Add(w);
        ILProcessor wi = w.Body.GetILProcessor();
        wi.Emit(OpCodes.Ldarg_0);
        wi.Emit(OpCodes.Call, b);
        for (int shift = 8; shift <= 24; shift += 8)
        {
            wi.Emit(OpCodes.Ldarg_0);
            wi.Emit(OpCodes.Ldc_I4, shift / 8);
            wi.Emit(OpCodes.Add);
            wi.Emit(OpCodes.Call, b);
            wi.Emit(OpCodes.Ldc_I4, shift);
            wi.Emit(OpCodes.Shl);
            wi.Emit(OpCodes.Or);
        }
        wi.Emit(OpCodes.Ret);

        // static string Get(int id)
        Mono.Cecil.Cil.MethodBody gb = _get.Body;
        gb.InitLocals = true;
        var s = new VariableDefinition(ts.String);
        var off = new VariableDefinition(ts.Int32);
        var len = new VariableDefinition(ts.Int32);
        var chars = new VariableDefinition(new ArrayType(ts.Char));
        var i = new VariableDefinition(ts.Int32);
        foreach (VariableDefinition v in new[] { s, off, len, chars, i }) gb.Variables.Add(v);
        ILProcessor g = gb.GetILProcessor();
        Instruction decode = g.Create(OpCodes.Ldarg_0);
        Instruction cond = g.Create(OpCodes.Ldloc, i);
        Instruction loop = g.Create(OpCodes.Ldloc, chars);
        g.Emit(OpCodes.Ldsfld, cache);
        g.Emit(OpCodes.Ldarg_0);
        g.Emit(OpCodes.Ldelem_Ref);
        g.Emit(OpCodes.Stloc, s);
        g.Emit(OpCodes.Ldloc, s);
        g.Emit(OpCodes.Brfalse, decode);
        g.Emit(OpCodes.Ldloc, s);
        g.Emit(OpCodes.Ret);
        g.Append(decode);                       // off = W(id * 8)
        g.Emit(OpCodes.Ldc_I4, 8);
        g.Emit(OpCodes.Mul);
        g.Emit(OpCodes.Call, w);
        g.Emit(OpCodes.Stloc, off);
        g.Emit(OpCodes.Ldarg_0);                // len = W(id * 8 + 4)
        g.Emit(OpCodes.Ldc_I4, 8);
        g.Emit(OpCodes.Mul);
        g.Emit(OpCodes.Ldc_I4, 4);
        g.Emit(OpCodes.Add);
        g.Emit(OpCodes.Call, w);
        g.Emit(OpCodes.Stloc, len);
        g.Emit(OpCodes.Ldloc, len);             // chars = new char[len]
        g.Emit(OpCodes.Newarr, ts.Char);
        g.Emit(OpCodes.Stloc, chars);
        g.Emit(OpCodes.Ldc_I4_0);
        g.Emit(OpCodes.Stloc, i);
        g.Emit(OpCodes.Br, cond);
        g.Append(loop);                         // chars[i] = (char)(B(off + 2i) | B(off + 2i + 1) << 8)
        g.Emit(OpCodes.Ldloc, i);
        g.Emit(OpCodes.Ldloc, off);
        g.Emit(OpCodes.Ldloc, i);
        g.Emit(OpCodes.Ldc_I4_2);
        g.Emit(OpCodes.Mul);
        g.Emit(OpCodes.Add);
        g.Emit(OpCodes.Call, b);
        g.Emit(OpCodes.Ldloc, off);
        g.Emit(OpCodes.Ldloc, i);
        g.Emit(OpCodes.Ldc_I4_2);
        g.Emit(OpCodes.Mul);
        g.Emit(OpCodes.Add);
        g.Emit(OpCodes.Ldc_I4_1);
        g.Emit(OpCodes.Add);
        g.Emit(OpCodes.Call, b);
        g.Emit(OpCodes.Ldc_I4, 8);
        g.Emit(OpCodes.Shl);
        g.Emit(OpCodes.Or);
        g.Emit(OpCodes.Conv_U2);
        g.Emit(OpCodes.Stelem_I2);
        g.Emit(OpCodes.Ldloc, i);               // i++
        g.Emit(OpCodes.Ldc_I4_1);
        g.Emit(OpCodes.Add);
        g.Emit(OpCodes.Stloc, i);
        g.Append(cond);                         // while (i < len)
        g.Emit(OpCodes.Ldloc, len);
        g.Emit(OpCodes.Blt, loop);
        g.Emit(OpCodes.Ldloc, chars);           // s = new string(chars), interned; cache[id] = s
        g.Emit(OpCodes.Newobj, stringCtor);
        if (_intern) g.Emit(OpCodes.Call, intern);
        g.Emit(OpCodes.Stloc, s);
        g.Emit(OpCodes.Ldsfld, cache);
        g.Emit(OpCodes.Ldarg_0);
        g.Emit(OpCodes.Ldloc, s);
        g.Emit(OpCodes.Stelem_Ref);
        g.Emit(OpCodes.Ldloc, s);
        g.Emit(OpCodes.Ret);

        foreach (MethodDefinition m in DecoderType.Methods) m.Body.OptimizeMacros();
    }

    /// <summary>Table of (offset, length) per id, then UTF-16 text; every byte xored with the keystream.</summary>
    private byte[] BuildBlob()
    {
        int tableBytes = _strings.Count * 8;
        int total = tableBytes + _strings.Sum(s => s.Length * 2);
        var blob = new byte[total];
        int offset = tableBytes;
        for (int id = 0; id < _strings.Count; id++)
        {
            string s = _strings[id];
            BitConverter.TryWriteBytes(blob.AsSpan(id * 8), offset);
            BitConverter.TryWriteBytes(blob.AsSpan(id * 8 + 4), s.Length);
            Encoding.Unicode.GetBytes(s, 0, s.Length, blob, offset);
            offset += s.Length * 2;
        }
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("The string blob is written little-endian.");
        for (int p = 0; p < blob.Length; p++) blob[p] ^= (byte)KeyByte(p, _key);
        return blob;
    }

    /// <summary>The keystream, exactly as the injected B(p) computes it.</summary>
    internal static int KeyByte(int p, uint key)
    {
        uint k = unchecked((uint)p * 0x9E3779B1u + key);
        k ^= k >> 15;
        k = unchecked(k * 0x85EBCA6Bu);
        k ^= k >> 13;
        return (int)(k & 0xFF);
    }

    public static uint RandomKey() => BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
}
