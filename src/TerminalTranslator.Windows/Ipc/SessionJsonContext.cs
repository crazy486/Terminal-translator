using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerminalTranslator.Windows.Ipc;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(HelloAckMessage))]
[JsonSerializable(typeof(TranslationEventMessage))]
[JsonSerializable(typeof(StateEventMessage))]
[JsonSerializable(typeof(StatusEventMessage))]
[JsonSerializable(typeof(ControlRequestMessage))]
[JsonSerializable(typeof(ControlResultMessage))]
[JsonSerializable(typeof(StatusResultMessage))]
public partial class SessionJsonContext : JsonSerializerContext;
