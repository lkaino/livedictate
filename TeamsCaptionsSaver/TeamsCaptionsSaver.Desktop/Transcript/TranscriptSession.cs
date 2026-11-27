using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop.Transcript;

/// <summary>
/// The captions captured for one meeting. Teams edits a caption in place while the speaker talks,
/// so each scan updates existing entries (matched by UIA element identity) and appends new ones.
/// Must be used from the UI thread (it owns an ObservableCollection bound to the grid).
/// </summary>
public sealed class TranscriptSession
{
    private const int DuplicateLookback = 30;
    private const int MinDuplicateLength = 25;

    private readonly Dictionary<string, CaptionEntry> _byKey = new();
    private readonly Func<string, string> _resolveAlias;

    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; } = "Teams Meeting";
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public DateTime? EndedAt { get; set; }

    /// <summary>
    /// File name (without extension) fixed when the meeting starts, so periodic saves keep overwriting
    /// the same files and the audio parts share the transcript's name. Null = built from the pattern.
    /// </summary>
    public string? FileBaseName { get; set; }

    /// <summary>Folder under the output folder (e.g. "Weekly sync\2026-11-18"), fixed like <see cref="FileBaseName"/>.</summary>
    public string? Subfolder { get; set; }

    /// <summary>The meeting's audio files, oldest first, each with the time it starts.</summary>
    public IReadOnlyList<Audio.AudioPart> AudioParts { get; set; } = [];
    /// <summary>Every caption block Teams showed, in order. Exports like WebVTT use these directly.</summary>
    public ObservableCollection<CaptionEntry> Entries { get; } = [];

    /// <summary>The same captions grouped by speaker: what the window shows and most exports write.</summary>
    public ObservableCollection<SpeakerTurn> Turns { get; } = [];

    /// <summary>True when entries changed since the last backup.</summary>
    public bool IsDirty { get; set; }

    /// <summary>True when there are entries that have not been exported yet.</summary>
    public bool HasUnsavedChanges { get; set; }

    public TranscriptSession(Func<string, string> resolveAlias)
    {
        _resolveAlias = resolveAlias;
    }

    public IEnumerable<string> Speakers => Turns.Select(t => t.Speaker).Distinct();

    public void Merge(IReadOnlyList<ParsedCaption> captions)
    {
        var now = DateTime.Now;
        var visibleKeys = captions.Select(c => c.Key).ToHashSet();

        foreach (var caption in captions)
        {
            if (_byKey.TryGetValue(caption.Key, out var existing))
            {
                if (existing.Text != caption.Text)
                {
                    existing.Text = caption.Text;
                    existing.LastUpdated = now;
                    MarkChanged();
                }
                continue;
            }

            // Teams sometimes re-renders the caption list, giving an old caption a new element.
            // Re-attach it to the existing entry instead of duplicating it. Short captions ("Okay.")
            // are legitimately repeated, so only longer ones are treated as re-renders.
            var match = caption.Text.Length < MinDuplicateLength ? null : Entries.Reverse().Take(DuplicateLookback).FirstOrDefault(e =>
                e.RawSpeaker == caption.Speaker && e.Text.Length >= MinDuplicateLength && !visibleKeys.Contains(e.Key) &&
                (e.Text == caption.Text || caption.Text.StartsWith(e.Text, StringComparison.Ordinal) ||
                 e.Text.StartsWith(caption.Text, StringComparison.Ordinal)));
            if (match != null)
            {
                _byKey[caption.Key] = match;
                if (caption.Text.Length > match.Text.Length)
                {
                    match.Text = caption.Text;
                    match.LastUpdated = now;
                    MarkChanged();
                }
                continue;
            }

            var entry = new CaptionEntry
            {
                Key = caption.Key,
                FirstSeen = now,
                RawSpeaker = caption.Speaker,
                Speaker = _resolveAlias(caption.Speaker),
                Text = caption.Text,
                LastUpdated = now,
            };
            _byKey[caption.Key] = entry;
            Entries.Add(entry);
            AddToTurns(entry);
            MarkChanged();
        }
    }

    public void ApplyAliases()
    {
        foreach (var e in Entries) e.Speaker = _resolveAlias(e.RawSpeaker);
        // Renaming can make neighbouring turns belong to the same person (or split them), so regroup.
        foreach (var t in Turns) t.Detach();
        Turns.Clear();
        foreach (var e in Entries) AddToTurns(e);
        MarkChanged();
    }

    /// <summary>
    /// Reads a transcript saved as JSON by <see cref="TranscriptExporter"/>, so it can be corrected and
    /// saved again. <paramref name="fix"/> may change each caption (speaker, text) before it's grouped into turns.
    /// </summary>
    public static TranscriptSession LoadJson(string path, Action<CaptionEntry>? fix = null)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var session = new TranscriptSession(s => s)
        {
            Title = Str(root, "title") ?? "Teams Meeting",
            StartedAt = root.GetProperty("startedAt").GetDateTime(),
            EndedAt = root.TryGetProperty("endedAt", out var ended) && ended.ValueKind == JsonValueKind.String
                ? ended.GetDateTime() : null,
            FileBaseName = Path.GetFileNameWithoutExtension(path),
        };

        var folder = Path.GetDirectoryName(path) ?? "";
        if (root.TryGetProperty("audioFiles", out var audio))
            session.AudioParts = audio.EnumerateArray()
                .Select(a => new Audio.AudioPart(Path.Combine(folder, Str(a, "file") ?? ""), a.GetProperty("start").GetDateTime()))
                .ToList();

        var marks = new List<(CaptionEntry First, bool Bookmarked, List<string> Highlights)>();
        foreach (var turn in root.GetProperty("turns").EnumerateArray())
        {
            var speaker = Str(turn, "speaker") ?? "";
            var raw = Str(turn, "originalSpeaker") ?? speaker;
            var end = turn.TryGetProperty("end", out var e) ? e.GetDateTime() : session.StartedAt;
            var captions = turn.GetProperty("captions").EnumerateArray().ToList();
            CaptionEntry? first = null;
            for (int i = 0; i < captions.Count; i++)
            {
                var time = captions[i].GetProperty("time").GetDateTime();
                var entry = new CaptionEntry
                {
                    Key = Guid.NewGuid().ToString("N"),
                    FirstSeen = time,
                    RawSpeaker = raw,
                    Speaker = speaker,
                    Text = Str(captions[i], "text") ?? "",
                    LastUpdated = i + 1 < captions.Count ? captions[i + 1].GetProperty("time").GetDateTime() : end,
                };
                fix?.Invoke(entry);
                first ??= entry;
                session.Entries.Add(entry);
                session.AddToTurns(entry);
            }
            if (first != null)
                marks.Add((first, turn.TryGetProperty("bookmarked", out var b) && b.ValueKind == JsonValueKind.True,
                    turn.TryGetProperty("highlights", out var h) ? h.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : []));
        }

        // Stars and highlights are saved per turn; put them back on the turn's captions.
        foreach (var (first, bookmarked, highlights) in marks)
        {
            if (bookmarked) first.IsBookmarked = true;
            var turn = session.Turns.First(t => t.Entries.Contains(first));
            foreach (var phrase in highlights) turn.AddHighlight(phrase);
        }
        return session;

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private void AddToTurns(CaptionEntry entry)
    {
        if (Turns.Count > 0 && Turns[^1].Speaker == entry.Speaker)
            Turns[^1].Add(entry);
        else
            Turns.Add(new SpeakerTurn(entry));
    }

    /// <summary>Records that the transcript changed (new captions, or the user starred or highlighted something).</summary>
    public void MarkChanged()
    {
        IsDirty = true;
        HasUnsavedChanges = true;
    }
}
