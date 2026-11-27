using System.IO;
using System.Text.Json;

namespace TeamsCaptionsSaver.Desktop.Mom;

/// <summary>
/// One point of the user's feedback, sorted by the agent: a speaker shown under the wrong name, a
/// misheard term, a lasting rule for the project, or something about this meeting only.
/// </summary>
public sealed class Proposal
{
    public const string Speaker = "speaker", Term = "term", Lesson = "lesson", Meeting = "meeting";

    public string Type { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Text { get; init; } = "";

    /// <summary>Ticked to be remembered (bound to the checkbox).</summary>
    public bool IsSelected { get; set; } = true;

    /// <summary>Only these can be remembered; "this meeting only" points are already in the revised minutes.</summary>
    public bool CanRemember => Type is Speaker or Term or Lesson && Description.Length > 0;

    public string Description => Type switch
    {
        Speaker when From.Length > 0 && To.Length > 0 => $"Speaker: “{From}” is {To}",
        Term when From.Length > 0 && To.Length > 0 => $"Term: “{From}” → “{To}”",
        Lesson when Text.Length > 0 => $"Rule: {Text}",
        Meeting => $"This meeting only: {Text}",
        _ => "",
    };

    /// <summary>
    /// Reads the agent's proposals file: {"proposals": [{"type": "...", "from": "...", "to": "...", "text": "..."}]}.
    /// Text around the JSON (a code fence, say) is ignored. Returns an empty list if it can't be read.
    /// </summary>
    public static List<Proposal> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var text = File.ReadAllText(path);
            int start = text.IndexOf('{'), end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return [];
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("proposals", out var list) || list.ValueKind != JsonValueKind.Array) return [];
            return list.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(p => new Proposal
            {
                Type = Str(p, "type").ToLowerInvariant(),
                From = Str(p, "from"),
                To = Str(p, "to"),
                Text = Str(p, "text"),
            }).Where(p => p.Description.Length > 0).ToList();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read the proposals in {path}: {ex.Message}");
            return [];
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
    }
}
