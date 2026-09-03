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
            return ExitCode(outcome);
        });
        return command;
    }

    internal static Command Create(
        Func<IProviderActivity, CancellationToken, Task<LastAssistanceOutcome>> execute,
        TextWriter? output = null,
        Func<TextWriter, IProviderActivity>? activityFactory = null,
        Func<bool>? isInteractive = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        TextWriter standardOutput = output ?? Console.Out;
        bool usesConsoleOutput = output is null;
        activityFactory ??= writer => new DelayedSpinnerProviderActivity(
            writer,
            isInteractive ?? (() =>
                usesConsoleOutput &&
                Environment.UserInteractive &&
                !Console.IsOutputRedirected));
        InlineAssistanceRenderer renderer = new(standardOutput);
        Command command = new("last", "Translate the strict previous completed command output.");
        command.SetAction(async (_, cancellationToken) =>
        {
            IProviderActivity activity = activityFactory(standardOutput);
            LastAssistanceOutcome outcome = await execute(activity, cancellationToken).ConfigureAwait(false);
            renderer.Render(outcome);
            return ExitCode(outcome);
        });
        return command;
    }

    private static int ExitCode(LastAssistanceOutcome outcome) =>
        outcome.Failure is AssistanceFailureKind.None or AssistanceFailureKind.NoPreviousCommand or
            AssistanceFailureKind.NoOutput or AssistanceFailureKind.NoTranslatableEnglish ? 0 : 5;
}
