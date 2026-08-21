using System.CommandLine;

namespace TerminalTranslator.Cli.Commands;

public static class CommandFactory
{
    public static RootCommand CreateRootCommand()
    {
        RootCommand root = new("Translate useful Windows terminal output in a companion pane.");
        root.Add(ConfigureCommand.Create());
        root.Add(StartCommand.Create());
        root.Add(OnCommand.Create());
        root.Add(CreatePlaceholder("off", "Disable translation for the current session."));
        root.Add(CreatePlaceholder("status", "Show content-free session status."));

        root.Add(HostCommand.Create());
        root.Add(CompanionCommand.Create());
        return root;
    }

    private static Command CreatePlaceholder(string name, string description)
    {
        Command command = new(name, description);
        command.SetAction(_ => 0);
        return command;
    }
}
