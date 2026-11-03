using Microsoft.Extensions.Logging;
using MNVTEngine.CLI;

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
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
