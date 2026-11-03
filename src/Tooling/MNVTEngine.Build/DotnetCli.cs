using System.Diagnostics;

namespace MNVTEngine.Build;

public sealed record DotnetResult(int ExitCode, string Output, TimeSpan Duration, string? StartError = null)
{
    public bool Succeeded => StartError is null && ExitCode == 0;
}

/// <summary>Runs the .NET SDK's <c>dotnet</c> command (development tooling only; exported games never need it) and other tools.</summary>
public static class DotnetCli
{
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the process tree is killed.</exception>
    public static Task<DotnetResult> RunAsync(IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        RunProcessAsync("dotnet", arguments, workingDirectory, timeout, cancellationToken);

    /// <summary>Runs any executable with captured output and a time limit.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the process tree is killed.</exception>
    public static async Task<DotnetResult> RunProcessAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en"; // diagnostics are parsed

        var watch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new DotnetResult(-1, "", watch.Elapsed, fileName == "dotnet" ? $"Could not start 'dotnet' (is the .NET SDK installed and on PATH?): {ex.Message}" : $"Could not start '{fileName}': {ex.Message}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new DotnetResult(-1, "", watch.Elapsed, $"'{Path.GetFileName(fileName)}' timed out after {timeout.TotalSeconds:0} s.");
        }

        return new DotnetResult(process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false), watch.Elapsed);
    }
}
