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

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ChatCompletionRequestDto))]
[JsonSerializable(typeof(ChatCompletionResponseDto))]
internal partial class ProviderJsonContext : JsonSerializerContext;
