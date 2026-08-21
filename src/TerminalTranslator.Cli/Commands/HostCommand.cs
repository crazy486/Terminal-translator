using System.CommandLine;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Parsing;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Commands;

public sealed class BasicHostPipeline(
    Guid sessionId,
    VtTextExtractor extractor,
    EnglishCandidateClassifier classifier,
    TranslationCoordinator coordinator)
{
    private ulong _sequence;

    public bool Enabled { get; private set; }

    public void Enable() => Enabled = true;

    public void Disable() => Enabled = false;

    public async Task<int> ProcessAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return 0;
        }

        int translated = 0;
        foreach (ExtractedText extracted in extractor.Feed(bytes.Span))
        {
            CandidateClassification classification = classifier.Classify(
                extracted.Text,
                extracted.Layout,
                extracted.Boundary);
            if (!classification.IsEligible)
            {
                continue;
            }

            OutputSegment segment = new(
                sessionId,
                1,
                ++_sequence,
                extracted.Text,
                extracted.Layout,
                extracted.Boundary,
                classification.Priority,
                classification.DeduplicationKey,
                TimeSpan.Zero);
            if (await coordinator.TranslateAsync(segment, cancellationToken).ConfigureAwait(false) is not null)
            {
                translated++;
            }
        }

        return translated;
    }
}

public static class HostCommand
{
    public static Command Create()
    {
        Option<string> session = new("--session") { Required = true };
        Option<string> workingDirectory = new("--working-directory") { Required = true };
        Command command = new("__host", "Internal program host.") { Hidden = true };
        command.Options.Add(session);
        command.Options.Add(workingDirectory);
        command.SetAction(parseResult =>
        {
            string requestedSession = parseResult.GetRequiredValue(session);
            string? inheritedSession = Environment.GetEnvironmentVariable("TT_SESSION_ID");
            string? nonce = Environment.GetEnvironmentVariable("TT_SESSION_NONCE");
            if (!string.Equals(requestedSession, inheritedSession, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(nonce))
            {
                return 5;
            }

            return Directory.Exists(parseResult.GetRequiredValue(workingDirectory)) ? 0 : 6;
        });
        return command;
    }
}
