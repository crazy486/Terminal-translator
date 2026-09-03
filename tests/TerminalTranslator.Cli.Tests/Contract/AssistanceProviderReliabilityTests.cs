using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Core.Privacy;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class AssistanceProviderReliabilityTests
{
    private const string SensitiveInput = "T135 synthetic terminal content must never enter diagnostics";

    [TestMethod]
    public async Task Success_RecordsEndpointModelStatusAndTimingsWithoutRequestContent()
    {
        RecordingSink diagnostics = new();
        using HttpClient client = new(new TestHttpMessageHandler((_, _) => Task.FromResult(Success())));

        AssistanceResult result = await Create(client, diagnostics).CompleteAsync(Request(), CancellationToken.None);

        Assert.AreEqual("translated", result.Translation);
        ProviderRequestDiagnostic diagnostic = diagnostics.Single();
        Assert.AreEqual("provider.example", diagnostic.Endpoint.Host);
        Assert.AreEqual("reliability-model", diagnostic.Model);
        Assert.AreEqual(200, diagnostic.HttpStatusCode);
        Assert.AreEqual("success", diagnostic.Outcome);
        Assert.IsNotNull(diagnostic.ResponseHeadersElapsedMilliseconds);
        Assert.IsTrue(diagnostic.ResponseCompletedElapsedMilliseconds >= diagnostic.ResponseHeadersElapsedMilliseconds);
        AssertDiagnosticIsContentFree(diagnostic);
    }

    [TestMethod]
    public async Task NetworkFailure_IsClassifiedAndAttemptedOnce()
    {
        RecordingSink diagnostics = new();
        TestHttpMessageHandler handler = new((_, _) => throw new HttpRequestException("synthetic socket failure"));
        using HttpClient client = new(handler);

        TranslationProviderException failure = await Assert.ThrowsExactlyAsync<TranslationProviderException>(
            () => Create(client, diagnostics).CompleteAsync(Request(), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Unavailable, failure.Code);
        Assert.AreEqual(TranslationProviderFailureSource.Network, failure.Details?.Source);
        Assert.AreEqual(typeof(HttpRequestException).FullName, failure.Details?.ExceptionType);
        Assert.AreEqual(1, handler.Requests.Count);
        AssertDiagnosticIsContentFree(diagnostics.Single());
    }

    [TestMethod]
    public async Task NonSuccessHttpStatus_IsDistinctAndAttemptedOnce()
    {
        RecordingSink diagnostics = new();
        TestHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using HttpClient client = new(handler);

        TranslationProviderException failure = await Assert.ThrowsExactlyAsync<TranslationProviderException>(
            () => Create(client, diagnostics).CompleteAsync(Request(), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Unavailable, failure.Code);
        Assert.AreEqual(TranslationProviderFailureSource.HttpStatus, failure.Details?.Source);
        Assert.AreEqual(503, failure.Details?.HttpStatusCode);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task Timeout_UsesTransportBudgetAndHasBoundedTotalLatencyWithoutRetry()
    {
        RecordingSink diagnostics = new();
        TestHttpMessageHandler handler = new(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new AssertFailedException("Canceled request cannot complete normally.");
        });
        using HttpClient client = new(handler);
        Stopwatch timer = Stopwatch.StartNew();

        TranslationProviderException failure = await Assert.ThrowsExactlyAsync<TranslationProviderException>(
            () => Create(client, diagnostics, TimeSpan.FromMilliseconds(100)).CompleteAsync(Request(), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Timeout, failure.Code);
        Assert.AreEqual(TranslationProviderFailureSource.Timeout, failure.Details?.Source);
        Assert.AreEqual("provider-timeout", failure.Details?.CancellationReason);
        Assert.AreEqual("transport-cancel-after", failure.Details?.TimeoutSource);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.IsLessThan(TimeSpan.FromSeconds(2), timer.Elapsed);
    }

    [TestMethod]
    public async Task CallerCancellation_PropagatesImmediatelyWithoutRetryOrTimeoutRelabeling()
    {
        RecordingSink diagnostics = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestHttpMessageHandler handler = new(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Success();
        });
        using HttpClient client = new(handler);
        using CancellationTokenSource caller = new();
        Task<AssistanceResult> pending = Create(client, diagnostics, TimeSpan.FromSeconds(5))
            .CompleteAsync(Request(), caller.Token);
        await started.Task;

        caller.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => pending);
        Assert.AreEqual(1, handler.Requests.Count);
        ProviderRequestDiagnostic diagnostic = diagnostics.Single();
        Assert.AreEqual("canceled", diagnostic.Outcome);
        Assert.AreEqual("caller-cancellation", diagnostic.CancellationReason);
        Assert.IsNull(diagnostic.TimeoutSource);
    }

    [TestMethod]
    public async Task HandlerTimeoutException_IsClassifiedWithoutRetry()
    {
        RecordingSink diagnostics = new();
        TestHttpMessageHandler handler = new((_, _) => throw new TimeoutException("synthetic handler timeout"));
        using HttpClient client = new(handler);

        TranslationProviderException failure = await Assert.ThrowsExactlyAsync<TranslationProviderException>(
            () => Create(client, diagnostics).CompleteAsync(Request(), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Timeout, failure.Code);
        Assert.AreEqual(typeof(TimeoutException).FullName, failure.Details?.ExceptionType);
        Assert.AreEqual("http-client-or-handler", failure.Details?.TimeoutSource);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task MalformedResponses_ReportContentFreeRootBoundaryAndSubtypeWithoutRetry()
    {
        (HttpResponseMessage Response, string ExpectedSubtype)[] cases =
        {
            (new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{not-json", Encoding.UTF8, "application/json"),
            }, "outer-json-invalid"),
            (OuterResponse("not-json-content"), "inner-json-invalid"),
            (OuterResponse(null, reasoningContent: "synthetic reasoning"), "missing-content"),
            (OuterResponse("   "), "empty-content"),
            (OuterResponse("{\"translation\":\"partial\"", "length"), "truncated/finish-length"),
            (OuterResponse("{\"translation\":\"translated\"}"), "schema-invalid"),
            (OuterResponse("```json\n{\"translation\":\"translated\",\"recommendation\":\"recommendation\"}\n```"), "inner-json-invalid"),
            (OuterResponse(new string('x', SafeChatCompletionTransport.MaximumResponseBytes)), "response-too-large"),
        };

        foreach ((HttpResponseMessage response, string expectedSubtype) in cases)
        {
            RecordingSink diagnostics = new();
            TestHttpMessageHandler handler = new((_, _) => Task.FromResult(response));
            using HttpClient client = new(handler);

            TranslationProviderException failure = await Assert.ThrowsExactlyAsync<TranslationProviderException>(
                () => Create(client, diagnostics).CompleteAsync(Request(), CancellationToken.None));

            Assert.AreEqual(TranslationErrorCode.InvalidResponse, failure.Code);
            Assert.AreEqual(TranslationProviderFailureSource.MalformedResponse, failure.Details?.Source);
            Assert.AreEqual(expectedSubtype, failure.Details?.MalformedResponseReason);
            Assert.AreEqual(1, handler.Requests.Count);
            ProviderRequestDiagnostic diagnostic = diagnostics.Single();
            Assert.AreEqual(expectedSubtype, diagnostic.MalformedResponseReason);
            if (expectedSubtype == "missing-content") Assert.IsTrue(diagnostic.ReasoningContentPresent);
            AssertDiagnosticIsContentFree(diagnostic);
        }
    }

    [TestMethod]
    public void JsonDiagnostics_AreOptInAndNeverContainPayloadOrCredential()
    {
        using StringWriter disabledOutput = new();
        IProviderRequestDiagnosticSink disabled = JsonProviderRequestDiagnosticSink.Create(disabledOutput, _ => null);
        disabled.Write(SampleDiagnostic());
        Assert.AreEqual(string.Empty, disabledOutput.ToString());

        using StringWriter enabledOutput = new();
        IProviderRequestDiagnosticSink enabled = JsonProviderRequestDiagnosticSink.Create(enabledOutput, name =>
            name == JsonProviderRequestDiagnosticSink.EnableEnvironmentVariable ? "1" : null);
        enabled.Write(SampleDiagnostic());
        string rendered = enabledOutput.ToString();
        StringAssert.Contains(rendered, "provider.example");
        StringAssert.Contains(rendered, "reliability-model");
        StringAssert.Contains(rendered, "inner-json-invalid");
        Assert.IsFalse(rendered.Contains("query-secret", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains(SensitiveInput, StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("synthetic-api-key", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PipelineDiagnostics_AreOptInAggregateOnlyAndContentFree()
    {
        AssistancePipelineDiagnostic diagnostic = new(
            AssistancePipelineStage.Eligibility,
            PreviousCommandResultKind.Success,
            RetrievedOutputUtf8Bytes: 1,
            AsciiLetterCount: 0,
            ControlCharacterCount: 0,
            EnglishEligible: false);
        using StringWriter disabledOutput = new();
        JsonAssistancePipelineDiagnosticSink.Create(disabledOutput, _ => null).Write(diagnostic);
        Assert.AreEqual(string.Empty, disabledOutput.ToString());

        using StringWriter enabledOutput = new();
        JsonAssistancePipelineDiagnosticSink.Create(enabledOutput, _ => "1").Write(diagnostic);
        string rendered = enabledOutput.ToString();
        StringAssert.Contains(rendered, "[tt:pipeline]");
        StringAssert.Contains(rendered, "\"retrievedOutputUtf8Bytes\":1");
        StringAssert.Contains(rendered, "\"englishEligible\":false");
        Assert.IsFalse(rendered.Contains(SensitiveInput, StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("command", StringComparison.OrdinalIgnoreCase));
    }

    private static ChatCompletionAssistanceProvider Create(
        HttpClient client,
        IProviderRequestDiagnosticSink diagnostics,
        TimeSpan? timeout = null) =>
        new(
            ProviderSettings.Create(
                new Uri("https://provider.example/v1/chat/completions"),
                "reliability-model",
                "TT_T135_TEST_KEY",
                timeout ?? TimeSpan.FromSeconds(1)),
            client,
            _ => "synthetic-api-key",
            AssistanceRequestVariableInputSerializer.Serialize,
            diagnostics);

    private static AuthorizedAssistanceRequest Request()
    {
        AssistanceRequest request = AssistanceRequest.CreateLastTranslation(
            "synthetic-command",
            SensitiveInput,
            new TerminationFacts(5, false));
        string scope = AssistanceConsentScopes.For(request.Kind);
        MatchingConsentAssertion consent = MatchingConsentAssertion.TryCreate("grant", "grant", scope)!;
        return new AssistancePrivacyGate(new TerminalTranslator.Core.Privacy.SecretDetector())
            .Authorize(request, consent)!;
    }

    private static HttpResponseMessage Success()
    {
        string content = JsonSerializer.Serialize(new { translation = "translated", recommendation = "recommendation" });
        return OuterResponse(content);
    }

    private static HttpResponseMessage OuterResponse(
        string? content,
        string finishReason = "stop",
        string? reasoningContent = null) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = "request-id",
                choices = new[] { new { message = new { content, reasoning_content = reasoningContent }, finish_reason = finishReason } },
            })),
        };

    private static ProviderRequestDiagnostic SampleDiagnostic() => new(
        new Uri("https://provider.example/v1/chat/completions?token=query-secret"),
        "reliability-model",
        DateTimeOffset.UnixEpoch,
        "success",
        200,
        10,
        20,
        null,
        null,
        null,
        "inner-json-invalid",
        false);

    private static void AssertDiagnosticIsContentFree(ProviderRequestDiagnostic diagnostic)
    {
        string serialized = JsonSerializer.Serialize(diagnostic);
        Assert.IsFalse(serialized.Contains(SensitiveInput, StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("synthetic-api-key", StringComparison.Ordinal));
    }

    private sealed class RecordingSink : IProviderRequestDiagnosticSink
    {
        private readonly List<ProviderRequestDiagnostic> _diagnostics = [];

        public void Write(ProviderRequestDiagnostic diagnostic) => _diagnostics.Add(diagnostic);

        public ProviderRequestDiagnostic Single() => _diagnostics.Single();
    }
}
