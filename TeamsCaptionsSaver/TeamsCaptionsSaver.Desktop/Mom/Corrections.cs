using System.Text.RegularExpressions;

namespace TeamsCaptionsSaver.Desktop.Mom;

/// <summary>A misheard or misspelled word or phrase and what it should be.</summary>
public sealed record TermFix(string From, string To);

public static class Corrections
{
    /// <summary>
    /// Replaces each <see cref="TermFix.From"/> as a whole word or phrase, ignoring case:
    /// "café" -> "Kafka" fixes "Café" but leaves "cafés" alone.
    /// </summary>
    public static string ReplaceTerms(string text, IEnumerable<TermFix> fixes)
    {
        foreach (var fix in fixes)
        {
            if (string.IsNullOrWhiteSpace(fix.From)) continue;
            text = Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(fix.From.Trim())}(?![\p{{L}}\p{{N}}])",
                fix.To.Replace("$", "$$"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return text;
    }
}
