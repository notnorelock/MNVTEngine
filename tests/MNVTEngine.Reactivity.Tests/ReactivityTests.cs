// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

namespace MNVTEngine.Reactivity.Tests;

public sealed class ReactivityTests
{
    public ReactivityTests()
    {
        // The runtime is per thread and test threads are reused: start each test from the defaults.
        ReactiveRuntime.Current.ErrorHandler = null;
        ReactiveRuntime.Current.MaxFlushRounds = 100;
        Reactive.Diagnostics.Reset();
    }

    [Fact]
    public void SignalReadsAndWrites()
    {
        var s = Signal.Create(1);
        Assert.Equal(1, s.Value);
        Assert.True(s.Set(2));
        Assert.Equal(2, s.Value);
        Assert.Equal(2, s.Peek());
        s.Update(v => v * 10);
        Assert.Equal(20, s.Value);
    }

    [Fact]
    public void EqualWritesDoNothing()
    {
        var s = Signal.Create("a");
        int runs = 0;
        using (Effect.Create(() => { _ = s.Value; runs++; }))
        {
            Assert.False(s.Set("a"));
            Assert.Equal(1, runs);
            s.Value = "b";
            Assert.Equal(2, runs);
        }
    }

    [Fact]
    public void CustomComparerDecidesWhatChanged()
    {
        var s = Signal.Create("Engine", StringComparer.OrdinalIgnoreCase);
        int runs = 0;
        using (Effect.Create(() => { _ = s.Value; runs++; }))
        {
            s.Value = "ENGINE";
            Assert.Equal(1, runs); // case-insensitive comparer: no change
            s.Value = "Other";
            Assert.Equal(2, runs);
        }
    }

    [Fact]
    public void NotifyChangedReRunsForInPlaceMutation()
    {
        var list = new List<int> { 1 };
        var s = Signal.Create(list);
        int seen = 0;
        using (Effect.Create(() => seen = s.Value.Count))
        {
            list.Add(2);
            s.NotifyChanged();
            Assert.Equal(2, seen);
        }
    }

    [Fact]
    public void ComputedIsCachedAndLazy()
    {
        var a = Signal.Create(2);
        int evaluations = 0;
        var doubled = Computed.Create(() => { evaluations++; return a.Value * 2; });
        Assert.Equal(0, evaluations); // lazy: nothing computed before the first read
        Assert.Equal(4, doubled.Value);
        Assert.Equal(4, doubled.Value);
        Assert.Equal(1, evaluations);
        a.Value = 3;
        Assert.Equal(1, evaluations); // not recomputed until read again
        Assert.Equal(6, doubled.Value);
        Assert.Equal(2, evaluations);
    }

    [Fact]
    public void ComputedFollowsConditionalDependencies()
    {
        var useA = Signal.Create(true);
        var a = Signal.Create("a");
        var b = Signal.Create("b");
        int evaluations = 0;
        var pick = Computed.Create(() => { evaluations++; return useA.Value ? a.Value : b.Value; });
        Assert.Equal("a", pick.Value);
        Assert.Equal(0, b.ObserverCount); // b not read yet
        useA.Value = false;
        Assert.Equal("b", pick.Value);
        Assert.Equal(0, a.ObserverCount); // the branch not taken is no longer a dependency
        Assert.Equal(1, b.ObserverCount);
        int before = evaluations;
        a.Value = "changed";
        Assert.Equal("b", pick.Value);
        Assert.Equal(before, evaluations); // writing the dropped dependency recomputes nothing
    }

    [Fact]
    public void NestedComputedStopsAtEqualResults()
    {
        var n = Signal.Create(1);
        int parityRuns = 0, labelRuns = 0;
        var parity = Computed.Create(() => { parityRuns++; return n.Value % 2; });
        var label = Computed.Create(() => { labelRuns++; return parity.Value == 0 ? "even" : "odd"; });
        int effectRuns = 0;
        using (Effect.Create(() => { _ = label.Value; effectRuns++; }))
        {
            n.Value = 3; // parity stays 1
            Assert.Equal(2, parityRuns);
            Assert.Equal(1, labelRuns); // an equal intermediate result stops the update
            Assert.Equal(1, effectRuns);
            n.Value = 4;
            Assert.Equal(2, labelRuns);
            Assert.Equal(2, effectRuns);
            Assert.Equal("even", label.Value);
        }
    }

    [Fact]
    public void DiamondDependencyRunsOnce()
    {
        var s = Signal.Create(1);
        var left = Computed.Create(() => s.Value + 1);
        var right = Computed.Create(() => s.Value * 10);
        int runs = 0;
        int sum = 0;
        using (Effect.Create(() => { sum = left.Value + right.Value; runs++; }))
        {
            s.Value = 2;
            Assert.Equal(2, runs); // one rerun although two paths changed
            Assert.Equal(23, sum);
        }
    }

    [Fact]
    public void EffectWithSeveralDependencies()
    {
        var first = Signal.Create("Ada");
        var last = Signal.Create("Lovelace");
        string? full = null;
        using (var e = Effect.Create(() => full = first.Value + " " + last.Value))
        {
            Assert.Equal(2, e.SourceCount);
            first.Value = "Grace";
            Assert.Equal("Grace Lovelace", full);
            last.Value = "Hopper";
            Assert.Equal("Grace Hopper", full);
            Assert.Equal(3, e.RunCount);
        }
    }

    [Fact]
    public void DisposedEffectNeverRunsAndReleasesSources()
    {
        var s = Signal.Create(0);
        int runs = 0;
        var e = Effect.Create(() => { _ = s.Value; runs++; });
        Assert.Equal(1, s.ObserverCount);
        e.Dispose();
        Assert.Equal(0, s.ObserverCount); // disposed effects do not stay subscribed (no leak)
        s.Value = 1;
        Assert.Equal(1, runs);
    }

    [Fact]
    public void CleanupRunsBeforeRerunAndOnDispose()
    {
        var s = Signal.Create(0);
        var log = new List<string>();
        var e = Effect.Create(() =>
        {
            int v = s.Value;
            log.Add("run " + v);
            Reactive.OnCleanup(() => log.Add("cleanup " + v));
        });
        s.Value = 1;
        e.Dispose();
        Assert.Equal(["run 0", "cleanup 0", "run 1", "cleanup 1"], log);
    }

    [Fact]
    public void EffectsCreatedInsideAnEffectAreDisposedOnRerun()
    {
        var outer = Signal.Create(0);
        var inner = Signal.Create(0);
        int innerRuns = 0;
        using (Effect.Create(() =>
        {
            _ = outer.Value;
            Effect.Create(() => { _ = inner.Value; innerRuns++; });
        }))
        {
            outer.Value = 1; // the old inner effect is disposed, a new one created
            Assert.Equal(2, innerRuns);
            Assert.Equal(1, inner.ObserverCount); // only the newest inner effect is subscribed
            inner.Value = 1;
            Assert.Equal(3, innerRuns);
        }
        Assert.Equal(0, inner.ObserverCount);
    }

    [Fact]
    public void ScopeDisposesEverythingItOwns()
    {
        var s = Signal.Create(0);
        var scope = Reactive.CreateScope(detached: true);
        int runs = 0;
        bool cleaned = false;
        scope.Run(() =>
        {
            Effect.Create(() => { _ = s.Value; runs++; });
            Computed.Create(() => s.Value).Peek();
            Reactive.OnCleanup(() => cleaned = true);
        });
        Assert.Equal(2, s.ObserverCount);
        scope.Dispose();
        Assert.True(cleaned);
        Assert.Equal(0, s.ObserverCount);
        s.Value = 5;
        Assert.Equal(1, runs);
    }

    [Fact]
    public void BatchRunsDependentsOnceAfterTheLastWrite()
    {
        var a = Signal.Create(0);
        var b = Signal.Create(0);
        var seen = new List<int>();
        using (Effect.Create(() => seen.Add(a.Value + b.Value)))
        {
            Reactive.Batch(() =>
            {
                a.Value = 1;
                b.Value = 2;
                Assert.Single(seen); // nothing runs inside the batch
                Reactive.Batch(() => a.Value = 10); // nested batches flush with the outermost
                Assert.Single(seen);
            });
            Assert.Equal([0, 12], seen);
        }
    }

    [Fact]
    public void EffectsRunInTheOrderTheyWereInvalidated()
    {
        var s = Signal.Create(0);
        var order = new List<string>();
        using (Effect.Create(() => { _ = s.Value; order.Add("first"); }))
        using (Effect.Create(() => { _ = s.Value; order.Add("second"); }))
        {
            order.Clear();
            s.Value = 1;
            Assert.Equal(["first", "second"], order);
        }
    }

    [Fact]
    public void UntrackReadsWithoutDepending()
    {
        var tracked = Signal.Create(0);
        var untracked = Signal.Create(0);
        int runs = 0;
        using (Effect.Create(() => { _ = tracked.Value; _ = Reactive.Untrack(() => untracked.Value); runs++; }))
        {
            untracked.Value = 1;
            Assert.Equal(1, runs);
            tracked.Value = 1;
            Assert.Equal(2, runs);
        }
    }

    [Fact]
    public void ComputedExceptionsAreCachedAndRethrownUntilASourceChanges()
    {
        var divisor = Signal.Create(0);
        int evaluations = 0;
        var quotient = Computed.Create(() => { evaluations++; return 10 / divisor.Value; });
        Assert.Throws<DivideByZeroException>(() => _ = quotient.Value);
        Assert.Throws<DivideByZeroException>(() => _ = quotient.Value);
        Assert.Equal(1, evaluations); // the error is cached
        divisor.Value = 2;
        Assert.Equal(5, quotient.Value);
    }

    [Fact]
    public void FailingEffectsDoNotStopOthersAndAreReported()
    {
        var s = Signal.Create(0);
        int healthyRuns = 0;
        using (Effect.Create(() => { if (s.Value == 1) throw new InvalidOperationException("boom"); }))
        using (Effect.Create(() => { _ = s.Value; healthyRuns++; }))
        {
            var ex = Assert.Throws<ReactiveEffectException>(() => s.Value = 1);
            Assert.Equal("boom", ex.InnerException!.Message);
            Assert.Equal(2, healthyRuns); // the other effect still ran

            var errors = new List<Exception>();
            ReactiveRuntime.Current.ErrorHandler = errors.Add;
            s.Value = 0;
            s.Value = 1;
            Assert.Single(errors); // with a handler the error is reported, not thrown
        }
    }

    [Fact]
    public void ComputedThatReadsItselfIsACycle()
    {
        Computed<int>? self = null;
        self = Computed.Create(() => self!.Value + 1);
        Assert.Throws<ReactiveCycleException>(() => _ = self.Value);
    }

    [Fact]
    public void EffectsThatKeepTriggeringEachOtherAreStopped()
    {
        ReactiveRuntime.Current.MaxFlushRounds = 20;
        var ping = Signal.Create(0);
        var pong = Signal.Create(0);
        using (Effect.Create(() => pong.Value = ping.Value + 1))
        {
            // Creating the second effect starts the loop; it is stopped and reported.
            var ex = Assert.Throws<ReactiveEffectException>(() => Effect.Create(() => ping.Value = pong.Value + 1).Dispose());
            Assert.IsType<ReactiveLoopException>(ex.InnerException);
        }
        Assert.Equal(1, Reactive.Diagnostics.LoopsStopped);
    }

    [Fact]
    public void AnEffectThatSettlesIsNotALoop()
    {
        var raw = Signal.Create(150);
        var clamped = Signal.Create(0);
        using (Effect.Create(() => clamped.Value = Math.Min(raw.Value, 100)))
        {
            Assert.Equal(100, clamped.Value);
            raw.Value = 40;
            Assert.Equal(40, clamped.Value);
        }
        Assert.Equal(0, Reactive.Diagnostics.LoopsStopped);
    }

    [Fact]
    public void WritesFromAnotherThreadAreRefused()
    {
        var s = Signal.Create(0);
        Exception? thrown = null;
        var t = new Thread(() =>
        {
            try { s.Value = 1; }
            catch (InvalidOperationException e) { thrown = e; }
        });
        t.Start();
        t.Join();
        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal(0, s.Peek());
    }

    [Fact]
    public void DispatcherBringsWorkOntoTheUiThreadInOneBatch()
    {
        var dispatcher = new ReactiveDispatcher();
        var a = Signal.Create(0);
        var b = Signal.Create(0);
        int runs = 0;
        using (Effect.Create(() => { _ = a.Value + b.Value; runs++; }))
        {
            var t = new Thread(() =>
            {
                dispatcher.Post(() => a.Value = 1);
                dispatcher.Post(() => b.Value = 2);
            });
            t.Start();
            t.Join();
            Assert.Equal(1, runs); // nothing ran on the background thread
            Assert.Equal(2, dispatcher.Drain());
            Assert.Equal(2, runs); // both writes, one rerun
        }
    }

    [Fact]
    public void UnrelatedEffectsAreNotRerun()
    {
        var signals = new Signal<int>[50];
        var effects = new Effect[50];
        for (int i = 0; i < signals.Length; i++)
        {
            int index = i;
            signals[i] = Signal.Create(0);
            effects[i] = Effect.Create(() => _ = signals[index].Value);
        }
        Reactive.Diagnostics.Reset();
        signals[17].Value = 1;
        Assert.Equal(1, Reactive.Diagnostics.EffectRuns); // one write, one effect
        Assert.Equal(2, effects[17].RunCount);
        Assert.Equal(1, effects[16].RunCount);
        foreach (Effect e in effects) e.Dispose();
    }
}
