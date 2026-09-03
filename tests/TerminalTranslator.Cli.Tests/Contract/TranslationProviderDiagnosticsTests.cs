using System.Net;
using System.Text;
using TerminalTranslator.Cli.Configuration;
using TerminalTranslator.Cli.Providers;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Cli.Tests.Contract;

[TestClass]
public sealed class TranslationProviderDiagnosticsTests
{
    private const string SensitiveSource = "T138 terminal source must never enter provider diagnostics";

    [TestMethod]
    public async Task Success_RecordsSafeTransportMetadata()
    {
        RecordingSink diagnostics = new();
        using HttpClient client = new(new TestHttpMessageHandler((_, _) => Task.FromResult(Success())));

        TranslationResult result = await Create(client, diagnostics).TranslateAsync(Request(), CancellationToken.None);

        Assert.AreEqual("translated", result.TranslatedText);
        ProviderRequestDiagnostic diagnostic = diagnostics.Single();
        Assert.AreEqual("provider.example", diagnostic.Endpoint.Host);
        Assert.AreEqual("live-model", diagnostic.Model);
        Assert.AreEqual("success", diagnostic.Outcome);
        Assert.AreEqual(200, diagnostic.HttpStatusCode);
        Assert.IsNotNull(diagnostic.ResponseHeadersElapsedMilliseconds);
        Assert.IsTrue(diagnostic.ResponseCompletedElapsedMilliseconds >= diagnostic.ResponseHeadersElapsedMilliseconds);
        AssertContentFree(diagnostic);
    }

    [TestMethod]
    public async Task Timeout_RecordsTransportDeadlineSourceWithoutRetry()
    {
        RecordingSink diagnostics = new();
        TestHttpMessageHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertFailedException("The transport deadline must cancel the request.");
        });
        using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };

        TranslationProviderException failure = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            Create(client, diagnostics, TimeSpan.FromMilliseconds(100)).TranslateAsync(
                Request(TimeSpan.FromMilliseconds(100)), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.Timeout, failure.Code);
        ProviderRequestDiagnostic diagnostic = diagnostics.Single();
        Assert.AreEqual("Timeout", diagnostic.Outcome);
        Assert.AreEqual("provider-timeout", diagnostic.CancellationReason);
        Assert.AreEqual("transport-cancel-after", diagnostic.TimeoutSource);
        Assert.AreEqual(1, handler.Requests.Count);
        AssertContentFree(diagnostic);
    }

    [TestMethod]
    public async Task CallerCancellation_RecordsCancellationWithoutTimeoutRelabeling()
    {
        RecordingSink diagnostics = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestHttpMessageHandler handler = new(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Success();
        });
        using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using CancellationTokenSource caller = new();
        Task<TranslationResult> pending = Create(client, diagnostics, TimeSpan.FromSeconds(5))
            .TranslateAsync(Request(TimeSpan.FromSeconds(5)), caller.Token);
        await started.Task;

        caller.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => pending);
        ProviderRequestDiagnostic diagnostic = diagnostics.Single();
        Assert.AreEqual("canceled", diagnostic.Outcome);
        Assert.AreEqual("caller-cancellation", diagnostic.CancellationReason);
        Assert.IsNull(diagnostic.TimeoutSource);
        Assert.AreEqual(1, handler.Requests.Count);
        AssertContentFree(diagnostic);
    }

    [TestMethod]
    public async Task PlainTextMalformedResponse_UsesLiveApplicableSubtype()
    {
        RecordingSink diagnostics = new();
        using HttpClient client = new(new TestHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{}}]}", Encoding.UTF8, "application/json"),
            })));

        TranslationProviderException failure = await Assert.ThrowsExactlyAsync<TranslationProviderException>(() =>
            Create(client, diagnostics).TranslateAsync(Request(), CancellationToken.None));

        Assert.AreEqual(TranslationErrorCode.InvalidResponse, failure.Code);
        ProviderRequestDiagnostic diagnostic = diagnostics.Single();
        Assert.AreEqual("MalformedResponse", diagnostic.Outcome);
        Assert.AreEqual("missing-content", diagnostic.MalformedResponseReason);
        Assert.AreNotEqual("inner-json-invalid", diagnostic.MalformedResponseReason);
        Assert.AreNotEqual("schema-invalid", diagnostic.MalformedResponseReason);
        AssertContentFree(diagnostic);
    }

    private static ChatCompletionTranslationProvider Create(
        HttpClient client,
        IProviderRequestDiagnosticSink diagnostics,
        TimeSpan? timeout = null)
    {
        ProviderSettings settings = ProviderSettings.Create(
            new Uri("https://provider.example/v1/chat/completions?tenant=sensitive"),
            "live-model",
            "TT_T138_KEY",
            timeout ?? TimeSpan.FromSeconds(1));
        return new ChatCompletionTranslationProvider(
            settings,
            client,
            _ => "synthetic-credential",
            secretDetector: null,
            requestAuthorization: null,
            diagnosticSink: diagnostics);
    }

    private static TranslationRequest Request(TimeSpan? timeout = null) =>
        new(7, 11, SensitiveSource, "en", "zh-Hans", timeout ?? TimeSpan.FromSeconds(1));

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"id\":\"req-live\",\"choices\":[{\"message\":{\"content\":\"translated\"}}]}",
            Encoding.UTF8,
            "application/json"),
    };

    private static void AssertContentFree(ProviderRequestDiagnostic diagnostic)
    {
        string rendered = System.Text.Json.JsonSerializer.Serialize(diagnostic);
        Assert.IsFalse(rendered.Contains(SensitiveSource, StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("synthetic-credential", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("translated", StringComparison.Ordinal));
    }

    private sealed class RecordingSink : IProviderRequestDiagnosticSink
    {
        private readonly List<ProviderRequestDiagnostic> _items = [];

        public void Write(ProviderRequestDiagnostic diagnostic) => _items.Add(diagnostic);

        public ProviderRequestDiagnostic Single() => _items.Single();
    }
}
