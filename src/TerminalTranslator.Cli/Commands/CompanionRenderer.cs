using TerminalTranslator.Windows.Ipc;

namespace TerminalTranslator.Cli.Commands;

public sealed class CompanionRenderer(TextWriter output)
{
    public void Render(TranslationEventMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        output.WriteLine($"[{message.Sequence}] {message.SourceText.Split('\n')[0].TrimStart()}");
        string[] translatedLines = message.TranslatedText
            .ReplaceLineEndings("\n")
            .TrimEnd('\n')
            .Split('\n');
        for (int index = 0; index < translatedLines.Length; index++)
        {
            int sourceIndent = index < message.Layout.Indent.Count ? message.Layout.Indent[index] : 0;
            output.WriteLine($"{new string(' ', sourceIndent + 4)}{translatedLines[index].TrimStart()}");
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
