using TerminalTranslator.Core.Capture;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class PreviousCommandRetrieverTests
{
    private static readonly CaptureSessionId Session = new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "nonce-c");

    [TestMethod]
    public void Retrieve_ReturnsReliableSnapshot()
    {
        PreviousCommandResult result = PreviousCommandRetriever.Retrieve(State([Command(1, "git status", "On branch main")]));
        Assert.AreEqual(PreviousCommandResultKind.Success, result.Kind);
        Assert.AreEqual("git status", result.Snapshot!.CommandText);
        Assert.AreEqual("On branch main", result.Snapshot.Output);
    }

    [TestMethod]
    public void Retrieve_DistinguishesNoPreviousAndStrictNoOutputWithoutLookingBack()
    {
        Assert.AreEqual(
            PreviousCommandResultKind.NoPreviousCommand,
            PreviousCommandRetriever.Retrieve(State([])).Kind);

        PreviousCommandResult result = PreviousCommandRetriever.Retrieve(State([
            Command(1, "git status", "useful output"),
            Command(2, "Set-Location ..", string.Empty),
        ]));
        Assert.AreEqual(PreviousCommandResultKind.NoOutput, result.Kind);
        Assert.IsNull(result.Snapshot);
    }

    [TestMethod]
    public void Retrieve_MapsDisabledUnavailableAndCorruptExclusively()
    {
        Assert.AreEqual(
            PreviousCommandResultKind.CaptureDisabled,
            PreviousCommandRetriever.Retrieve(State([], CapturePreference.Disabled)).Kind);
        Assert.AreEqual(
            PreviousCommandResultKind.CaptureUnavailable,
            PreviousCommandRetriever.Retrieve(State([], health: CaptureHealthState.Unavailable)).Kind);
        Assert.AreEqual(
            PreviousCommandResultKind.UnreliableOrCorrupt,
            PreviousCommandRetriever.Retrieve(State([], storeReliable: false)).Kind);
    }

    [TestMethod]
    public void Retrieve_RejectsWrongSessionAndInconsistentSequence()
    {
        CaptureSessionId other = new(Guid.NewGuid(), "other");
        Assert.AreEqual(
            PreviousCommandResultKind.UnreliableOrCorrupt,
            PreviousCommandRetriever.Retrieve(State([Command(1, "echo", "output", other)])).Kind);
        Assert.AreEqual(
            PreviousCommandResultKind.UnreliableOrCorrupt,
            PreviousCommandRetriever.Retrieve(State([Command(2, "two", "two"), Command(1, "one", "one")])).Kind);
    }

    [TestMethod]
    public void Retrieve_ContextualMaintenanceRecordDoesNotReplaceOrdinaryTarget()
    {
        PreviousCommandResult result = PreviousCommandRetriever.Retrieve(State([
            Command(1, "git status", "output"),
            Command(2, "tt last", "translation", contextual: true),
        ]));
        Assert.AreEqual(PreviousCommandResultKind.Success, result.Kind);
        Assert.AreEqual(1L, result.Snapshot!.Sequence);
    }

    [TestMethod]
    public void Retrieve_PreservesOrderedNativeStreamsAndPowerShellErrorSourceContext()
    {
        const string native = "The native command completed successfully.\nThe native command reported a recoverable warning.";
        PreviousCommandResult nativeResult = PreviousCommandRetriever.Retrieve(State([Command(1, "native-probe", native)]));
        Assert.AreEqual(native, nativeResult.Snapshot!.Output);

        const string error = "Get-Item : Cannot find drive.\n+ Get-Item \"Z:\\TT_DEFINITELY_MISSING_002\"\n    + CategoryInfo : ObjectNotFound";
        PreviousCommandResult errorResult = PreviousCommandRetriever.Retrieve(State([Command(1, "Get-Item \"Z:\\TT_DEFINITELY_MISSING_002\"", error)]));
        Assert.AreEqual(error, errorResult.Snapshot!.Output);
    }

    [TestMethod]
    public void Retrieve_StrictIntegrityMatrixNeverFallsBack()
    {
        CapturedCommand older = Command(1, "git status", "older useful");
        CapturedCommand newest = Command(2, "cd ..", string.Empty);
        Assert.AreEqual(PreviousCommandResultKind.NoOutput, PreviousCommandRetriever.Retrieve(State([older, newest])).Kind);

        foreach (PreviousCommandRetrievalState corrupt in new[]
        {
            State([older, newest]) with { ReferencesComplete = false },
            State([older, newest]) with { HashesMatch = false },
            State([older, newest]) with { CommittedGenerationReliable = false },
            State([older, newest]) with { LatestFinalizationSucceeded = false },
            State([older, newest]) with { FormatVersion = 99 },
        })
        {
            Assert.AreEqual(PreviousCommandResultKind.UnreliableOrCorrupt, PreviousCommandRetriever.Retrieve(corrupt).Kind);
        }
    }

    private static PreviousCommandRetrievalState State(
        IReadOnlyList<CapturedCommand> commands,
        CapturePreference preference = CapturePreference.Enabled,
        CaptureHealthState health = CaptureHealthState.Healthy,
        bool storeReliable = true) => new(preference, health, Session, commands, storeReliable);

    private static CapturedCommand Command(
        long sequence,
        string command,
        string output,
        CaptureSessionId? session = null,
        bool contextual = false)
    {
        CaptureSessionId actualSession = session ?? Session;
        CommandBoundary boundary = new(actualSession, sequence, command, true, 0, false);
        return new CapturedCommand(
            actualSession,
            sequence,
            command,
            output,
            boundary,
            LocalCaptureCompleteness.Complete,
            System.Text.Encoding.UTF8.GetByteCount(output),
            contextual);
    }
}
