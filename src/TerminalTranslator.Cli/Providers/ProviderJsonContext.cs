using System.Text.Json.Serialization;
using System.Text.Json;

namespace TerminalTranslator.Cli.Providers;

public sealed record ChatCompletionMessageDto(string Role, string Content);

public sealed record ChatCompletionRequestDto(
    string Model,
    IReadOnlyList<ChatCompletionMessageDto> Messages,
    double Temperature);

public sealed record ChatCompletionResponseDto(
    string? Id,
    IReadOnlyList<ChatCompletionChoiceDto>? Choices);

public sealed record ChatCompletionChoiceDto(ChatCompletionResponseMessageDto? Message);

public sealed record ChatCompletionResponseMessageDto(string? Content);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ChatCompletionRequestDto))]
[JsonSerializable(typeof(ChatCompletionResponseDto))]
public partial class ProviderJsonContext : JsonSerializerContext;
