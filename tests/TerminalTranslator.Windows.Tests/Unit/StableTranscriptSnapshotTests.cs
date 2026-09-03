using System.Security.Cryptography;
using System.Text;
using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.Tests.Unit;

[TestClass]
public sealed class StableTranscriptSnapshotTests
{
    [TestMethod]
    public async Task AcquireAsync_ReturnsExclusiveBytesLengthAndHash()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "staging.txt");
        byte[] expected = "stable transcript"u8.ToArray();
        await File.WriteAllBytesAsync(path, expected);

        StableTranscriptSnapshotResult result = await StableTranscriptSnapshot.AcquireAsync(
            path,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10));

        Assert.IsTrue(result.IsStable);
        CollectionAssert.AreEqual(expected, result.Content!);
        Assert.AreEqual(expected.Length, result.Length);
        CollectionAssert.AreEqual(SHA256.HashData(expected), result.Sha256!);
        Assert.IsTrue(await result.MatchesFileAsync(path));
    }

    [TestMethod]
    public async Task AcquireAsync_TimesOutWhenExclusiveReadIsUnavailable()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "staging.txt");
        await File.WriteAllTextAsync(path, "locked");
        await using FileStream held = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        StableTranscriptSnapshotResult result = await StableTranscriptSnapshot.AcquireAsync(
            path,
            TimeSpan.FromMilliseconds(75),
            TimeSpan.FromMilliseconds(10));

        Assert.IsFalse(result.IsStable);
        Assert.IsNull(result.Content);
    }

    [TestMethod]
    public async Task Evidence_DetectsMutationAfterSnapshot()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "staging.txt");
        await File.WriteAllTextAsync(path, "before");
        StableTranscriptSnapshotResult result = await StableTranscriptSnapshot.AcquireAsync(path, TimeSpan.FromSeconds(1));
        await File.WriteAllTextAsync(path, "after mutation");
        Assert.IsFalse(await result.MatchesFileAsync(path));
    }

    [TestMethod]
    public async Task AcquireAsync_DoesNotFinalizeTwoIdenticalReadsBeforeProducerCompletionEvidence()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "staging.txt");
        await File.WriteAllTextAsync(path, "command echo\r\n");
        int completionChecks = 0;
        bool producerCompleted = false;
        Task<StableTranscriptSnapshotResult> acquisition = StableTranscriptSnapshot.AcquireAsync(
            path,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            () =>
            {
                Interlocked.Increment(ref completionChecks);
                return Volatile.Read(ref producerCompleted);
            });

        while (Volatile.Read(ref completionChecks) < 2)
            await Task.Yield();
        Assert.IsFalse(acquisition.IsCompleted, "Two unchanged observations cannot replace producer-completion evidence.");

        await File.AppendAllTextAsync(path, "failed native stdout\r\n");
        Volatile.Write(ref producerCompleted, true);
        StableTranscriptSnapshotResult result = await acquisition;

        Assert.IsTrue(result.IsStable);
        Assert.AreEqual("command echo\r\nfailed native stdout\r\n", Encoding.UTF8.GetString(result.Content!));
        Assert.IsTrue(await result.MatchesFileAsync(path));
    }

    [TestMethod]
    public async Task AcquireAsync_MissingOrInaccessiblePathFailsClosed()
    {
        StableTranscriptSnapshotResult result = await StableTranscriptSnapshot.AcquireAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.txt"),
            TimeSpan.FromMilliseconds(20));
        Assert.IsFalse(result.IsStable);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-stable-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
