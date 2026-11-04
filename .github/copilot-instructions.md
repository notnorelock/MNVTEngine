# Copilot instructions

Follow [AGENTS.md](../AGENTS.md) at the repository root: build and test commands, architecture rules, code conventions and workflow.

Most important:

- .NET 11, warnings are errors. Build with `dotnet build MNVTEngine.sln`, test with `dotnet test --project tests/<Project>`.
- Respect the dependency rules in `tests/MNVTEngine.Architecture.Tests`; engine code never references Editor or Tooling.
- Newtonsoft.Json only, never `TypeNameHandling`. Log through `ILogger` with `[LoggerMessage]`. No swallowed exceptions.
- In ImGui code, use the `Gui.*` text helpers instead of `ImGui.TextColored`/`SetTooltip`/`BulletText`/`TextWrapped`/`TextDisabled`.
