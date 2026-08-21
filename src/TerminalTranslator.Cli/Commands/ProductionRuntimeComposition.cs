using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Parsing;
using TerminalTranslator.Core.Privacy;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Commands;

public static class ProductionRuntimeComposition
{
    public static ProductionTranslationPipeline CreateTranslationPipeline(
        Guid sessionId,
        ITranslationProvider provider,
        ITranslationEventSink eventSink,
        TimeSpan providerTimeout,
        Func<TranslationErrorCode, CancellationToken, ValueTask>? providerErrorSink = null,
        int viewportColumns = 120,
        Func<string, bool>? commandEchoFilter = null,
        Func<string, AnalysisLineDisposition>? analysisLineFilter = null,
        SecretDetector? secretDetector = null,
        TranslationSession? session = null,
        string? providerFingerprint = null,
        Func<PrivacyDecision, CancellationToken, ValueTask>? privacyDecisionSink = null,
        Func<int, CancellationToken, ValueTask>? overloadSink = null)
    {
        SystemClock clock = new();
        TranslationWorkQueue queue = new(clock);
        TranslationCoordinator coordinator = new(
            provider,
            eventSink,
            clock,
            providerTimeout,
            secretDetector,
            session,
            providerFingerprint,
            privacyDecisionSink);
        TranslationWorker worker = new(queue, coordinator, session, providerErrorSink);
        return new ProductionTranslationPipeline(
            sessionId,
            new VtTextExtractor(viewportColumns),
            new EnglishCandidateClassifier(),
            clock,
            worker,
            commandEchoFilter: commandEchoFilter,
            analysisLineFilter: analysisLineFilter,
            session: session,
            providerFingerprint: providerFingerprint,
            secretDetector: secretDetector,
            privacyDecisionSink: privacyDecisionSink,
            overloadSink: overloadSink);
    }
}
