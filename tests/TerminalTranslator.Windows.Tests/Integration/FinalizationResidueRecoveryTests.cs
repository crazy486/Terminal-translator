using TerminalTranslator.Windows.Capture;

namespace TerminalTranslator.Windows.Tests.Integration;

[TestClass]
public sealed class FinalizationResidueRecoveryTests
{
    [TestMethod]
    public async Task ResidueSet_BlocksNewCaptureAndCannotAccumulateSecondSet()
    {
        using TemporaryDirectory temporary = new();
        string first = Path.Combine(temporary.Path, "staging-one.txt");
        string second = Path.Combine(temporary.Path, "candidate-two.txt");
        await File.WriteAllTextAsync(first, "content");
        await File.WriteAllTextAsync(second, "content");
        bool failFirst = true;
        FinalizationResidueManager manager = new(temporary.Path, path =>
        {
            if (path == first && failFirst)
            {
                return false;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        });

        Assert.IsTrue(await manager.TryRegisterAsync([first, second]));
        Assert.IsFalse(manager.CanStartCapture);
        Assert.IsFalse(await manager.TryRegisterAsync([Path.Combine(temporary.Path, "third.txt")]));
        Assert.IsFalse(await manager.TryRecoverAsync());
        Assert.IsFalse(manager.CanStartCapture);

        failFirst = false;
        Assert.IsTrue(await manager.TryRecoverAsync());
        Assert.IsTrue(manager.CanStartCapture);
        Assert.IsFalse(File.Exists(first));
        Assert.IsFalse(File.Exists(second));
    }

    [TestMethod]
    public async Task Registration_RejectsPathsOutsideExactSessionDirectory()
    {
        using TemporaryDirectory temporary = new();
        FinalizationResidueManager manager = new(temporary.Path);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            manager.TryRegisterAsync([Path.Combine(Path.GetTempPath(), "outside.txt")]));
        Assert.IsTrue(manager.CanStartCapture);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tt-residue-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
