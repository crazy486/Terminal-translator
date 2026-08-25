using TerminalTranslator.Core.Assistance;

namespace TerminalTranslator.Core.Tests.Unit;

[TestClass]
public sealed class AiInputBudgetPolicyTests
{
    [TestMethod]
    public void DefaultIsAdjustable8192Utf8BytesAndSmallerAdapterWins()
    {
        Assert.AreEqual(8192, AiInputBudgetPolicy.V1Default.VariableInputBytes);
        Assert.AreEqual(8192, AiInputBudgetPolicy.V1Default.EffectiveBytes(null));
        Assert.AreEqual(4096, AiInputBudgetPolicy.V1Default.EffectiveBytes(4096));
        Assert.AreEqual(8192, AiInputBudgetPolicy.V1Default.EffectiveBytes(16384));
        Assert.AreEqual(128, new AiInputBudgetPolicy(128).VariableInputBytes);
    }
}
