@echo off
rem mnvt from the engine sources: builds the CLI when needed, then runs it. Add this folder to PATH to use `mnvt` anywhere.
dotnet run --project "%~dp0..\src\Tooling\MNVTEngine.CLI" -v:q -- %*
