namespace TerminalTranslator.Windows.Tests.TestDoubles;

internal sealed class FaultSwitch
{
    public bool ShouldFail { get; set; }

    public void ThrowIfEnabled(string operation)
    {
        if (ShouldFail)
        {
            throw new IOException($"Injected failure: {operation}");
        }
    }
}

internal sealed record FakeProcessIdentity(int ProcessId, long StartTimeUtcTicks, string UserSid, bool IsAlive = true);

internal sealed class RecordingPromptHost
{
    public List<string> Events { get; } = [];

    public string InvokeOriginalPrompt()
    {
        Events.Add("original-prompt");
        return "CUSTOM> ";
    }
}
