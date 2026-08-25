using System.CommandLine;
using TerminalTranslator.Core.Assistance;

namespace TerminalTranslator.Cli.Commands;

public static class LastCommand
{
    public static Command Create(
        Func<CancellationToken, Task<LastAssistanceOutcome>>? execute = null,
        TextWriter? output = null)
    {
        Command command = new("last", "Translate the strict previous completed command output.");
        execute ??= _ => Task.FromResult(new LastAssistanceOutcome(AssistanceFailureKind.CaptureUnavailable));
        InlineAssistanceRenderer renderer = new(output ?? Console.Out);
        command.SetAction(async (_, cancellationToken) =>
        {
            LastAssistanceOutcome outcome = await execute(cancellationToken).ConfigureAwait(false);
            renderer.Render(outcome);
            return outcome.Failure is AssistanceFailureKind.None or AssistanceFailureKind.NoPreviousCommand or
                AssistanceFailureKind.NoOutput or AssistanceFailureKind.NoTranslatableEnglish ? 0 : 5;
        });
        return command;
    }
}
