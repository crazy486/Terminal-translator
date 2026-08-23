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
        int viewportRows = 30,
        Func<string, bool>? commandEchoFilter = null,
        Func<string, AnalysisLineDisposition>? analysisLineFilter = null,
        ISubmittedCommandTracker? submittedCommandTracker = null,
        SecretDetector? secretDetector = null,
        TranslationSession? session = null,
        string? providerFingerprint = null,
        Func<PrivacyDecision, CancellationToken, ValueTask>? privacyDecisionSink = null,
        Func<int, CancellationToken, ValueTask>? overloadSink = null,
        ITranslationRuntimeObserver? runtimeObserver = null)
    {
        SystemClock clock = new();
        TranslationWorkQueue queue = new(clock, runtimeObserver);
        TranslationCoordinator coordinator = new(
            provider,
            eventSink,
            clock,
            providerTimeout,
            secretDetector,
            session,
            providerFingerprint,
            privacyDecisionSink,
            runtimeObserver);
        TranslationWorker worker = new(
            queue,
            coordinator,
            session,
            providerErrorSink,
            runtimeObserver,
            clock);
        return new ProductionTranslationPipeline(
            sessionId,
            new VtTextExtractor(viewportColumns, viewportRows),
            new EnglishCandidateClassifier(),
            clock,
            worker,
            commandEchoFilter: commandEchoFilter,
            analysisLineFilter: analysisLineFilter,
            submittedCommandTracker: submittedCommandTracker,
            session: session,
            providerFingerprint: providerFingerprint,
            secretDetector: secretDetector,
            privacyDecisionSink: privacyDecisionSink,
            overloadSink: overloadSink,
            runtimeObserver: runtimeObserver);
    }
}
