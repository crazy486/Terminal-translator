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
    public CandidateClassification Classify(
        string text,
        LayoutHints layout,
        SourceBoundary boundary,
        bool programOutputOwnershipEstablished = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normalized = WhitespaceRegex().Replace(text.Trim(), " ");
        TranslationPriority priority = IsHighPriority(normalized)
            ? TranslationPriority.High
            : TranslationPriority.Normal;

        bool eligible = IsUsefulEnglish(normalized, programOutputOwnershipEstablished);
        return new CandidateClassification(eligible, priority, normalized.ToUpperInvariant(), layout);
    }

    private static bool IsUsefulEnglish(string text, bool programOutputOwnershipEstablished)
    {
        if (text.Length < 4 || PathRegex().IsMatch(text) || VersionRegex().IsMatch(text))
        {
            return false;
        }

        int asciiLetters = text.Count(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        int cjk = text.Count(character => character is >= '\u4E00' and <= '\u9FFF');
        if (asciiLetters < 4 || (cjk > 0 && cjk * 2 >= asciiLetters))
        {
            return false;
        }

        string[] words = WordRegex().Matches(text).Select(match => match.Value).ToArray();
        if (words.Length < 2 && !text.Contains('?'))
        {
            return false;
        }

        string trimmed = text.TrimStart();
        bool commandLike = programOutputOwnershipEstablished
            ? CompleteCommandRegex().IsMatch(trimmed)
            : CommandRegex().IsMatch(trimmed);
        if (CodeRegex().IsMatch(trimmed) ||
            (commandLike && !PowerShellErrorRecordRegex().IsMatch(trimmed)))
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

    [GeneratedRegex(
        @"^(?:>\s*)*(?:(?:git|npm|npx|dotnet|python|pip|pwsh|powershell|tt|cd|dir|ls)\s+(?:""[^""]*""|'[^']*'|[-\w./\\]+)(?:\s+(?:""[^""]*""|'[^']*'|[-\w./\\]+))*|[A-Za-z]+-[A-Za-z]+(?:\s+(?:""[^""]*""|'[^']*'|[-\w./\\]+))*)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompleteCommandRegex();

    [GeneratedRegex(@"(?:CategoryInfo|FullyQualifiedErrorId)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellErrorRecordRegex();

    [GeneratedRegex(@"\b(?:error|failed|failure|unable|denied|warning|continue|confirm|proceed)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HighPriorityRegex();
}
