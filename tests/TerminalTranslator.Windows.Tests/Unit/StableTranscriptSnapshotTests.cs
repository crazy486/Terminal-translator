using System.Security.Cryptography;
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
