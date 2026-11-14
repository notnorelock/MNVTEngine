// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-03.

using Microsoft.Extensions.Logging;
using MNVTEngine.CLI;

using var logHub = new MNVTEngine.Core.Logging.LogHub().AddSink(new MNVTEngine.Core.Logging.ConsoleLogSink());
using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logHub));
MNVTEngine.GameLog.Factory = loggerFactory;
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // stop child builds cleanly instead of killing the CLI mid-write
    cancel.Cancel();
};

try
{
    return await Cli.RunAsync(args, Console.Out, loggerFactory, cancel.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
