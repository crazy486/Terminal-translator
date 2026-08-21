using TerminalTranslator.Cli.Commands;

return await CommandFactory.CreateRootCommand().Parse(args).InvokeAsync();
