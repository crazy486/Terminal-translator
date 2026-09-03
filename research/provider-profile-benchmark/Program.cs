using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const string SystemPrompt =
    "Translate terminal output to Simplified Chinese and return JSON with translation and recommendation fields. " +
    "Use the command only as context. Do not translate the command text. Translate English natural language, " +
    "preserve existing Chinese, and preserve technical tokens: paths, URLs, flags/options, error codes, identifiers, " +
    "package/module names, and code fragments. Give one short recommendation of roughly 1-2 lines. " +
    "Shell commands are text only; never execute anything.";

int count = ReadIntArgument(args, "--count", 5);
int timeoutSeconds = ReadIntArgument(args, "--timeout-seconds", 10);
string settingsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "TerminalTranslator",
    "provider-settings.json");

using JsonDocument settingsDocument = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
JsonElement settings = settingsDocument.RootElement;
Uri endpoint = new(settings.GetProperty("endpoint").GetString()!);
string model = settings.GetProperty("model").GetString()!;
string credentialVariable = settings.GetProperty("apiKeyEnvironmentVariable").GetString()!;
string credential = Environment.GetEnvironmentVariable(credentialVariable)
    ?? throw new InvalidOperationException($"Credential environment variable {credentialVariable} is not set.");

string shortOutput = "The package installation completed successfully.";
string mediumOutput = BuildMediumOutput(900);
Dictionary<string, string> inputs = new()
{
    ["short"] = shortOutput,
    ["medium"] = mediumOutput,
};
string[] profiles = ReadStringArgument(args, "--profiles")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? ["A-current", "B-non-thinking", "C-minimal"];
List<RunResult> results = [];

using HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
{
    Timeout = Timeout.InfiniteTimeSpan,
};

Console.WriteLine(JsonSerializer.Serialize(new
{
    kind = "benchmark-start",
    endpoint = new UriBuilder(endpoint) { Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri,
    model,
    countPerCell = count,
    timeoutSeconds,
    systemPromptChars = SystemPrompt.Length,
    inputs = inputs.ToDictionary(pair => pair.Key, pair => new
    {
        outputUtf8Bytes = Encoding.UTF8.GetByteCount(pair.Value),
        userPromptChars = BuildUserPrompt(pair.Value).Length,
        userPromptUtf8Bytes = Encoding.UTF8.GetByteCount(BuildUserPrompt(pair.Value)),
    }),
}));

for (int iteration = 0; iteration < count; iteration++)
{
    string[] inputOrder = iteration % 2 == 0 ? ["short", "medium"] : ["medium", "short"];
    string[] profileOrder = profiles.Skip(iteration % profiles.Length).Concat(profiles.Take(iteration % profiles.Length)).ToArray();
    foreach (string inputName in inputOrder)
    {
        foreach (string profile in profileOrder)
        {
            RunResult result = await RunAsync(client, endpoint, model, credential, profile, inputName, inputs[inputName], timeoutSeconds);
            results.Add(result);
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
    }
}

foreach (IGrouping<(string Profile, string Input), RunResult> cell in results.GroupBy(result => (result.Profile, result.Input)))
{
    long[] completed = cell.Where(result => result.CompletedMilliseconds is not null)
        .Select(result => result.CompletedMilliseconds!.Value).Order().ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        kind = "summary",
        profile = cell.Key.Profile,
        input = cell.Key.Input,
        requests = cell.Count(),
        success = cell.Count(result => result.Outcome == "success"),
        timeout = cell.Count(result => result.Outcome == "timeout"),
        malformed = cell.Count(result => result.Outcome.StartsWith("malformed-", StringComparison.Ordinal)),
        medianMilliseconds = Percentile(completed, 0.5),
        p90Milliseconds = Percentile(completed, 0.9),
        maxMilliseconds = completed.Length == 0 ? (long?)null : completed[^1],
    }));
}

static async Task<RunResult> RunAsync(
    HttpClient client,
    Uri endpoint,
    string model,
    string credential,
    string profile,
    string inputName,
    string output,
    int timeoutSeconds)
{
    JsonObject requestObject = new()
    {
        ["model"] = model,
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = BuildUserPrompt(output) }),
        ["temperature"] = 0,
    };
    if (profile is "B-non-thinking" or "C-minimal" or "E-non-thinking-json")
        requestObject["thinking"] = new JsonObject { ["type"] = "disabled" };
    if (profile is "C-minimal" or "D-current-json" or "E-non-thinking-json")
        requestObject["response_format"] = new JsonObject { ["type"] = "json_object" };
    if (profile == "C-minimal")
    {
        requestObject["max_tokens"] = 1024;
    }

    byte[] requestBytes = Encoding.UTF8.GetBytes(requestObject.ToJsonString());
    using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
    {
        Content = new ByteArrayContent(requestBytes),
    };
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(timeoutSeconds));
    Stopwatch timer = Stopwatch.StartNew();
    long? headersMilliseconds = null;
    int? status = null;
    try
    {
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        headersMilliseconds = timer.ElapsedMilliseconds;
        status = (int)response.StatusCode;
        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        long completedMilliseconds = timer.ElapsedMilliseconds;
        if (!response.IsSuccessStatusCode)
            return Result("http-status", responseBytes.Length);

        try
        {
            using JsonDocument envelope = JsonDocument.Parse(responseBytes);
            JsonElement root = envelope.RootElement;
            JsonElement choice = root.GetProperty("choices")[0];
            string? finishReason = ReadString(choice, "finish_reason");
            JsonElement message = choice.GetProperty("message");
            string? content = ReadString(message, "content");
            string? reasoning = ReadString(message, "reasoning_content");
            (int? promptTokens, int? completionTokens, int? reasoningTokens) = ReadUsage(root);
            string outcome;
            if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)) outcome = "malformed-finish-length";
            else if (content is null) outcome = "malformed-missing-content";
            else if (string.IsNullOrWhiteSpace(content)) outcome = "malformed-empty-content";
            else
            {
                try
                {
                    using JsonDocument inner = JsonDocument.Parse(content);
                    JsonElement innerRoot = inner.RootElement;
                    bool validSchema = innerRoot.TryGetProperty("translation", out JsonElement translation) &&
                        !string.IsNullOrWhiteSpace(translation.GetString()) &&
                        innerRoot.TryGetProperty("recommendation", out JsonElement recommendation) &&
                        !string.IsNullOrWhiteSpace(recommendation.GetString());
                    string translationText = validSchema ? translation.GetString()! : string.Empty;
                    bool qualitySignals = validSchema && translationText.Any(character => character is >= '\u4e00' and <= '\u9fff') &&
                        (inputName != "medium" ||
                         (translationText.Contains("Contoso.Core", StringComparison.Ordinal) &&
                          translationText.Contains("1603", StringComparison.Ordinal)));
                    outcome = !validSchema ? "malformed-schema" : qualitySignals ? "success" : "quality-failed";
                }
                catch (JsonException)
                {
                    outcome = "malformed-inner-json";
                }
            }

            return new RunResult(
                "run", profile, inputName, requestBytes.Length, status, headersMilliseconds,
                completedMilliseconds, outcome, responseBytes.Length, finishReason,
                content is not null, content?.Length, !string.IsNullOrEmpty(reasoning), reasoning?.Length,
                promptTokens, completionTokens, reasoningTokens);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return Result("malformed-outer-json", responseBytes.Length);
        }

        RunResult Result(string outcome, int responseByteCount) => new(
            "run", profile, inputName, requestBytes.Length, status, headersMilliseconds,
            completedMilliseconds, outcome, responseByteCount, null, null, null, null, null, null, null, null);
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
        return new(
            "run", profile, inputName, requestBytes.Length, status, headersMilliseconds,
            timer.ElapsedMilliseconds, "timeout", null, null, null, null, null, null, null, null, null);
    }
    catch (HttpRequestException exception)
    {
        return new(
            "run", profile, inputName, requestBytes.Length, status, headersMilliseconds,
            timer.ElapsedMilliseconds, $"network-{exception.GetType().Name}", null, null, null, null, null, null, null, null, null);
    }
}

static string BuildUserPrompt(string output) =>
    "Command context (do not translate):\nwinget install Contoso.Tools\n\n" +
    "Termination facts:\npowerShellSucceeded=unknown; nativeExitCode=0; interrupted=false\n\n" +
    $"Selected ordered output:\n{output}\n\nInput completeness: Complete; local completeness: Complete.";

static string BuildMediumOutput(int targetBytes)
{
    const string seed =
        "Install-Package : Package installation failed because dependency Contoso.Core could not be resolved.\n" +
        "At line:1 char:1\n+ Install-Package Contoso.Tools -RequiredVersion 4.2.0\n" +
        "+ CategoryInfo : NotSpecified: (:) [Install-Package], InvalidOperationException\n" +
        "+ FullyQualifiedErrorId : NuGetCmdletUnhandledException,NuGet.PackageManagement.PowerShellCmdlets.InstallPackageCommand\n" +
        "NativeCommandExitCode: 1603\n";
    StringBuilder builder = new(seed);
    while (Encoding.UTF8.GetByteCount(builder.ToString()) < targetBytes)
        builder.Append("Diagnostic detail: repository metadata was checked but no compatible package source responded.\n");
    string value = builder.ToString();
    return value[..targetBytes];
}

static string? ReadString(JsonElement parent, string property) =>
    parent.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

static (int? Prompt, int? Completion, int? Reasoning) ReadUsage(JsonElement root)
{
    if (!root.TryGetProperty("usage", out JsonElement usage)) return (null, null, null);
    int? prompt = ReadInt(usage, "prompt_tokens");
    int? completion = ReadInt(usage, "completion_tokens");
    int? reasoning = usage.TryGetProperty("completion_tokens_details", out JsonElement details)
        ? ReadInt(details, "reasoning_tokens")
        : ReadInt(usage, "reasoning_tokens");
    return (prompt, completion, reasoning);
}

static int? ReadInt(JsonElement parent, string property) =>
    parent.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int parsed) ? parsed : null;

static int ReadIntArgument(string[] arguments, string name, int fallback)
{
    int index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length && int.TryParse(arguments[index + 1], out int value) && value > 0
        ? value
        : fallback;
}

static string? ReadStringArgument(string[] arguments, string name)
{
    int index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static long? Percentile(long[] values, double percentile) =>
    values.Length == 0 ? null : values[Math.Max(0, (int)Math.Ceiling(percentile * values.Length) - 1)];

internal sealed record RunResult(
    string Kind,
    string Profile,
    string Input,
    int RequestBytes,
    int? HttpStatus,
    long? HeadersMilliseconds,
    long? CompletedMilliseconds,
    string Outcome,
    int? ResponseBytes,
    string? FinishReason,
    bool? ContentPresent,
    int? ContentChars,
    bool? ReasoningContentPresent,
    int? ReasoningContentChars,
    int? PromptTokens,
    int? CompletionTokens,
    int? ReasoningTokens);
