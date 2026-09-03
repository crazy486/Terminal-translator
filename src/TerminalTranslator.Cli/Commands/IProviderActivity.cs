namespace TerminalTranslator.Cli.Commands;

internal interface IProviderActivity
{
    Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}
