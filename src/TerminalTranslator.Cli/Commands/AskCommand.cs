using System.CommandLine;
using TerminalTranslator.Core.Assistance;

namespace TerminalTranslator.Cli.Commands;

public static class AskCommand
{
    public static Command Create(
        Func<string, CancellationToken, Task<AskAssistanceOutcome>>? executeQuestion = null,
        Func<string, CancellationToken, Task<AskAssistanceOutcome>>? executeLast = null,
        TextWriter? output = null,
        Func<Func<string, CancellationToken, Task<AskAssistanceOutcome>>>? contextualExecutorFactory = null)
    {
        Argument<string[]> question = RequiredQuestion();
        Command command = new("ask", "Ask one stateless question.");
        command.Arguments.Add(question);

        Argument<string[]> lastQuestion = RequiredQuestion();
        Command last = new("last", "Ask one stateless question about the strict previous command.");
        last.Arguments.Add(lastQuestion);

        InlineAssistanceRenderer renderer = new(output ?? Console.Out);
        executeQuestion ??= (_, _) => Task.FromResult(new AskAssistanceOutcome(AssistanceFailureKind.ProviderError));
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string text = Join(parseResult.GetRequiredValue(question));
            AskAssistanceOutcome outcome = await executeQuestion(text, cancellationToken).ConfigureAwait(false);
            renderer.RenderAnswer(outcome);
            return ExitCode(outcome);
        });

        last.SetAction(async (parseResult, cancellationToken) =>
        {
            string text = Join(parseResult.GetRequiredValue(lastQuestion));
            Func<string, CancellationToken, Task<AskAssistanceOutcome>> executor =
                contextualExecutorFactory?.Invoke() ?? executeLast ??
                ((_, _) => Task.FromResult(new AskAssistanceOutcome(AssistanceFailureKind.CaptureUnavailable)));
            AskAssistanceOutcome outcome = await executor(text, cancellationToken).ConfigureAwait(false);
            renderer.RenderAnswer(outcome);
            return ExitCode(outcome);
        });
        command.Subcommands.Add(last);
        return command;
    }

    private static Argument<string[]> RequiredQuestion()
    {
        Argument<string[]> argument = new("question")
        {
            Arity = ArgumentArity.OneOrMore,
        };
        argument.Validators.Add(result =>
        {
            if (result.Tokens.Count == 0 || result.Tokens.All(token => string.IsNullOrWhiteSpace(token.Value)))
                result.AddError("Question text is required.");
            if (result.Tokens.Any(token => token.Value.StartsWith("--", StringComparison.Ordinal)))
                result.AddError("Question text cannot use an option alias.");
        });
        return argument;
    }

    private static string Join(IReadOnlyList<string> tokens) => string.Join(' ', tokens);

    private static int ExitCode(AskAssistanceOutcome outcome) => outcome.Failure switch
    {
        AssistanceFailureKind.None or AssistanceFailureKind.NoPreviousCommand or AssistanceFailureKind.NoOutput => 0,
        AssistanceFailureKind.CaptureDisabled or AssistanceFailureKind.CaptureUnavailable or
            AssistanceFailureKind.UnreliableOrCorrupt => 5,
        _ => 6,
    };
}
