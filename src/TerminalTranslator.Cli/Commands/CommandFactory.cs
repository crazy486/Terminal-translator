using System.CommandLine;

namespace TerminalTranslator.Cli.Commands;

public static class CommandFactory
{
    public static async Task<int> InvokeAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ParseResult parseResult = CreateRootCommand().Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            await parseResult.InvokeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return 2;
        }

        return await parseResult.InvokeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static RootCommand CreateRootCommand(OnDemandAssistanceRuntimeComposition? onDemand = null)
    {
        onDemand ??= OnDemandAssistanceRuntimeComposition.CreateProduction();
        RootCommand root = new("Translate useful Windows terminal output in a companion pane.");
        root.Add(ConfigureCommand.Create());
        root.Add(StartCommand.Create());
        root.Add(OnCommand.Create());
        root.Add(OffCommand.Create());
        root.Add(StatusCommand.Create());

        root.Add(LastCommand.Create(onDemand.ExecuteLastAsync));
        root.Add(AskCommand.Create(
            onDemand.ExecuteQuestionAsync,
            contextualExecutorFactory: () => onDemand.ExecuteLastQuestionAsync));

        root.Add(CaptureIntegrationCommand.Create());

        root.Add(HostCommand.Create());
        root.Add(CompanionCommand.Create());
        return root;
    }
}
