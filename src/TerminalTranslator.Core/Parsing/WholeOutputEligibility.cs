using System.Text.RegularExpressions;

namespace TerminalTranslator.Core.Parsing;

public static partial class WholeOutputEligibility
{
    private static readonly HashSet<string> NaturalSignalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "because", "cannot", "could", "did", "does",
        "error", "failed", "failure", "found", "missing", "not", "please", "succeeded", "success", "unable",
        "warning", "required", "invalid", "denied", "try", "check", "using", "from", "to", "with", "for",
        "build", "file", "directory", "command", "connection", "timeout", "completed", "oops", "hello",
    };

    public static bool HasTranslatableEnglish(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        if (CodeOnlyRegex().IsMatch(output.Trim())) return false;
        string scrubbed = TechnicalTokenRegex().Replace(output, " ");
        string[] words = WordRegex().Matches(scrubbed).Select(match => match.Value).ToArray();
        if (words.Any(word => NaturalSignalWords.Contains(word))) return true;
        return words.Count(word => word.Length >= 2 && char.IsLower(word[0]) && word.All(char.IsLetter)) >= 2;
    }

    [GeneratedRegex(@"(?ix)(https?://\S+|(?:npm|nuget|pip):\S+|@[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+|(?:[A-Za-z]:\\|/)[^\s]+|--?[A-Za-z0-9][\w:-]*|\b(?:0x[0-9a-f]+|[A-Z][A-Z0-9_]*\d+[A-Z0-9_]*|E_[A-Z0-9_]+)\b|\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\b|\b[A-Za-z]+_[A-Za-z0-9_]+\b|[{}();=<>]+)")]
    private static partial Regex TechnicalTokenRegex();

    [GeneratedRegex(@"\b[A-Za-z]+\b")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"(?is)^\s*(?:if|for|while|switch|return|throw|var|const|public|private)\b.*[;{}]\s*$")]
    private static partial Regex CodeOnlyRegex();
}
