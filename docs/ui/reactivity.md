# Reactivity

`MNVTEngine.Reactivity` is fine-grained reactive state: signals, cached computed values and effects, in the style of
SolidJS (owner trees) with the push-pull "Reactively" algorithm. It is plain C#, with no reflection, no expression
trees and no engine dependencies, so UI, gameplay code, tools and servers can share it. Nothing polls per frame.

```csharp
var username = Signal.Create("");
var busy = Signal.Create(false);
var canSubmit = Computed.Create(() => username.Value.Trim().Length >= 3 && !busy.Value);
using var log = Effect.Create(() => GameLog.Info($"can submit: {canSubmit.Value}"));
username.Value = "slime"; // the effect reruns before the setter returns
```

## Primitives

- **`Signal<T>`** holds a value.
  - Reading `.Value` inside a computed value or an effect records a dependency.
  - `.Set(v)` (or the setter) notifies dependents only when the comparer says the value changed. The default comparer
    is `EqualityComparer<T>.Default`; pass another to the constructor (case-insensitive strings, reference equality for
    snapshots).
  - `Peek()` reads without depending, `Update(f)` writes a function of the value, and `NotifyChanged()` notifies after
    a mutable value changed in place.
- **`Computed<T>`** is cached and lazy.
  - It recomputes only when read after a source really changed.
  - Its dependencies are exactly what its last evaluation read, so branches no longer taken stop being dependencies.
  - Recomputing to an equal value does not rerun its dependents (equality cut-off).
  - An exception thrown inside is cached and rethrown to every reader until a source changes.
  - A computed value that reads itself throws `ReactiveCycleException`.
- **`Effect`** runs once on creation and again when a dependency really changes. Before a rerun and on disposal, it
  disposes what its last run created and runs its `Reactive.OnCleanup` callbacks, newest first. A disposed effect never
  runs again and unsubscribes from everything.
- **`ReactiveScope`** owns what is created inside `Run(...)`; disposing it disposes everything it owns. Create one with
  `Reactive.CreateScope()`.
- **`Reactive.Batch(action)`** defers effects to the end of the outermost batch, so dependents of several writes run
  once. `Reactive.Untrack(...)` reads without depending.

## Propagation

A write marks direct observers **Dirty** and everything downstream **Check**, and queues the effects it reaches. Before
an effect runs, or a computed value is read, its Check sources are brought up to date; it recomputes only if one of them
actually changed. So:

- a diamond (`a → b, c → effect`) runs the effect once;
- a computed value that recomputes to the same answer stops the update;
- a write that nothing depends on costs almost nothing.

## Order and loops

- Outside a batch, effects run before the setter returns.
- Queued effects run in the order they were invalidated; effects queued during a round run in the next round.
- An effect's first run is itself a batch, so what it writes runs after it has subscribed to its own sources.
- Effects that keep re-triggering each other are stopped after `ReactiveRuntime.MaxFlushRounds` (100) rounds with a
  `ReactiveLoopException` naming them.

## Errors

An effect that throws does not stop the others in the same flush. Without a `ReactiveRuntime.ErrorHandler`, the errors
are rethrown after the flush as a `ReactiveEffectException`. Hosts that must not break a frame (the UI) install a
handler that logs.

## Threads

There is one runtime per thread (`ReactiveRuntime.Current`); the UI uses the game thread's. Signals and nodes remember
the thread they were created on and throw `InvalidOperationException` elsewhere. Other threads (network callbacks,
tasks) post with a `ReactiveDispatcher`; its owner drains it once per frame on the reactive thread, and the posted
actions run in one batch.

## Diagnostics

`Reactive.Diagnostics` counts signal writes, computed evaluations, effect runs, errors and stopped loops. Each counter
costs one increment, so they are always on. `Signal.ObserverCount`, `Computed.SourceCount` and `Effect.RunCount` help
leak tests.

## Tests

`tests/MNVTEngine.Reactivity.Tests` covers reads and writes, equal writes and custom comparers, in-place notification,
laziness and caching, conditional dependencies, nested cut-off, the diamond, effects with several dependencies, disposal
without leaks, cleanup order, nested effects, scopes, batches, effect order, untracked reads, cached errors, reported
errors, cycles, loop detection, settling effects, thread refusal, the dispatcher and unrelated effects not rerunning.
