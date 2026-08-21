using System.Reflection;
using TerminalTranslator.Core.Models;
using TerminalTranslator.Core.Sessions;
using TerminalTranslator.Core.Tests.TestDoubles;
using TerminalTranslator.Core.Translation;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class StatusAggregatorTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [TestMethod]
    public void ProviderFailures_AreAggregatedAndRateLimitedWithinFiveSecondWindow()
    {
        FakeClock clock = new();
        StatusAggregatorContractSubject aggregator = StatusAggregatorContractSubject.Create(SessionId, clock);

        StatusEvent? first = aggregator.RecordProviderFailure(TranslationErrorCode.Timeout);
        StatusEvent? second = aggregator.RecordProviderFailure(TranslationErrorCode.Timeout);
        StatusEvent? third = aggregator.RecordProviderFailure(TranslationErrorCode.Timeout);
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        StatusEvent? fourth = aggregator.RecordProviderFailure(TranslationErrorCode.Timeout);

        Assert.IsNotNull(first);
        Assert.AreEqual(1, first.Count);
        Assert.IsNull(second);
        Assert.IsNull(third);
        Assert.IsNull(fourth);
    }

    [TestMethod]
    public void ProviderFailure_AfterFiveSecondWindowEmitsNewNoticeWithAggregatedCount()
    {
        FakeClock clock = new();
        StatusAggregatorContractSubject aggregator = StatusAggregatorContractSubject.Create(SessionId, clock);
        Assert.IsNotNull(aggregator.RecordProviderFailure(TranslationErrorCode.Unavailable));
        Assert.IsNull(aggregator.RecordProviderFailure(TranslationErrorCode.Unavailable));
        Assert.IsNull(aggregator.RecordProviderFailure(TranslationErrorCode.Unavailable));

        clock.Advance(TimeSpan.FromSeconds(5));
        StatusEvent? next = aggregator.RecordProviderFailure(TranslationErrorCode.Unavailable);

        Assert.IsNotNull(next);
        Assert.AreEqual(StatusKind.ProviderError, next.Kind);
        Assert.AreEqual(3, next.Count, "The new failure and two suppressed failures should be aggregated.");
    }

    [TestMethod]
    public void PrivacySkips_AreAggregatedAndRateLimitedWithinFiveSecondWindow()
    {
        FakeClock clock = new();
        StatusAggregatorContractSubject aggregator = StatusAggregatorContractSubject.Create(SessionId, clock);

        StatusEvent? first = aggregator.RecordPrivacySkip(PrivacyReasonCode.TokenShape);
        StatusEvent? second = aggregator.RecordPrivacySkip(PrivacyReasonCode.TokenShape);
        StatusEvent? third = aggregator.RecordPrivacySkip(PrivacyReasonCode.TokenShape);

        Assert.IsNotNull(first);
        Assert.AreEqual(StatusKind.PrivacySkip, first.Kind);
        Assert.AreEqual(1, first.Count);
        Assert.IsNull(second);
        Assert.IsNull(third);
    }

    [TestMethod]
    public void OverloadDrops_AreAggregatedAndRateLimitedWithinFiveSecondWindow()
    {
        FakeClock clock = new();
        StatusAggregatorContractSubject aggregator = StatusAggregatorContractSubject.Create(SessionId, clock);

        StatusEvent? first = aggregator.RecordOverload(2);
        StatusEvent? second = aggregator.RecordOverload(3);
        clock.Advance(TimeSpan.FromSeconds(5));
        StatusEvent? next = aggregator.RecordOverload(4);

        Assert.IsNotNull(first);
        Assert.AreEqual(StatusKind.Degraded, first.Kind);
        Assert.AreEqual(2, first.Count);
        Assert.IsNull(second);
        Assert.IsNotNull(next);
        Assert.AreEqual(7, next.Count, "Suppressed and current overload counts should be aggregated.");
    }

    [TestMethod]
    public void ProviderStatus_ContainsOnlyNormalizedContentFreeMetadata()
    {
        const string terminalSource = "The deployment failed for customer Alpha.";
        const string translationText = "部署失败。";
        const string credential = "sk-test-1234567890abcdefghijklmnop";
        const string authorization = "Authorization: Bearer tt_test_secret_987654321";
        const string responseBody = "{\"error\":\"private upstream body\"}";
        InvalidOperationException rawException = new(
            $"{terminalSource} {translationText} {credential} {authorization} {responseBody}");
        FakeClock clock = new();
        StatusAggregatorContractSubject aggregator = StatusAggregatorContractSubject.Create(SessionId, clock);

        StatusEvent? status = aggregator.RecordProviderFailure(
            TranslationErrorCode.Authentication,
            count: 2,
            duration: TimeSpan.FromMilliseconds(320),
            rawException);

        Assert.IsNotNull(status);
        Assert.AreEqual(StatusKind.ProviderError, status.Kind);
        Assert.AreEqual("authentication", status.Code);
        Assert.AreEqual(2, status.Count);
        AssertContentFree(status, terminalSource, translationText, credential, authorization, responseBody, rawException.Message);
    }

    [TestMethod]
    public void PrivacyStatus_ExposesOnlyGenericReasonAndCount()
    {
        FakeClock clock = new();
        StatusAggregatorContractSubject aggregator = StatusAggregatorContractSubject.Create(SessionId, clock);

        StatusEvent? status = aggregator.RecordPrivacySkip(PrivacyReasonCode.CredentialAssignment, count: 3);

        Assert.IsNotNull(status);
        Assert.AreEqual(StatusKind.PrivacySkip, status.Kind);
        Assert.AreEqual("credential-assignment", status.Code);
        Assert.AreEqual(3, status.Count);
        CollectionAssert.AreEqual(
            new[] { typeof(PrivacyReasonCode), typeof(int) },
            aggregator.PrivacyInputParameterTypes,
            "The privacy aggregation boundary must not accept matched source text or secret fragments.");
        AssertContentFree(
            status,
            "API_KEY=sk-test-1234567890abcdefghijklmnop",
            "sk-test-1234567890",
            "Bearer tt_test_secret");
    }

    [TestMethod]
    public void OverloadStatus_IsGenericAndContentFree()
    {
        FakeClock clock = new();
        StatusAggregatorContractSubject aggregator = StatusAggregatorContractSubject.Create(SessionId, clock);

        StatusEvent? status = aggregator.RecordOverload(12);

        Assert.IsNotNull(status);
        Assert.AreEqual(StatusKind.Degraded, status.Kind);
        Assert.AreEqual("overload", status.Code);
        Assert.AreEqual(12, status.Count);
        AssertContentFree(status, "raw terminal output", "translated terminal output", "provider response body");
    }

    private static void AssertContentFree(StatusEvent status, params string[] forbiddenValues)
    {
        string rendered = status.ToString();
        foreach (string forbiddenValue in forbiddenValues)
        {
            Assert.IsFalse(
                rendered.Contains(forbiddenValue, StringComparison.OrdinalIgnoreCase),
                $"Status exposed forbidden content: {forbiddenValue}");
        }
    }

    /// <summary>
    /// Test-only reflection seam. It contains no aggregation or filtering behavior and binds every
    /// call to the production StatusAggregator that T062 will provide.
    /// </summary>
    private sealed class StatusAggregatorContractSubject
    {
        private const string ProductionTypeName = "TerminalTranslator.Core.Sessions.StatusAggregator";

        private readonly object _instance;
        private readonly MethodInfo _recordProviderFailure;
        private readonly MethodInfo _recordPrivacySkip;
        private readonly MethodInfo _recordOverload;

        private StatusAggregatorContractSubject(Type type, object instance)
        {
            _instance = instance;
            _recordProviderFailure = RequireMethod(
                type,
                "RecordProviderFailure",
                typeof(TranslationErrorCode),
                typeof(int),
                typeof(TimeSpan?),
                typeof(Exception));
            _recordPrivacySkip = RequireMethod(
                type,
                "RecordPrivacySkip",
                typeof(PrivacyReasonCode),
                typeof(int));
            _recordOverload = RequireMethod(type, "RecordOverload", typeof(int));
        }

        public Type[] PrivacyInputParameterTypes =>
            _recordPrivacySkip.GetParameters().Select(static parameter => parameter.ParameterType).ToArray();

        public static StatusAggregatorContractSubject Create(Guid sessionId, IClock clock)
        {
            Type? type = typeof(StatusEvent).Assembly.GetType(ProductionTypeName, throwOnError: false);
            if (type is null)
            {
                throw new AssertFailedException(
                    $"T062 production type '{ProductionTypeName}' has not been implemented.");
            }

            ConstructorInfo? constructor = type.GetConstructor([typeof(Guid), typeof(IClock)]);
            if (constructor is null)
            {
                throw new AssertFailedException(
                    $"{ProductionTypeName} must expose a constructor accepting Guid and IClock.");
            }

            return new StatusAggregatorContractSubject(type, constructor.Invoke([sessionId, clock]));
        }

        public StatusEvent? RecordProviderFailure(
            TranslationErrorCode reason,
            int count = 1,
            TimeSpan? duration = null,
            Exception? rawException = null) =>
            InvokeStatus(_recordProviderFailure, reason, count, duration, rawException);

        public StatusEvent? RecordPrivacySkip(PrivacyReasonCode reason, int count = 1) =>
            InvokeStatus(_recordPrivacySkip, reason, count);

        public StatusEvent? RecordOverload(int count = 1) =>
            InvokeStatus(_recordOverload, count);

        private StatusEvent? InvokeStatus(MethodInfo method, params object?[] arguments)
        {
            object? result = method.Invoke(_instance, arguments);
            return result switch
            {
                null => null,
                StatusEvent status => status,
                _ => throw new AssertFailedException($"{method.Name} must return StatusEvent or null."),
            };
        }

        private static MethodInfo RequireMethod(Type type, string name, params Type[] parameterTypes) =>
            type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, parameterTypes, null)
            ?? throw new AssertFailedException(
                $"{ProductionTypeName} must expose {name}({string.Join(", ", parameterTypes.Select(static value => value.Name))}).");
    }
}
