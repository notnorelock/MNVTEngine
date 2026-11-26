// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using MNVTEngine.Core.Timing;

namespace MNVTEngine.Scenes.Tests;

public sealed class SceneSynchronizationTests
{
    private static readonly FrameTime Frame = new(0.1f, 0.1f, 0.1, 0.1, 1);

    /// <summary>Awaits work finished on another thread, then records where and in which frame it resumed.</summary>
    private sealed class Awaiter : Component
    {
        public TaskCompletionSource<int> Work { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int? Result { get; private set; }

        public int ResumedThread { get; private set; }

        public long ResumedFrame { get; private set; } = -1;

        public override async void Start()
        {
            Result = await Work.Task;
            (ResumedThread, ResumedFrame) = (Environment.CurrentManagedThreadId, Time.FrameCount);
        }
    }

    private sealed class Thrower : Component
    {
        public TaskCompletionSource Work { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async void Start()
        {
            await Work.Task;
            throw new InvalidOperationException("after await");
        }
    }

    [Fact]
    public async Task Await_InGameCode_ResumesOnTheGameThread_AtTheNextUpdate()
    {
        var scene = new Scene("Await") { RethrowScriptExceptions = true };
        var awaiter = scene.CreateGameObject("A").AddComponent<Awaiter>();
        scene.Start();
        scene.Update(Frame);
        var gameThread = Environment.CurrentManagedThreadId;

        await Task.Run(() => awaiter.Work.SetResult(42), TestContext.Current.CancellationToken);
        Assert.Null(awaiter.Result); // queued, not run on the worker

        scene.Update(Frame with { FrameCount = 2 });
        Assert.Equal(42, awaiter.Result);
        Assert.Equal(gameThread, awaiter.ResumedThread);
        Assert.Equal(2, awaiter.ResumedFrame); // Time works: it ran inside the scene
    }

    [Fact]
    public async Task Post_FromAnotherThread_RunsInTheNextUpdate()
    {
        var scene = new Scene("Post");
        scene.Start();
        var ran = 0;
        await Task.Run(() => scene.Post(() => ran = Environment.CurrentManagedThreadId), TestContext.Current.CancellationToken);

        Assert.Equal(0, ran);
        scene.Update(Frame);
        Assert.Equal(Environment.CurrentManagedThreadId, ran);
    }

    [Fact]
    public void WorkPostedWhileDraining_WaitsForTheNextUpdate()
    {
        var scene = new Scene("Chain");
        scene.Start();
        var log = new List<int>();
        scene.Post(() =>
        {
            log.Add(1);
            scene.Post(() => log.Add(2));
        });

        scene.Update(Frame);
        Assert.Equal([1], log);
        scene.Update(Frame);
        Assert.Equal([1, 2], log);
    }

    [Fact]
    public async Task ExceptionAfterAwait_IsLoggedLikeAComponentCallback()
    {
        var scene = new Scene("Throw");
        var thrower = scene.CreateGameObject("T").AddComponent<Thrower>();
        scene.Start();
        scene.Update(Frame);

        await Task.Run(thrower.Work.SetResult, TestContext.Current.CancellationToken);
        scene.Update(Frame);

        Assert.Equal(1, scene.ScriptExceptionCount);
        Assert.IsType<InvalidOperationException>(scene.RecentScriptErrors.Single().Exception);
    }

    [Fact]
    public void Context_IsCurrentOnlyInsideTheScene()
    {
        var outer = SynchronizationContext.Current;
        var scene = new Scene("Context");
        SynchronizationContext? inside = null;
        scene.CreateGameObject("C").AddComponent<Probe>().OnStart = () => inside = SynchronizationContext.Current;
        scene.Start();
        scene.Update(Frame);

        Assert.NotNull(inside);
        Assert.NotSame(outer, inside);
        Assert.Same(outer, SynchronizationContext.Current);

        var ran = false;
        inside.Post(_ => ran = true, null); // the scene's queue
        scene.Update(Frame);
        Assert.True(ran);
    }

    [Fact]
    public void PendingWork_MovesWithPersistentObjects_AndUnloadDropsTheRest()
    {
        var first = new Scene("First");
        first.Start();
        var ran = new List<string>();
        first.Post(() => ran.Add("carried"));
        var second = new Scene("Second");
        first.MovePersistentObjectsTo(second);
        first.Unload();
        second.Start();
        second.Update(Frame);
        Assert.Equal(["carried"], ran);

        second.Post(() => ran.Add("dropped"));
        second.Unload();
        second.Start();
        second.Update(Frame);
        Assert.Equal(["carried"], ran);
    }

    [Fact]
    public void PostsToAHandedOverContext_ReachTheSceneThatTookOver()
    {
        // A persistent object captured the first scene's context (a socket raising its events through it): after the
        // scene changes, what it posts must still run on the game thread, in whichever scene is running now.
        var first = new Scene("Menu");
        first.Start();
        SynchronizationContext? captured = null;
        first.CreateGameObject("Session").AddComponent<Probe>().OnStart = () => captured = SynchronizationContext.Current;
        first.Update(Frame);
        var second = new Scene("Club");
        first.MovePersistentObjectsTo(second);
        first.Unload();
        second.Start();
        var third = new Scene("Menu again");
        second.MovePersistentObjectsTo(third);
        second.Unload();
        third.Start();

        var ran = 0;
        captured!.Post(_ => ran++, null);
        third.Update(Frame);
        Assert.Equal(1, ran);

        first.Start(); // a scene running again takes its own posts back
        captured.Post(_ => ran++, null);
        third.Update(Frame);
        Assert.Equal(1, ran);
        first.Update(Frame);
        Assert.Equal(2, ran);
    }

    private sealed class Probe : Component
    {
        public Action? OnStart { get; set; }

        public override void Start() => OnStart?.Invoke();
    }
}
