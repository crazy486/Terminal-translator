using System.Text.RegularExpressions;
using TerminalTranslator.Core.Models;

namespace TerminalTranslator.Core.Parsing;

public sealed record CandidateClassification(
    bool IsEligible,
    TranslationPriority Priority,
    string DeduplicationKey,
    LayoutHints Layout);

public sealed partial class EnglishCandidateClassifier
{
    public CandidateClassification Classify(string text, LayoutHints layout, SourceBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normalized = WhitespaceRegex().Replace(text.Trim(), " ");
        TranslationPriority priority = IsHighPriority(normalized)
            ? TranslationPriority.High
            : TranslationPriority.Normal;

        bool eligible = IsUsefulEnglish(normalized);
        return new CandidateClassification(eligible, priority, normalized.ToUpperInvariant(), layout);
    }

    private static bool IsUsefulEnglish(string text)
    {
        if (text.Length < 4 || PathRegex().IsMatch(text) || VersionRegex().IsMatch(text) ||
            PowerShellPromptRegex().IsMatch(text) || TerminalTranslatorStatusRegex().IsMatch(text))
        {
            return false;
        }

        int asciiLetters = text.Count(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        int cjk = text.Count(character => character is >= '?' and <= '?');
        if (asciiLetters < 4 || cjk > asciiLetters)
        {
            return false;
        }

        string[] words = WordRegex().Matches(text).Select(match => match.Value).ToArray();
        if (words.Length < 2 && !text.Contains('?'))
        {
            return false;
        }

        string trimmed = text.TrimStart();
        if (CodeRegex().IsMatch(trimmed) ||
            (CommandRegex().IsMatch(trimmed) && !PowerShellErrorRecordRegex().IsMatch(trimmed)))
        {
            return false;
        }

        return words.Any(word => word.Length >= 3);
    }

    private static bool IsHighPriority(string text) =>
        text.Contains('?') ||
        HighPriorityRegex().IsMatch(text);

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(?:[A-Za-z]:\\|\\\\|/)[^\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"^v?\d+(?:\.\d+){1,4}(?:[-+][\w.-]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"[A-Za-z]+(?:'[A-Za-z]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"^(?:const|var|let|public|private|class|if|for|while|return)\b|[{};].*[=();]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"^(?:>\s*)*(?:(?:git|npm|npx|dotnet|python|pip|pwsh|powershell|tt|cd|dir|ls)\s+[-\w]|[A-Za-z]+-[A-Za-z]+\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandRegex();

    [GeneratedRegex(@"^PS\s+(?:[A-Za-z]:\\|\\\\|/).*>\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellPromptRegex();

    [GeneratedRegex(@"^Translation\s+(?:enabled|disabled)\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TerminalTranslatorStatusRegex();

    [GeneratedRegex(@"(?:CategoryInfo|FullyQualifiedErrorId)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellErrorRecordRegex();

    [GeneratedRegex(@"\b(?:error|failed|failure|unable|denied|warning|continue|confirm|proceed)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HighPriorityRegex();
}
