using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Commands;

public sealed class CompanionRenderer(TextWriter output)
{
    public void Render(TranslationEventMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string prefix = $"[{message.Sequence}] ";
        string continuationPrefix = new(' ', prefix.Length);
        string[] sourceLines = message.SourceText
            .ReplaceLineEndings("\n")
            .TrimEnd('\n')
            .Split('\n');
        for (int index = 0; index < sourceLines.Length; index++)
        {
            output.WriteLine($"{(index == 0 ? prefix : continuationPrefix)}{sourceLines[index]}");
        }

        string[] translatedLines = message.TranslatedText
            .ReplaceLineEndings("\n")
            .TrimEnd('\n')
            .Split('\n');
        for (int index = 0; index < translatedLines.Length; index++)
        {
            int sourceIndent = index < message.Layout.Indent.Count ? message.Layout.Indent[index] : 0;
            output.WriteLine(
                $"{new string(' ', prefix.Length + sourceIndent)}{translatedLines[index].TrimStart()}");
        }

        output.WriteLine();
    }

    public void Render(StatusEventMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        output.WriteLine($"status: {message.Type} ({message.Code})");
    }

    public void Render(StateEventMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        output.WriteLine($"translation: {message.State} ({message.ProviderHost}, {message.Model})");
    }
}
