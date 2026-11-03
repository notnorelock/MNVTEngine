# Logging

All engine, editor and game logging goes through `Microsoft.Extensions.Logging`. The engine has its own provider, `LogHub` (in `MNVTEngine.Core.Logging`). Its design follows UniLog:

- **Named loggers.** A logger's name is its category. It is the full class name for `GameLog.For<T>()` and `ILoggerFactory.CreateLogger<T>()`, or a short name such as `"Scripts"`.
- **Levels per logger name.** A rule applies to that logger and every logger below it. A rule for `MNVTEngine.Physics2D` covers `MNVTEngine.Physics2D.PhysicsWorld2D`. The longest matching rule wins; without one, the default level applies.
- **Call sites.** Every message records the namespace, class and method that logged it. `GameLog` messages also record the file and line.
- **One format** for the console and the log file:

```
15:33:29.162 INF [Scripts] MNVTEngine.Editor.Services.ScriptReloadController.Reload: Scripts reloaded: 4 components (22 ms)
15:40:02.511 INF Platformer.Coin.OnTriggerEnter2D: Collected Coin 3
```

The logger name in brackets is left out when it is the class itself.

## Logging from game code

Every component has a `Log` named after its class:

```csharp
public sealed class Coin : Component, ITriggerHandler2D
{
    public void OnTriggerEnter2D(Collider2D other)
    {
        Log.Info($"Collected {GameObject.Name}");
    }
}
```

Elsewhere, create one:

```csharp
private static readonly GameLog Log = GameLog.For<SaveSystem>(); // or GameLog.For("AI.Pathfinding")
```

- **Methods:** `Trace`, `Debug`, `Info`, `Warn`, `Error(message, exception)`, `Critical(message, exception)` and `IsEnabled(level)`.
- **Before calling an expensive message builder,** check `IsEnabled`: filtered calls cost a level lookup, but the message string is still built.
- **Hosts:** the editor, the Player and exported games set `GameLog.Factory`. Before that, for example in unit tests, messages go nowhere.

Engine code keeps using source-generated `[LoggerMessage]` methods for structured logging. The hub finds their caller with a stack walk, which costs a few microseconds per message that passes the level filter. If the JIT inlined a method, the site can name its caller instead. Turn the stack walk off with `"callSites": false`.

## Levels

**In the editor:** open **Console → Levels...**. There you can:

- set the default level;
- add a rule for any logger seen so far, or for any of their namespace prefixes, or type a name;
- **Save** the rules to `Settings/logging.json`.

**The file format:**

```json
{
  "default": "Information",
  "callSites": true,
  "levels": {
    "MNVTEngine.Physics2D": "Debug",
    "Platformer.Player": "Trace",
    "Scripts": "Warning"
  }
}
```

**Where it is read:**

- The editor reads `Settings/logging.json` when it opens the project.
- An exported game reads an optional `logging.json` next to its executable, so a player can turn on more detail without a rebuild. An invalid file logs a warning and is ignored.

**Level names:** `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` and `None`.

## Sinks

`LogHub.AddSink(ILogSink)` adds outputs:

- `ConsoleLogSink` writes colored output to the console.
- `FileLogSink` writes `Logs/game.log` in exported games. On a write error it reports once to stderr and stops writing; it never crashes the game.
- `InMemoryLoggerProvider` keeps a ring buffer behind the editor Console. Hover a line there to see the namespace, logger, file and line, and the exception.

A custom sink implements `Write(LogEntry)`. `LogEntry.Source` holds the call site. Sinks are called on the thread that logged, so they must be thread-safe and fast.

## Limitations

- Scopes (`BeginScope`) are ignored.
- `.mnvts` `log(...)` messages show the `MissionRunner` as their source, not the script line.
- Exported games take levels only from the file next to the executable; the project's `Settings/logging.json` is not packaged.
