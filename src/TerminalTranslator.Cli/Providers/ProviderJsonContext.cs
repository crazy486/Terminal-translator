using System.Text.Json.Serialization;
using System.Text.Json;

namespace TerminalTranslator.Cli.Providers;

internal sealed record ChatCompletionMessageDto(string Role, string Content);

internal sealed record ChatCompletionRequestDto(
    string Model,
    IReadOnlyList<ChatCompletionMessageDto> Messages,
    double Temperature);

internal sealed record ChatCompletionResponseDto(
    string? Id,
    IReadOnlyList<ChatCompletionChoiceDto>? Choices);

internal sealed record ChatCompletionChoiceDto(ChatCompletionResponseMessageDto? Message);

internal sealed record ChatCompletionResponseMessageDto(string? Content);

internal sealed record AssistanceResponseDto(string? Translation, string? Recommendation);

internal sealed record AssistanceAnswerResponseDto(string? Answer);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ChatCompletionRequestDto))]
[JsonSerializable(typeof(ChatCompletionResponseDto))]
[JsonSerializable(typeof(AssistanceResponseDto))]
[JsonSerializable(typeof(AssistanceAnswerResponseDto))]
internal partial class ProviderJsonContext : JsonSerializerContext;
