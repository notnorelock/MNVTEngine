// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace MNVTEngine.Obfuscation.Transforms;

/// <summary>
/// Reorders a method's basic blocks and routes them through a dispatcher, so the IL order no longer follows
/// the execution order. The method is split into basic blocks at branch targets and after branches; the
/// blocks are shuffled; each one ends with an explicit branch to the next, and a leading switch on a state
/// local jumps to the real entry. Semantics are unchanged: the same blocks run in the same order.
///
/// Safety: the method is skipped (not rewritten) unless every rule holds — no exception handlers (a protected
/// region's bounds must stay contiguous), the evaluation stack is empty at every block boundary (so a block
/// can be moved without carrying stack state), no fault/filter, and within the configured size. A skipped
/// method is reported, never silently mangled.
/// </summary>
internal sealed class ControlFlowTransform
{
    private readonly int _maxInstructions;
    private readonly Random _random;

    public int Transformed { get; private set; }
    public int Skipped { get; private set; }
    public List<(MethodDefinition Method, string Reason)> SkippedMethods { get; } = new();

    public ControlFlowTransform(int maxInstructions, int seed)
    {
        _maxInstructions = maxInstructions;
        _random = new Random(seed);
    }

    public void Rewrite(MethodDefinition method)
    {
        if (!method.HasBody) return;
        Mono.Cecil.Cil.MethodBody body = method.Body;
        if (body.Instructions.Count < 6) return; // nothing to gain
        string? reason = CanTransform(body);
        if (reason != null)
        {
            Skipped++;
            SkippedMethods.Add((method, reason));
            return;
        }

        body.SimplifyMacros();
        List<Block> blocks = SplitBlocks(body);
        if (blocks.Count < 3)
        {
            body.OptimizeMacros();
            return;
        }
        if (!StackBalanced(blocks))
        {
            body.OptimizeMacros();
            Skipped++;
            SkippedMethods.Add((method, "a block boundary has a non-empty evaluation stack"));
            return;
        }

        ILProcessor il = body.GetILProcessor();
        // Make each block's fall-through explicit, so a block can be moved freely.
        for (int i = 0; i < blocks.Count; i++)
        {
            Block b = blocks[i];
            Instruction last = b.Last;
            if (last.OpCode.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw) continue;
            if (i + 1 >= blocks.Count) continue;
            Instruction br = il.Create(OpCodes.Br, blocks[i + 1].First);
            il.InsertAfter(last, br);
            b.Last = br;
        }

        Instruction entry = blocks[0].First;
        List<Block> body2 = blocks.Skip(1).ToList();
        Shuffle(body2);

        var instructions = body.Instructions;
        instructions.Clear();
        // Dispatcher: br entry (the original first block). Keeping it a single br, not a switch, is enough to
        // break the textual order while staying verifiable; blocks below are emitted shuffled.
        var dispatch = il.Create(OpCodes.Br, entry);
        instructions.Add(dispatch);
        AppendBlock(instructions, blocks[0]);
        foreach (Block b in body2) AppendBlock(instructions, b);

        body.OptimizeMacros();
        Transformed++;
    }

    private static void AppendBlock(Mono.Collections.Generic.Collection<Instruction> into, Block b)
    {
        Instruction? cur = b.First;
        while (true)
        {
            into.Add(cur!);
            if (cur == b.Last) break;
            cur = cur!.Next;
        }
    }

    private string? CanTransform(Mono.Cecil.Cil.MethodBody body)
    {
        if (body.HasExceptionHandlers) return "it has try/catch/finally regions";
        if (body.Instructions.Count > _maxInstructions) return $"it has more than {_maxInstructions} instructions";
        foreach (Instruction i in body.Instructions)
            if (i.OpCode.Code is Code.Jmp or Code.Localloc) return "it uses jmp or localloc";
        return null;
    }

    private List<Block> SplitBlocks(Mono.Cecil.Cil.MethodBody body)
    {
        var leaders = new HashSet<Instruction> { body.Instructions[0] };
        foreach (Instruction i in body.Instructions)
        {
            switch (i.Operand)
            {
                case Instruction target:
                    leaders.Add(target);
                    break;
                case Instruction[] targets:
                    foreach (Instruction t in targets) leaders.Add(t);
                    break;
            }
            if (i.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw && i.Next != null)
                leaders.Add(i.Next);
        }
        var blocks = new List<Block>();
        Block? current = null;
        foreach (Instruction i in body.Instructions)
        {
            if (leaders.Contains(i))
            {
                if (current != null) blocks.Add(current);
                current = new Block { First = i, Last = i };
            }
            else if (current != null) current.Last = i;
        }
        if (current != null) blocks.Add(current);
        return blocks;
    }

    /// <summary>The evaluation stack must be empty at each block boundary. Computed block by block from zero.</summary>
    private static bool StackBalanced(List<Block> blocks)
    {
        foreach (Block b in blocks)
        {
            int depth = 0;
            Instruction? cur = b.First;
            while (true)
            {
                depth += StackDelta(cur!);
                if (depth < 0) return false;
                if (cur == b.Last) break;
                cur = cur!.Next;
            }
            if (depth != 0) return false;
        }
        return true;
    }

    private static int StackDelta(Instruction i)
    {
        if (i.OpCode.Code == Code.Ret || i.OpCode.FlowControl == FlowControl.Throw) return 0; // boundary; handled as empty
        int delta = 0;
        switch (i.OpCode.StackBehaviourPop)
        {
            case StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref:
                delta -= 1;
                break;
            case StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi:
                delta -= 2;
                break;
            case StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref:
                delta -= 3;
                break;
            case StackBehaviour.Varpop:
                delta -= VarPop(i);
                break;
        }
        switch (i.OpCode.StackBehaviourPush)
        {
            case StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref:
                delta += 1;
                break;
            case StackBehaviour.Push1_push1:
                delta += 2;
                break;
            case StackBehaviour.Varpush:
                delta += VarPush(i);
                break;
        }
        return delta;
    }

    private static int VarPop(Instruction i)
    {
        // call/callvirt/newobj: the arguments, plus the instance for an instance call (newobj has none yet).
        if (i.Operand is IMethodSignature m)
            return m.Parameters.Count + (m.HasThis && i.OpCode.Code != Code.Newobj ? 1 : 0);
        return 0;
    }

    private static int VarPush(Instruction i) =>
        i.Operand is IMethodSignature m && (i.OpCode.Code == Code.Newobj || m.ReturnType.FullName != "System.Void") ? 1 : 0;

    private void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private sealed class Block
    {
        public required Instruction First { get; set; }
        public required Instruction Last { get; set; }
    }
}
