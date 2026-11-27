using System.IO;
using TeamsCaptionsSaver.Desktop.Models;
using TeamsCaptionsSaver.Desktop.Mom;
using TeamsCaptionsSaver.Desktop.Transcript;
using Xunit;

namespace TeamsCaptionsSaver.Tests;

public sealed class MomTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tcs-mom-" + Guid.NewGuid().ToString("N"));

    public MomTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Saves a meeting the way the app does and returns it as the meetings list sees it.</summary>
    private MeetingRecord SaveMeeting(string title = "Weekly sync")
    {
        var session = new TranscriptSession(s => s) { Title = title };
        session.Merge([
            new ParsedCaption("c1", "Speaker 1", "Let's look at the café cluster."),
            new ParsedCaption("c2", "Matti Virtanen", "The café upgrade is done."),
            new ParsedCaption("c3", "Speaker 1", "Great, then cafés are closed."),
        ]);
        session.Turns[1].IsBookmarked = true;
        var folder = Path.Combine(_folder, title, session.StartedAt.ToString("yyyy-MM-dd"));
        TranscriptExporter.SaveAll(session, folder, "{date}_{time}_{title}", ["txt", "json"]);
        return MeetingCatalog.Scan(_folder).Single(m => m.Title == title);
    }

    [Theory]
    [InlineData("Speaker 1", true)]
    [InlineData("puhuja 12", true)]
    [InlineData("Unknown", true)]
    [InlineData("Vieras", true)]
    [InlineData("Meeting Room 4", true)] // asked about by the user
    [InlineData("Matti Virtanen", false)]
    [InlineData("Speakerphone Inc", false)]
    public void Placeholder_speakers_are_recognised(string name, bool generic) =>
        Assert.Equal(generic, SpeakerNames.IsGeneric(name, ["meeting room 4"]));

    [Fact]
    public void Term_fixes_replace_whole_words_in_any_case()
    {
        TermFix[] fixes = [new("café", "Kafka"), new("cuber netties", "Kubernetes")];
        Assert.Equal("Kafka and Kubernetes, but cafés stay.",
            Corrections.ReplaceTerms("Café and Cuber Netties, but cafés stay.", fixes));
        Assert.Equal("costs $5", Corrections.ReplaceTerms("costs five", [new("five", "$5")]));
    }

    [Fact]
    public void The_Copilot_command_line_keeps_paths_with_spaces_together()
    {
        var harness = HarnessConfig.CopilotCli();
        var args = HarnessRunner.SplitArguments(harness.Arguments)
            .Select(a => HarnessRunner.Fill(a, new Dictionary<string, string>
            {
                ["prompt"] = "Read the task file C:\\My Docs\\task.md and do it.",
                ["momFolder"] = "C:\\My Docs\\Weekly sync_MoM",
                ["log"] = "C:\\My Docs\\log.md",
            })).ToList();

        Assert.Equal(["-p", "Read the task file C:\\My Docs\\task.md and do it.", "--add-dir", "C:\\My Docs\\Weekly sync_MoM",
            "--allow-all-tools", "--deny-tool", "shell", "--no-ask-user", "--no-color", "--share", "C:\\My Docs\\log.md"], args);
        Assert.Equal("\"C:\\My Docs\\\\\"", HarnessRunner.Quote("C:\\My Docs\\"));
        Assert.Equal("plain", HarnessRunner.Quote("plain"));
        Assert.DoesNotContain('"', MomTask.Prompt("C:\\a b\\task.md")); // cmd.exe would split it at quotes
    }

    [Fact]
    public void Proposals_are_read_even_inside_a_code_fence()
    {
        var path = Path.Combine(_folder, "proposals.json");
        File.WriteAllText(path, """
            Here you go:
            ```json
            {"proposals": [
              {"type": "speaker", "from": "Speaker 2", "to": "Liisa"},
              {"type": "TERM", "from": "café", "to": "Kafka"},
              {"type": "lesson", "text": "Action items in a table."},
              {"type": "meeting", "text": "Budget decision was missing."},
              {"type": "speaker", "from": "", "to": "nobody"}
            ]}
            ```
            """);
        var proposals = Proposal.Load(path);
        Assert.Equal(["speaker", "term", "lesson", "meeting"], proposals.Select(p => p.Type));
        Assert.Equal(3, proposals.Count(p => p.CanRemember));
        Assert.Empty(Proposal.Load(Path.Combine(_folder, "missing.json")));
    }

    [Fact]
    public void Project_memory_keeps_glossary_fixes_and_people_without_duplicates()
    {
        var memory = new ProjectMemory(Path.Combine(_folder, ".mom"));
        memory.EnsureCreated();
        memory.AddTerm("café", "Kafka");
        memory.AddTerm("Café", "Kafka again");
        memory.AddPerson("Liisa Korhonen");
        memory.AddPerson("liisa korhonen");
        memory.AddLesson("Action items in a table.");

        Assert.Equal([new TermFix("café", "Kafka")], memory.GlossaryFixes());
        Assert.Single(memory.Read(ProjectMemory.PeopleFile).Split('\n'), l => l.Contains("Liisa", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("- Action items in a table.", memory.Read(ProjectMemory.LessonsFile));
    }

    [Fact]
    public void A_saved_transcript_loads_back_with_its_speakers_times_and_stars()
    {
        var meeting = SaveMeeting();
        var session = TranscriptSession.LoadJson(meeting.TranscriptPath);
        Assert.Equal("Weekly sync", session.Title);
        Assert.Equal(meeting.StartedAt, session.StartedAt);
        Assert.Equal(["Speaker 1", "Matti Virtanen", "Speaker 1"], session.Turns.Select(t => t.Speaker));
        Assert.True(session.Turns[1].IsBookmarked);
        Assert.Equal(meeting.BaseName, session.FileBaseName);
    }

    [Fact]
    public void Naming_a_speaker_and_fixing_a_term_corrects_the_transcript_files_and_keeps_the_original()
    {
        var meeting = SaveMeeting();
        var ws = new MomWorkspace(meeting);
        Assert.Equal(["Speaker 1"], ws.SpeakersToAsk([]));

        ws.State.Speakers["Speaker 1"] = "Liisa Korhonen";
        ws.State.Terms.Add(new TermFix("café", "Kafka"));
        ws.SaveState();
        ws.ApplyCorrectionsToTranscript();

        var text = File.ReadAllText(Path.ChangeExtension(meeting.TranscriptPath, "txt"));
        Assert.Contains("Liisa Korhonen: Let's look at the Kafka cluster.", text);
        Assert.Contains("cafés are closed", text);
        Assert.DoesNotContain("Speaker 1", text);
        Assert.Equal(["Liisa Korhonen", "Matti Virtanen"], MeetingCatalog.Read(meeting.TranscriptPath)!.Speakers);

        // Done again from the original, so a changed answer replaces the earlier one.
        var again = new MomWorkspace(meeting);
        Assert.Empty(again.SpeakersToAsk([]));
        again.State.Speakers["Speaker 1"] = "Pekka";
        again.ApplyCorrectionsToTranscript();
        Assert.Contains("Pekka: Let's look", File.ReadAllText(Path.ChangeExtension(meeting.TranscriptPath, "txt")));

        // The MoM folder's JSON files are not meetings.
        Assert.Single(MeetingCatalog.Scan(_folder));
    }

    [Fact]
    public void The_agent_gets_a_task_with_the_memory_the_feedback_and_where_to_write()
    {
        var meeting = SaveMeeting();
        var ws = new MomWorkspace(meeting);
        var memory = new ProjectMemory(Path.Combine(_folder, ".mom"));
        memory.EnsureCreated();
        memory.AddTerm("café", "Kafka");
        memory.AddLesson("Action items in a table.");
        var project = new MomProject { Name = "Platform", WorkingDirectory = _folder };

        ws.WriteTranscriptForAgent(memory.GlossaryFixes());
        Assert.Contains("the Kafka cluster", File.ReadAllText(ws.TranscriptForAgentPath));
        Assert.DoesNotContain("🔊", File.ReadAllText(ws.TranscriptForAgentPath));

        var create = MomTask.Create(ws, project, memory, 1);
        Assert.Contains(ws.VersionPath(1), create);
        Assert.Contains(ws.TranscriptForAgentPath, create);
        Assert.Contains("- Action items in a table.", create);
        Assert.Contains("### Glossary", create); // the file's own heading, demoted under the section

        var revise = MomTask.Revise(ws, project, memory, 2, "Speaker 1 was Liisa.");
        Assert.Contains(ws.VersionPath(1), revise);
        Assert.Contains(ws.VersionPath(2), revise);
        Assert.Contains(ws.ProposalsPath, revise);
        Assert.Contains("Speaker 1 was Liisa.", revise);
    }

    [Fact]
    public void Versions_are_listed_in_order_and_the_latest_is_published_next_to_the_transcript()
    {
        var meeting = SaveMeeting();
        var ws = new MomWorkspace(meeting);
        Directory.CreateDirectory(ws.Folder);
        File.WriteAllText(ws.VersionPath(2), "two");
        File.WriteAllText(ws.VersionPath(10), "ten");
        File.WriteAllText(ws.VersionPath(1), "one");

        Assert.Equal([1, 2, 10], ws.Versions());
        ws.PublishLatest();
        Assert.Equal("ten", File.ReadAllText(meeting.MomPath));
        Assert.True(meeting.HasMom);
    }
}
