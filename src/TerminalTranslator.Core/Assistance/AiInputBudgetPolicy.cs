namespace TerminalTranslator.Core.Assistance;

public sealed record AiInputBudgetPolicy
{
    public const int V1DefaultBytes = 8 * 1024;
    public static AiInputBudgetPolicy V1Default { get; } = new(V1DefaultBytes);

    public AiInputBudgetPolicy(int variableInputBytes)
    {
        if (variableInputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(variableInputBytes));
        VariableInputBytes = variableInputBytes;
    }

    public int VariableInputBytes { get; }
    public int EffectiveBytes(int? adapterCapabilityBytes) =>
        adapterCapabilityBytes is > 0 ? Math.Min(VariableInputBytes, adapterCapabilityBytes.Value) : VariableInputBytes;
}
