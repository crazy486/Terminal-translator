using System.Text.Json.Serialization;
using System.Text.Json;

namespace TerminalTranslator.Cli.Providers;

internal sealed record ChatCompletionMessageDto(string Role, string Content);

internal sealed record ChatCompletionThinkingDto(string Type);

internal sealed record ChatCompletionResponseFormatDto(string Type);

internal sealed record ChatCompletionRequestDto(
    string Model,
    IReadOnlyList<ChatCompletionMessageDto> Messages,
    double Temperature,
    ChatCompletionThinkingDto Thinking,
    [property: JsonPropertyName("response_format")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ChatCompletionResponseFormatDto? ResponseFormat);

internal sealed record ChatCompletionResponseDto(
    string? Id,
    IReadOnlyList<ChatCompletionChoiceDto>? Choices);

internal sealed record ChatCompletionChoiceDto(
    ChatCompletionResponseMessageDto? Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record ChatCompletionResponseMessageDto(
    string? Content,
    [property: JsonPropertyName("reasoning_content")] string? ReasoningContent);

internal sealed record AssistanceResponseDto(string? Translation, string? Recommendation);

internal sealed record AssistanceAnswerResponseDto(string? Answer);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ChatCompletionRequestDto))]
[JsonSerializable(typeof(ChatCompletionResponseDto))]
[JsonSerializable(typeof(AssistanceResponseDto))]
[JsonSerializable(typeof(AssistanceAnswerResponseDto))]
internal partial class ProviderJsonContext : JsonSerializerContext;
