using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TeamsCaptionsSaver.Desktop.Audio;
using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop.Transcript;

/// <summary>
/// Writes a transcript in each supported format. Every method takes an optional list of turns, so the
/// window can export just what its filters show; by default the whole session is exported.
/// </summary>
public static class TranscriptExporter
{
    public static readonly (string Ext, string Label)[] Formats =
    [
        ("txt", "Plain text"),
        ("md", "Markdown"),
        ("json", "JSON"),
        ("csv", "CSV (Excel)"),
        ("vtt", "WebVTT subtitles"),
    ];

    public static string FileDialogFilter =>
        string.Join("|", Formats.Select(f => $"{f.Label} (*.{f.Ext})|*.{f.Ext}"));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Captions show up a moment after the words are spoken, so audio positions point this much earlier.
    /// </summary>
    public static readonly TimeSpan AudioLead = TimeSpan.FromSeconds(2);

    public static string BuildFileName(string pattern, TranscriptSession session)
    {
        var name = CleanName(Fill(pattern, session), 150);
        return name.Length == 0 ? "Teams Transcript" : name;
    }

    /// <summary>
    /// The relative folder for a session, e.g. "{title}/{date}" -> "Weekly sync\2026-11-18". A "/" in
    /// the meeting title doesn't create a folder; empty parts are dropped.
    /// </summary>
    public static string BuildSubfolder(string pattern, TranscriptSession session) =>
        Path.Combine(pattern.Split('/', '\\')
            .Select(part => CleanName(Fill(part, session), 80))
            .Where(part => part.Length > 0).ToArray());

    private static string Fill(string pattern, TranscriptSession session) => pattern
        .Replace("{date}", session.StartedAt.ToString("yyyy-MM-dd"))
        .Replace("{time}", session.StartedAt.ToString("HH-mm"))
        .Replace("{title}", session.Title);

    private static string CleanName(string name, int maxLength)
    {
        foreach (var c in Path.GetInvalidFileNameChars().Append('/').Append('\\')) name = name.Replace(c, '_');
        name = name.Trim(' ', '.', '_');
        return name.Length > maxLength ? name[..maxLength].TrimEnd(' ', '.') : name;
    }

    /// <summary>The session's fixed file name if it has one, otherwise one built from the pattern.</summary>
    public static string BaseNameFor(TranscriptSession session, string pattern) =>
        session.FileBaseName ?? BuildFileName(pattern, session);

    /// <summary>The session's folder: <paramref name="outputFolder"/> plus its fixed or pattern-built subfolder.</summary>
    public static string FolderFor(TranscriptSession session, string outputFolder, string subfolderPattern) =>
        Path.Combine(outputFolder, session.Subfolder ?? BuildSubfolder(subfolderPattern, session));

    /// <summary>
    /// Where something said at <paramref name="time"/> is in the meeting's audio: the file and the
    /// position to start playing (<see cref="AudioLead"/> early). Null when there is no audio for it.
    /// </summary>
    public static (AudioPart Part, TimeSpan Position)? AudioPositionOf(TranscriptSession session, DateTime time)
    {
        var parts = session.AudioParts;
        if (parts.Count == 0 || time < parts[0].Start) return null;
        var at = time - AudioLead;
        var part = parts.LastOrDefault(p => p.Start <= at) ?? parts[0];
        var position = at - part.Start;
        return (part, position < TimeSpan.Zero ? TimeSpan.Zero : position);
    }

    /// <summary>"14-10-00 @ 3:23": the audio file (by the time in its name) and the position in it.</summary>
    public static string? AudioReference(TranscriptSession session, DateTime time)
    {
        if (AudioPositionOf(session, time) is not (var part, var position)) return null;
        return $"{AudioLabel(part)} @ {FormatPosition(position)}";
    }

    /// <summary>The part of the file name that tells the parts apart: "14-10-00" (or "14-10-00_2").</summary>
    private static string AudioLabel(AudioPart part)
    {
        var name = Path.GetFileNameWithoutExtension(part.Path);
        int i = name.LastIndexOf("_audio_", StringComparison.Ordinal);
        return i >= 0 ? name[(i + "_audio_".Length)..] : name;
    }

    private static string FormatPosition(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : $"{(int)t.TotalMinutes}:{t:ss}";

    /// <summary>True when <paramref name="turns"/> leaves out some of the session's turns.</summary>
    public static bool IsFiltered(TranscriptSession s, IReadOnlyList<SpeakerTurn>? turns) =>
        turns != null && turns.Count != s.Turns.Count;

    /// <summary>
    /// Writes the transcript in each format to <paramref name="folder"/>; returns the paths.
    /// A filtered subset gets a "_filtered" suffix so it never overwrites the full transcript.
    /// </summary>
    public static List<string> SaveAll(TranscriptSession session, string folder, string pattern, IEnumerable<string> formats,
        IReadOnlyList<SpeakerTurn>? turns = null)
    {
        Directory.CreateDirectory(folder);
        var baseName = BaseNameFor(session, pattern) + (IsFiltered(session, turns) ? "_filtered" : "");
        var paths = new List<string>();
        foreach (var ext in formats.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, $"{baseName}.{ext}");
            Save(session, path, ext, turns);
            paths.Add(path);
        }
        return paths;
    }

    public static void Save(TranscriptSession session, string path, string? format = null,
        IReadOnlyList<SpeakerTurn>? turns = null)
    {
        format ??= Path.GetExtension(path).TrimStart('.');
        var content = format.ToLowerInvariant() switch
        {
            "md" => ToMarkdown(session, turns),
            "json" => ToJson(session, turns),
            "csv" => ToCsv(session, turns),
            "vtt" => ToVtt(session, turns),
            _ => ToText(session, turns),
        };
        // UTF-8 with BOM so Excel and Notepad pick the right encoding for non-English names.
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static IEnumerable<string> SpeakersOf(IEnumerable<SpeakerTurn> turns) =>
        turns.Select(t => t.Speaker).Distinct();

    private static string Header(TranscriptSession s, IReadOnlyList<SpeakerTurn> turns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Meeting: {s.Title}");
        sb.AppendLine($"Date: {s.StartedAt:dddd, MMMM d, yyyy}");
        sb.AppendLine($"Time: {s.StartedAt:HH:mm}{(s.EndedAt is { } end ? $" - {end:HH:mm}" : "")}");
        sb.AppendLine($"Speakers: {string.Join(", ", SpeakersOf(turns))}");
        if (IsFiltered(s, turns)) sb.AppendLine($"Filtered: {turns.Count} of {s.Turns.Count} turns");
        if (s.AudioParts.Count > 0) sb.AppendLine(AudioNote);
        return sb.ToString();
    }

    private const string AudioNote =
        "Audio: each line ends with 🔊 file @ position - the audio file whose name ends in that time, and " +
        "where to start playing (a couple of seconds before the caption appeared).";

    public static string ToText(TranscriptSession s, IReadOnlyList<SpeakerTurn>? turns = null)
    {
        turns ??= s.Turns;
        var sb = new StringBuilder(Header(s, turns));

        var marked = turns.Where(t => t.IsMarked).ToList();
        if (marked.Count > 0)
        {
            sb.AppendLine().AppendLine("Highlights:");
            foreach (var t in marked)
                sb.AppendLine(t.IsBookmarked
                    ? $"⭐ [{t.TimeDisplay}] {t.Speaker}: {MarkText(t)}"
                    : $"🖍 [{t.TimeDisplay}] {t.Speaker}: {string.Join(" · ", t.Highlights.Select(Quote))}");
            sb.AppendLine().Append("Transcript:").AppendLine();
        }

        foreach (var t in turns)
            sb.AppendLine().AppendLine(FormatTurn(s, t));
        return sb.ToString();
    }

    /// <summary>
    /// One speaker turn as a plain-text line: "[17:50:50] Jonas: what they said", with a ⭐ in front
    /// when starred and [[highlighted words]] marked.
    /// </summary>
    public static string FormatTurn(SpeakerTurn t) =>
        $"{(t.IsBookmarked ? "⭐ " : "")}[{t.TimeDisplay}] {t.Speaker}: {MarkText(t)}";

    /// <summary><see cref="FormatTurn"/> plus where the turn is in the audio, if there is audio.</summary>
    private static string FormatTurn(TranscriptSession s, SpeakerTurn t) =>
        AudioReference(s, t.StartTime) is { } audio ? $"{FormatTurn(t)}   🔊 {audio}" : FormatTurn(t);

    private static string MarkText(SpeakerTurn t) => HighlightRanges.Mark(t.Text, t.Highlights, "[[", "]]");

    private static string Quote(string phrase) => $"“{phrase}”";

    public static string ToMarkdown(TranscriptSession s, IReadOnlyList<SpeakerTurn>? turns = null)
    {
        turns ??= s.Turns;
        var sb = new StringBuilder();
        sb.AppendLine($"# {s.Title}");
        sb.AppendLine();
        sb.AppendLine($"- **Date:** {s.StartedAt:dddd, MMMM d, yyyy}");
        sb.AppendLine($"- **Time:** {s.StartedAt:HH:mm}{(s.EndedAt is { } end ? $" – {end:HH:mm}" : "")}");
        sb.AppendLine($"- **Speakers:** {string.Join(", ", SpeakersOf(turns))}");
        if (IsFiltered(s, turns)) sb.AppendLine($"- **Filtered:** {turns.Count} of {s.Turns.Count} turns");
        if (s.AudioParts.Count > 0)
            sb.AppendLine($"- **Audio:** {string.Join(", ", s.AudioParts.Select(p => $"`{Path.GetFileName(p.Path)}`"))}. " +
                          "🔊 after a speaker gives the file (by the time in its name) and where to start playing.");
        sb.AppendLine();

        var marked = turns.Where(t => t.IsMarked).ToList();
        if (marked.Count > 0)
        {
            sb.AppendLine("## Highlights");
            sb.AppendLine();
            foreach (var t in marked)
                sb.AppendLine(t.IsBookmarked
                    ? $"- ⭐ **{t.Speaker}** _{t.TimeDisplay}_: {MarkMarkdown(t)}"
                    : $"- 🖍 **{t.Speaker}** _{t.TimeDisplay}_: {string.Join(" · ", t.Highlights.Select(Quote))}");
            sb.AppendLine();
        }

        sb.AppendLine("## Transcript");
        sb.AppendLine();

        foreach (var t in turns)
        {
            var audio = AudioReference(s, t.StartTime) is { } a ? $" · 🔊 {a}" : "";
            sb.AppendLine($"**{t.Speaker}** _{t.TimeDisplay}_{audio}{(t.IsBookmarked ? " ⭐" : "")}  ");
            sb.AppendLine(MarkMarkdown(t));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string MarkMarkdown(SpeakerTurn t) => HighlightRanges.Mark(t.Text, t.Highlights, "**", "**");

    public static string ToJson(TranscriptSession s, IReadOnlyList<SpeakerTurn>? turns = null)
    {
        var shown = turns ?? s.Turns;
        return JsonSerializer.Serialize(new
        {
            title = s.Title,
            startedAt = s.StartedAt,
            endedAt = s.EndedAt,
            speakers = SpeakersOf(shown).ToList(),
            audioFiles = s.AudioParts.Select(p => new { file = Path.GetFileName(p.Path), start = p.Start }),
            filtered = IsFiltered(s, turns) ? new { shownTurns = shown.Count, totalTurns = s.Turns.Count } : null,
            turns = shown.Select(t => new
            {
                speaker = t.Speaker,
                originalSpeaker = t.RawSpeaker != t.Speaker ? t.RawSpeaker : null,
                start = t.StartTime,
                end = t.EndTime,
                offsetSeconds = Math.Round((t.StartTime - s.StartedAt).TotalSeconds, 1),
                text = t.Text,
                audio = AudioPositionOf(s, t.StartTime) is (var part, var position)
                    ? new { file = Path.GetFileName(part.Path), positionSeconds = Math.Round(position.TotalSeconds, 1) }
                    : null,
                bookmarked = t.IsBookmarked,
                highlights = t.Highlights,
                // The individual caption blocks, for anyone who needs finer timing.
                captions = t.Entries.Select(e => new { time = e.FirstSeen, text = e.Text }),
            }),
        }, JsonOptions);
    }

    public static string ToCsv(TranscriptSession s, IReadOnlyList<SpeakerTurn>? turns = null)
    {
        static string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Start,End,Speaker,Text,Bookmarked,Highlights,Audio file,Audio position\r\n");
        foreach (var t in turns ?? s.Turns)
        {
            var audio = AudioPositionOf(s, t.StartTime);
            sb.Append(Q(t.StartTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(Q(t.EndTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(Q(t.Speaker)).Append(',').Append(Q(t.Text)).Append(',')
              .Append(t.IsBookmarked ? "yes" : "").Append(',')
              .Append(Q(string.Join("; ", t.Highlights))).Append(',')
              .Append(audio is (var part, _) ? Q(Path.GetFileName(part.Path)) : "").Append(',')
              .Append(audio is (_, var position) ? Q(FormatPosition(position)) : "").Append("\r\n");
        }
        return sb.ToString();
    }

    public static string ToVtt(TranscriptSession s, IReadOnlyList<SpeakerTurn>? turns = null)
    {
        static string Ts(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
        var sb = new StringBuilder("WEBVTT\n\n");
        var entries = turns == null ? s.Entries.ToList() : turns.SelectMany(t => t.Entries).ToList();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var start = e.FirstSeen - s.StartedAt;
            var end = i + 1 < entries.Count ? entries[i + 1].FirstSeen - s.StartedAt : e.LastUpdated - s.StartedAt;
            if (end <= start) end = start + TimeSpan.FromSeconds(3);
            sb.Append(i + 1).Append('\n')
              .Append(Ts(start)).Append(" --> ").Append(Ts(end)).Append('\n')
              .Append("<v ").Append(e.Speaker).Append('>').Append(e.Text).Append("\n\n");
        }
        return sb.ToString();
    }
}
