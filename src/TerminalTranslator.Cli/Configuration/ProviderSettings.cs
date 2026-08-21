using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TerminalTranslator.Cli.Configuration;

public sealed partial record ProviderSettings(
    string Adapter,
    Uri Endpoint,
    string Model,
    string ApiKeyEnvironmentVariable,
    TimeSpan RequestTimeout,
    string SourceLanguage,
    string TargetLanguage,
    bool? RequestTimeoutIsDefault = null)
{
    public const string ChatCompletionAdapter = "chat-completion-http";
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan LegacyDefaultRequestTimeout = TimeSpan.FromMilliseconds(1500);

    public string Fingerprint
    {
        get
        {
            string value = $"{Adapter}\n{Endpoint.AbsoluteUri}\n{Model}\n{TargetLanguage}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
    }

    public static ProviderSettings Create(
        Uri endpoint,
        string model,
        string apiKeyEnvironmentVariable,
        TimeSpan requestTimeout,
        bool allowLoopbackHttp = false,
        bool? requestTimeoutIsDefault = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        bool validScheme = endpoint.Scheme == Uri.UriSchemeHttps ||
            (allowLoopbackHttp && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback);
        if (!endpoint.IsAbsoluteUri || !validScheme || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("Endpoint must be an absolute HTTPS URI without user information.", nameof(endpoint));
        }

        string normalizedModel = (model ?? string.Empty).Trim();
        if (normalizedModel.Length is 0 or > 200)
        {
            throw new ArgumentException("Model must contain between 1 and 200 characters.", nameof(model));
        }

        string normalizedEnvironmentVariable = (apiKeyEnvironmentVariable ?? string.Empty).Trim();
        if (!EnvironmentVariableRegex().IsMatch(normalizedEnvironmentVariable))
        {
            throw new ArgumentException("API key environment variable name is invalid.", nameof(apiKeyEnvironmentVariable));
        }

        if (requestTimeout < TimeSpan.FromMilliseconds(100) || requestTimeout > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "Timeout must be between 100 and 10000 milliseconds.");
        }

        return new ProviderSettings(
            ChatCompletionAdapter,
            endpoint,
            normalizedModel,
            normalizedEnvironmentVariable,
            requestTimeout,
            "en",
            "zh-Hans",
            requestTimeoutIsDefault);
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariableRegex();
}
