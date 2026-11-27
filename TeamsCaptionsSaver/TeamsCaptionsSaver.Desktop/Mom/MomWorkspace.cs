using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamsCaptionsSaver.Desktop.Transcript;

namespace TeamsCaptionsSaver.Desktop.Mom;

/// <summary>What has been decided for one meeting's minutes; saved as state.json in its MoM folder.</summary>
public sealed class MomState
{
    public string? Project { get; set; }

    /// <summary>Speaker as saved in the transcript -> the right name.</summary>
    public Dictionary<string, string> Speakers { get; set; } = [];

    /// <summary>Misheard words fixed in this meeting's transcript.</summary>
    public List<TermFix> Terms { get; set; } = [];

    public bool Final { get; set; }
}

/// <summary>
/// The minutes of one meeting: a "<transcript name>_MoM" folder next to the transcript with every
/// version (MoM.v1.md, MoM.v2.md, ...), the feedback given on them, the task files handed to the
/// agent and their logs. The latest version is also copied next to the transcript as "_MoM.md".
/// </summary>
public sealed partial class MomWorkspace
{
    public const string FolderSuffix = "_MoM";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MomWorkspace(MeetingRecord meeting)
    {
        Meeting = meeting;
        Folder = Path.Combine(meeting.Folder, meeting.BaseName + FolderSuffix);
        State = LoadState();
    }

    public MeetingRecord Meeting { get; }
    public string Folder { get; }
    public MomState State { get; }

    private string StatePath => Path.Combine(Folder, "state.json");

    /// <summary>The transcript as first saved, before any corrections; corrections are always redone from it.</summary>
    private string OriginalTranscriptPath => Path.Combine(Folder, "original-transcript.json");

    public string FeedbackPath => Path.Combine(Folder, "feedback.md");
    public string ProposalsPath => Path.Combine(Folder, "proposals.json");
    public string TaskPath => Path.Combine(Folder, "task.md");
    public string TranscriptForAgentPath => Path.Combine(Folder, "transcript.txt");
    public string VersionPath(int version) => Path.Combine(Folder, $"MoM.v{version}.md");
    public string LogPath(int version) => Path.Combine(Folder, $"agent-session-v{version}.md");

    [GeneratedRegex(@"^MoM\.v(\d+)\.md$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionFile();

    /// <summary>Version numbers that exist, oldest first.</summary>
    public List<int> Versions() => !Directory.Exists(Folder) ? []
        : Directory.GetFiles(Folder, "MoM.v*.md")
            .Select(f => VersionFile().Match(Path.GetFileName(f)))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).Order().ToList();

    public int LatestVersion => Versions().LastOrDefault();

    private MomState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var state = JsonSerializer.Deserialize<MomState>(File.ReadAllText(StatePath), JsonOptions) ?? new();
                state.Speakers = new(state.Speakers, StringComparer.OrdinalIgnoreCase);
                return state;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read {StatePath}: {ex.Message}");
        }
        return new MomState { Speakers = new(StringComparer.OrdinalIgnoreCase) };
    }

    public void SaveState()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(State, JsonOptions));
    }

    /// <summary>Copies the latest version next to the transcript, where it's easy to find.</summary>
    public void PublishLatest()
    {
        if (LatestVersion > 0) File.Copy(VersionPath(LatestVersion), Meeting.MomPath, overwrite: true);
    }

    public void AppendFeedback(int version, string feedback)
    {
        Directory.CreateDirectory(Folder);
        File.AppendAllText(FeedbackPath,
            $"## Feedback on version {version} ({DateTime.Now:yyyy-MM-dd HH:mm})\n\n{feedback.Trim()}\n\n", Encoding.UTF8);
    }

    /// <summary>The transcript with this meeting's speaker and term corrections applied.</summary>
    public TranscriptSession LoadTranscript(IEnumerable<TermFix>? extraTerms = null)
    {
        var terms = State.Terms.Concat(extraTerms ?? []).ToList();
        var source = File.Exists(OriginalTranscriptPath) ? OriginalTranscriptPath : Meeting.TranscriptPath;
        var session = TranscriptSession.LoadJson(source, entry =>
        {
            if (State.Speakers.TryGetValue(entry.Speaker, out var name) && name.Length > 0) entry.Speaker = name;
            if (terms.Count > 0) entry.Text = Corrections.ReplaceTerms(entry.Text, terms);
        });
        session.FileBaseName = Meeting.BaseName;
        return session;
    }

    /// <summary>
    /// Speakers whose real name should be asked before minutes are written: placeholders like
    /// "Speaker 1" (see <see cref="SpeakerNames"/>) that haven't been given a name yet.
    /// </summary>
    public List<string> SpeakersToAsk(IEnumerable<string> alwaysAsk)
    {
        var source = File.Exists(OriginalTranscriptPath) ? OriginalTranscriptPath : Meeting.TranscriptPath;
        var names = new List<string>();
        TranscriptSession.LoadJson(source, entry =>
        {
            if (!names.Contains(entry.Speaker)) names.Add(entry.Speaker);
        });
        var ask = alwaysAsk.ToList();
        return names.Where(n => SpeakerNames.IsGeneric(n, ask) && !State.Speakers.ContainsKey(n)).ToList();
    }

    /// <summary>
    /// Saves the meeting's transcript files again with the corrections applied, in every format that
    /// was saved before. The first time, the uncorrected JSON is kept in the MoM folder.
    /// </summary>
    public void ApplyCorrectionsToTranscript()
    {
        Directory.CreateDirectory(Folder);
        if (!File.Exists(OriginalTranscriptPath)) File.Copy(Meeting.TranscriptPath, OriginalTranscriptPath);
        var session = LoadTranscript();
        var formats = TranscriptExporter.Formats.Select(f => f.Ext)
            .Where(ext => File.Exists(Path.Combine(Meeting.Folder, $"{Meeting.BaseName}.{ext}")))
            .Append("json").ToList();
        TranscriptExporter.SaveAll(session, Meeting.Folder, "{title}", formats);
        Log.Write($"Corrected transcript saved: {Meeting.BaseName}");
    }

    /// <summary>
    /// Writes the transcript the agent reads: corrected, with the project's glossary fixes applied,
    /// as plain text without the audio positions.
    /// </summary>
    public void WriteTranscriptForAgent(IEnumerable<TermFix> glossaryFixes)
    {
        Directory.CreateDirectory(Folder);
        var session = LoadTranscript(glossaryFixes);
        session.AudioParts = [];
        File.WriteAllText(TranscriptForAgentPath, TranscriptExporter.ToText(session), Encoding.UTF8);
    }
}
