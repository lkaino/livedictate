using System.IO;
using System.Text.Json;

namespace TeamsCaptionsSaver.Desktop.Mom;

/// <summary>A saved meeting transcript, found through its JSON file.</summary>
public sealed class MeetingRecord
{
    public required string TranscriptPath { get; init; }
    public required string Title { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public IReadOnlyList<string> Speakers { get; init; } = [];
    public int TurnCount { get; init; }

    public string Folder => Path.GetDirectoryName(TranscriptPath) ?? "";
    public string BaseName => Path.GetFileNameWithoutExtension(TranscriptPath);

    /// <summary>The latest minutes, next to the transcript.</summary>
    public string MomPath => Path.Combine(Folder, BaseName + "_MoM.md");

    public bool HasMom => File.Exists(MomPath);

    public string DateDisplay => StartedAt.ToString("yyyy-MM-dd ddd");
    public string TimeDisplay => StartedAt.ToString("HH:mm") + (EndedAt is { } end ? $"–{end:HH:mm}" : "");
    public string SpeakersDisplay => string.Join(", ", Speakers);
    public string MomDisplay => HasMom ? "✔ MoM" : "";
}

/// <summary>Finds the meetings saved under the transcripts folder.</summary>
public static class MeetingCatalog
{
    /// <summary>
    /// Every transcript JSON under <paramref name="root"/>, newest first. Filtered exports and the
    /// files in a meeting's MoM folder are skipped, as is anything that isn't a transcript.
    /// </summary>
    public static List<MeetingRecord> Scan(string root)
    {
        var meetings = new List<MeetingRecord>();
        if (!Directory.Exists(root)) return meetings;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.json", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not list meetings in {root}: {ex.Message}");
            return meetings;
        }

        foreach (var path in files)
        {
            if (Path.GetFileNameWithoutExtension(path).EndsWith("_filtered", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFileName(Path.GetDirectoryName(path) ?? "").EndsWith(MomWorkspace.FolderSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (Read(path) is { } meeting) meetings.Add(meeting);
        }
        return meetings.OrderByDescending(m => m.StartedAt).ToList();
    }

    public static MeetingRecord? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("startedAt", out var started))
                return null;

            return new MeetingRecord
            {
                TranscriptPath = path,
                Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                StartedAt = started.GetDateTime(),
                EndedAt = root.TryGetProperty("endedAt", out var e) && e.ValueKind == JsonValueKind.String ? e.GetDateTime() : null,
                Speakers = root.TryGetProperty("speakers", out var s) && s.ValueKind == JsonValueKind.Array
                    ? s.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : [],
                TurnCount = turns.GetArrayLength(),
            };
        }
        catch (Exception)
        {
            return null; // not one of ours, or half-written
        }
    }
}
