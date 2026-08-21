using TerminalTranslator.Core.Parsing;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Commands;

public static class ProductionRuntimeComposition
{
    public static ProductionTranslationPipeline CreateTranslationPipeline(
        Guid sessionId,
        ITranslationProvider provider,
        ITranslationEventSink eventSink,
        TimeSpan providerTimeout) =>
        new(
            sessionId,
            new VtTextExtractor(),
            new EnglishCandidateClassifier(),
            new TranslationCoordinator(provider, eventSink, new SystemClock(), providerTimeout));
}
