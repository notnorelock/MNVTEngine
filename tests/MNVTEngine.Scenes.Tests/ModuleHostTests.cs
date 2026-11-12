using MNVTEngine.Scenes;

namespace MNVTEngine.Scenes.Tests;

public sealed class ModuleHostTests
{
    private sealed class Recorder(string id, List<string> log, string? failOn = null) : IEngineModule
    {
        public string Id => id;

        public void Register(ComponentRegistry components) => Step("register");

        public void Initialize() => Step("initialize");

        public void Shutdown() => Step("shutdown");

        private void Step(string phase)
        {
            log.Add($"{phase} {id}");
            if (phase == failOn)
            {
                throw new InvalidOperationException($"{id} broke in {phase}");
            }
        }
    }

    [Fact]
    public void Modules_RegisterThenInitializeInOrder_AndShutDownInReverse()
    {
        var log = new List<string>();
        var host = new ModuleHost([new Recorder("core", log), new Recorder("world.2d", log), new Recorder("physics.2d", log)]);
        host.Start(new ComponentRegistry());
        Assert.Equal(ModuleHostState.Running, host.State);
        Assert.Equal(["core", "world.2d", "physics.2d"], host.Initialized);

        host.Shutdown();
        Assert.Equal(
            ["register core", "register world.2d", "register physics.2d", "initialize core", "initialize world.2d", "initialize physics.2d",
             "shutdown physics.2d", "shutdown world.2d", "shutdown core"],
            log);
        Assert.Equal(ModuleHostState.Stopped, host.State);
        Assert.Empty(host.Initialized);
    }

    [Fact]
    public void AFailedInitialization_StopsTheModulesStartedBeforeIt_AndNamesTheModule()
    {
        var log = new List<string>();
        var host = new ModuleHost([new Recorder("a", log), new Recorder("b", log), new Recorder("c", log, failOn: "initialize"), new Recorder("d", log)]);
        var ex = Assert.Throws<ModuleStartupException>(() => host.Start(new ComponentRegistry()));
        Assert.Equal(("c", "initialize"), (ex.Module, ex.Phase));
        Assert.Contains("Module 'c' failed to initialize", ex.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal(["initialize a", "initialize b", "initialize c", "shutdown b", "shutdown a"], log.Where(l => !l.StartsWith("register", StringComparison.Ordinal)));
        Assert.Equal(ModuleHostState.Failed, host.State);
        Assert.Empty(host.Initialized);

        host.Shutdown(); // nothing left to stop
        Assert.DoesNotContain(log, l => l is "shutdown c" or "shutdown d");
    }

    [Fact]
    public void AFailedRegistration_InitializesNothing()
    {
        var log = new List<string>();
        var host = new ModuleHost([new Recorder("a", log), new Recorder("b", log, failOn: "register")]);
        var ex = Assert.Throws<ModuleStartupException>(() => host.Start(new ComponentRegistry()));
        Assert.Equal(("b", "register"), (ex.Module, ex.Phase));
        Assert.Equal(["register a", "register b"], log);
    }

    [Fact]
    public void CleanupFailures_AreReported_AndEveryModuleIsStillStopped()
    {
        var log = new List<string>();
        var host = new ModuleHost([new Recorder("a", log), new Recorder("b", log, failOn: "shutdown"), new Recorder("c", log, failOn: "initialize")]);
        var ex = Assert.Throws<ModuleStartupException>(() => host.Start(new ComponentRegistry()));
        Assert.Equal("b", Assert.IsType<ModuleFailureException>(Assert.Single(ex.CleanupFailures)).Module);
        Assert.Contains("shutdown a", log);
    }

    [Fact]
    public void ShutdownFailures_DoNotStopTheOtherModules()
    {
        var log = new List<string>();
        var host = new ModuleHost([new Recorder("a", log), new Recorder("b", log, failOn: "shutdown"), new Recorder("c", log)]);
        host.Start(new ComponentRegistry());
        var ex = Assert.Throws<ModuleShutdownException>(host.Shutdown);
        Assert.Equal("b", Assert.IsType<ModuleFailureException>(Assert.Single(ex.InnerExceptions)).Module);
        Assert.Equal(["shutdown c", "shutdown b", "shutdown a"], log.Where(l => l.StartsWith("shutdown", StringComparison.Ordinal)));
        host.Shutdown(); // once only
        Assert.Equal(3, log.Count(l => l.StartsWith("shutdown", StringComparison.Ordinal)));
    }

    [Fact]
    public void ModulesStartOnce_AndIdsAreUnique()
    {
        var log = new List<string>();
        var host = new ModuleHost([new Recorder("a", log)]);
        host.Start(new ComponentRegistry());
        Assert.Throws<InvalidOperationException>(() => host.Start(new ComponentRegistry()));
        Assert.Equal(["register a", "initialize a"], log);
        Assert.Throws<ArgumentException>(() => new ModuleHost([new Recorder("a", log), new Recorder("a", log)]));

        var neverStarted = new ModuleHost([new Recorder("x", log)]);
        neverStarted.Shutdown();
        Assert.DoesNotContain("shutdown x", log);
    }

    [Fact]
    public void ModulesWithoutServices_UseTheDefaultLifecycle()
    {
        var registry = new ComponentRegistry();
        var host = new ModuleHost([new RegistersOnly()]);
        host.Start(registry);
        Assert.True(registry.TryGetType("MNVTEngine.Transform", out _));
        host.Shutdown();
    }

    private sealed class RegistersOnly : IEngineModule
    {
        public string Id => "only";

        public void Register(ComponentRegistry components) => components.Register<Transform>();
    }
}
