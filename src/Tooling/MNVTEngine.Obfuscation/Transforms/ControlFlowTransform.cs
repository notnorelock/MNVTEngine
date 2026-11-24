// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace MNVTEngine.Obfuscation.Transforms;

/// <summary>
/// Flattens a method's control flow: the basic blocks are emitted in a shuffled order with random state numbers, a
/// <c>state</c> local drives a <c>switch</c> dispatcher, and each block ends by setting <c>state</c> to its successor
/// and jumping back to the dispatcher (a conditional block sets one of two states). The IL no longer follows the
/// execution order, yet the same blocks run in the same order, so semantics are unchanged.
///
/// Safety: the method is skipped (not rewritten) unless every rule holds — no exception handlers (a protected
/// region's bounds must stay contiguous), the evaluation stack is empty at every block boundary (so a block can be
/// moved without carrying stack state), no fault/filter, no <c>jmp</c>/<c>localloc</c>/<c>switch</c>, and within the
/// configured size. A skipped method is reported, never silently mangled.
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

        if (!Flatten(body, blocks))
        {
            body.OptimizeMacros();
            Skipped++;
            SkippedMethods.Add((method, "a block ran off the end of the method"));
            return;
        }

        body.OptimizeMacros();
        Transformed++;
    }

    /// <summary>
    /// Rewrites the blocks into a flat state machine: a <c>state</c> local, a dispatcher that <c>switch</c>es on it to
    /// the real block, and each block ending by setting <c>state</c> to its successor and jumping back to the
    /// dispatcher. The blocks are emitted in a shuffled order with random state numbers, so the IL no longer follows
    /// execution. Returns false (leaving the body valid but unflattened) if a block has no successor and does not end.
    /// </summary>
    private bool Flatten(Mono.Cecil.Cil.MethodBody body, List<Block> blocks)
    {
        int n = blocks.Count;
        var index = new Dictionary<Instruction, int>();
        for (int i = 0; i < n; i++)
        {
            index[blocks[i].First] = i;
        }

        var state = new VariableDefinition(body.Method.Module.TypeSystem.Int32);
        body.Variables.Add(state);
        body.InitLocals = true;

        // A random, unique state number per block, and the inverse for the dispatcher.
        var stateOf = Enumerable.Range(0, n).ToList();
        Shuffle(stateOf);
        var dispatch = Instruction.Create(OpCodes.Ldloc, state);

        List<Instruction> SetState(int blockIndex) =>
            [Instruction.Create(OpCodes.Ldc_I4, stateOf[blockIndex]), Instruction.Create(OpCodes.Stloc, state), Instruction.Create(OpCodes.Br, dispatch)];

        var emitted = new List<Instruction>[n]; // the stream for each block
        var entry = new Instruction[n];         // where the dispatcher jumps for that block
        var order = Enumerable.Range(0, n).ToList();
        Shuffle(order);

        foreach (int bi in order)
        {
            Block b = blocks[bi];
            var insns = new List<Instruction>();
            for (Instruction cur = b.First; ; cur = cur.Next)
            {
                insns.Add(cur);
                if (cur == b.Last)
                {
                    break;
                }
            }

            Instruction last = b.Last;
            var stream = new List<Instruction>();
            if (last.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw)
            {
                stream.AddRange(insns); // ends the method; no successor
            }
            else if (last.OpCode.Code is Code.Br)
            {
                stream.AddRange(insns[..^1]); // drop the branch
                stream.AddRange(SetState(index[(Instruction)last.Operand]));
            }
            else if (last.OpCode.FlowControl is FlowControl.Cond_Branch)
            {
                if (last.Next is null)
                {
                    return false;
                }

                var trueStub = SetState(index[(Instruction)last.Operand]);
                stream.AddRange(insns);                     // keep the condition and its branch
                stream.AddRange(SetState(index[last.Next])); // false path: fall through to here
                stream.AddRange(trueStub);                  // true path:
                last.Operand = trueStub[0];                 // retarget the branch to the true stub
            }
            else
            {
                if (last.Next is null)
                {
                    return false; // a non-terminator block that falls off the end
                }

                stream.AddRange(insns);
                stream.AddRange(SetState(index[last.Next]));
            }

            emitted[bi] = stream;
            entry[bi] = stream[0];
        }

        var switchTargets = new Instruction[n];
        for (int i = 0; i < n; i++)
        {
            switchTargets[stateOf[i]] = entry[i];
        }

        var instructions = body.Instructions;
        instructions.Clear();
        foreach (Instruction i in SetState(0)) // enter at the first block via the dispatcher
        {
            instructions.Add(i);
        }

        foreach (int bi in order)
        {
            foreach (Instruction i in emitted[bi])
            {
                instructions.Add(i);
            }
        }

        instructions.Add(dispatch);
        instructions.Add(Instruction.Create(OpCodes.Switch, switchTargets));
        instructions.Add(Instruction.Create(OpCodes.Br, entry[0])); // unreachable: states cover every case
        return true;
    }

    private string? CanTransform(Mono.Cecil.Cil.MethodBody body)
    {
        if (body.HasExceptionHandlers) return "it has try/catch/finally regions";
        if (body.Instructions.Count > _maxInstructions) return $"it has more than {_maxInstructions} instructions";
        foreach (Instruction i in body.Instructions)
            if (i.OpCode.Code is Code.Jmp or Code.Localloc or Code.Switch) return "it uses jmp, localloc or a switch";
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
    private static bool StackBalanced(List<Block> blocks) => blocks.All(BlockBalanced);

    private static bool BlockBalanced(Block b)
    {
        int depth = 0;
        for (Instruction cur = b.First; ; cur = cur.Next)
        {
            bool last = cur == b.Last;
            // A block ending in ret or throw is self-contained: ret leaves the return value (void: 0, value: 1) and
            // throw the exception; both are consumed by leaving the method, so the block still routes cleanly.
            if (last && cur.OpCode.Code == Code.Ret)
            {
                return depth is 0 or 1;
            }

            if (last && cur.OpCode.FlowControl == FlowControl.Throw)
            {
                return depth >= 1;
            }

            depth += StackDelta(cur);
            if (depth < 0)
            {
                return false;
            }

            if (last)
            {
                return depth == 0; // a block that hands off to another must leave an empty stack
            }
        }
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
