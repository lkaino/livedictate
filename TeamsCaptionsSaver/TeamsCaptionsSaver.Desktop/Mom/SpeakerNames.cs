using System.Text.RegularExpressions;

namespace TeamsCaptionsSaver.Desktop.Mom;

/// <summary>Tells real speaker names from the placeholders Teams sometimes shows instead.</summary>
public static partial class SpeakerNames
{
    [GeneratedRegex(@"^(speaker|puhuja|talare|participant|osallistuja)\s*#?\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex Numbered();

    private static readonly string[] Placeholders =
        ["Unknown", "Unknown speaker", "Guest", "Tuntematon", "Tuntematon puhuja", "Vieras", "Okänd", "Gäst"];

    /// <summary>
    /// True for "Speaker 1", "Unknown", "Guest" and the like, and for names the user asked to always
    /// be asked about (a meeting room's account, for example).
    /// </summary>
    public static bool IsGeneric(string name, IEnumerable<string> alwaysAsk)
    {
        name = name.Trim();
        return name.Length == 0 || Numbered().IsMatch(name) ||
               Placeholders.Contains(name, StringComparer.OrdinalIgnoreCase) ||
               alwaysAsk.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
