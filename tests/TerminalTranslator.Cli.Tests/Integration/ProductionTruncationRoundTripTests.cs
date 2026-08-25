using System.Text;
using TerminalTranslator.Cli.Commands;
using TerminalTranslator.Cli.Tests.TestDoubles;
using TerminalTranslator.Core.Assistance;
using TerminalTranslator.Core.Capture;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Cli.Tests.Integration;

[TestClass]
public sealed class ProductionTruncationRoundTripTests
{
    [TestMethod]
    public async Task DiskLocalHeadTail_FlowsThroughRetrieverAiSelectorAndRenderer_WithBothDisclosures()
    {
        using TemporaryDirectory directory = new();
        CaptureSessionId session = new(Guid.Parse("89898989-8989-8989-8989-898989898989"), "nonce");
        string output = "English head failure " +
            new string('q', checked((int)CaptureRetentionPolicy.MaximumRetainedBytes)) +
            " English tail failure";
        CommandBoundary boundary = new(session, 3, "dotnet build", true, 1, false);
        CapturedCommand source = new(
            session,
            3,
            boundary.CommandText,
            output,
            boundary,
            LocalCaptureCompleteness.Complete,
            Encoding.UTF8.GetByteCount(output),
            false);
        RetainedCaptureStore store = new(directory.Path);
        FinalizationResult finalized = await new CapturedCommandFinalizer(store).FinalizeAsync(
            new CapturedCommandCandidate(
                "roundtrip",
                source.Sequence,
                Encoding.UTF8.GetBytes(source.Output),
                RetainedCommandRecordCodec.SerializeMetadata(source),
                true,
                source));
        Assert.AreEqual(FinalizationOutcome.Published, finalized.Outcome);

        RetainedGeneration generation = (await store.LoadCommittedAsync())!;
        RetainedRecordDescriptor descriptor = generation.Records.Single();
        CapturedCommand fromDisk = RetainedCommandRecordCodec.Deserialize(
            await store.ReadContentAsync(descriptor),
            await store.ReadMetadataAsync(descriptor));
        PreviousCommandResult retrieval = PreviousCommandRetriever.Retrieve(new PreviousCommandRetrievalState(
            CapturePreference.Enabled,
            CaptureHealthState.Healthy,
            session,
            [fromDisk],
            true));
        RecordingAssistanceProvider provider = new();
        LastAssistanceCoordinator coordinator = new(
            () => retrieval,
            provider,
            new ApprovedAssistanceRequestAuthorizer(),
            new AiInputBudgetPolicy(512),
            isEligible: _ => true);
        using StringWriter rendered = new();

        Assert.AreEqual(0, await LastCommand.Create(coordinator.ExecuteAsync, rendered).Parse([]).InvokeAsync());

        AssistanceRequest sent = provider.Requests.Single().Request;
        Assert.AreEqual(LocalCaptureCompleteness.LocalHeadTail, sent.LocalCompleteness);
        Assert.AreEqual(AiInputCompleteness.AiHeadTail, sent.AiCompleteness);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(output), sent.OriginalOutputBytes);
        Assert.IsLessThan(fromDisk.Output.Length, sent.SelectedOutput.Length);
        Assert.AreEqual(2, rendered.ToString().Split(Environment.NewLine)
            .Count(line => line.StartsWith("[tt]", StringComparison.Ordinal)));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-truncation-roundtrip-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
