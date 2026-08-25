using System.CommandLine;

namespace TerminalTranslator.Cli.Commands;

public static class CaptureIntegrationCommand
{
    public static Command Create(
        Func<IReadOnlyList<string>, CancellationToken, Task<int>>? handler = null)
    {
        Argument<string[]> arguments = new("arguments")
        {
            Arity = ArgumentArity.OneOrMore,
        };
        Command command = new("__capture", "Internal PowerShell capture maintenance bridge.")
        {
            Hidden = true,
        };
        command.Arguments.Add(arguments);
        command.SetAction((parseResult, cancellationToken) =>
        {
            string[] values = parseResult.GetRequiredValue(arguments);
            return (handler ?? new CaptureMaintenanceBridge().HandleAsync)(values, cancellationToken);
        });
        return command;
    }
}
