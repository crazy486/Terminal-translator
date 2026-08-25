using System.Text.RegularExpressions;

namespace TerminalTranslator.Core.Assistance;

public static partial class ResponseLanguagePolicy
{
    private static readonly ResponseLanguageSelection SimplifiedChinese = new(
        "Simplified Chinese",
        "Answer in Simplified Chinese unless the user explicitly requests another language.");

    public static ResponseLanguageSelection Select(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question is required.", nameof(question));

        if (EnglishRequest().IsMatch(question))
            return new("English", "Answer in English as explicitly requested by the user.");
        if (JapaneseRequest().IsMatch(question))
            return new("Japanese", "Answer in Japanese as explicitly requested by the user.");
        return SimplifiedChinese;
    }

    [GeneratedRegex(@"(?ix)(?:\b(?:please\s+)?(?:answer|respond|explain)(?:\s+(?:this(?:\s+question)?|it|the\s+question))?\s+in\s+english\b|请用(?:英文|英语)(?:回答|解释)|用(?:英文|英语)(?:回答|解释))")]
    private static partial Regex EnglishRequest();

    [GeneratedRegex(@"(?ix)(?:\b(?:please\s+)?(?:answer|respond|explain)(?:\s+(?:this(?:\s+question)?|it|the\s+question))?\s+in\s+japanese\b|请用(?:日文|日语)(?:回答|解释)|用(?:日文|日语)(?:回答|解释))")]
    private static partial Regex JapaneseRequest();
}
