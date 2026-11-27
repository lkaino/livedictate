using System.IO;
using System.Text;

namespace TeamsCaptionsSaver.Desktop.Mom;

/// <summary>
/// Writes the task file handed to the agent. Everything the agent needs is in it or named in it,
/// so the command line only has to say "read this file" - no quoting trouble, no length limits.
/// </summary>
public static class MomTask
{
    /// <summary>The one-line prompt put on the command line. No quotes, so it survives cmd.exe.</summary>
    public static string Prompt(string taskPath) =>
        $"Read the task file {taskPath} and do exactly what it says. Don't change any other files.";

    public static string Create(MomWorkspace ws, MomProject project, ProjectMemory memory, int version)
    {
        var sb = Intro(ws, project);
        AppendMemory(sb, memory);
        sb.AppendLine("## Output").AppendLine();
        sb.AppendLine($"Write the minutes, in Markdown, to this file: {ws.VersionPath(version)}");
        sb.AppendLine("Write only that file. Don't change anything in the repository.");
        return sb.ToString();
    }

    public static string Revise(MomWorkspace ws, MomProject project, ProjectMemory memory, int version, string feedback)
    {
        var sb = Intro(ws, project);
        AppendMemory(sb, memory);
        sb.AppendLine("## Previous version of the minutes").AppendLine();
        sb.AppendLine($"The current minutes are in {ws.VersionPath(version - 1)}.").AppendLine();
        sb.AppendLine("## The user's feedback on them").AppendLine();
        sb.AppendLine(feedback.Trim()).AppendLine();
        sb.AppendLine("## Output").AppendLine();
        sb.AppendLine($"1. Write the corrected minutes, in Markdown, to this file: {ws.VersionPath(version)}");
        sb.AppendLine("   Apply all of the feedback. Keep everything the feedback doesn't ask to change.");
        sb.AppendLine();
        sb.AppendLine($"2. Sort each point of the feedback and write the result as JSON to this file: {ws.ProposalsPath}");
        sb.AppendLine("""
               The user decides which points are remembered for future meetings, so describe each one plainly:

               ```json
               {"proposals": [
                 {"type": "speaker", "from": "Speaker 2", "to": "Liisa Korhonen"},
                 {"type": "term", "from": "café", "to": "Kafka"},
                 {"type": "lesson", "text": "Put action items in a table with owner and due date."},
                 {"type": "meeting", "text": "The budget decision was missing."}
               ]}
               ```

               - "speaker": a person appears under the wrong name or a placeholder. "from" is the name exactly
                 as it appears in the transcript, "to" the right name.
               - "term": a word or phrase the captions got wrong. "from" is the wrong text exactly as it
                 appears in the transcript, "to" the right one.
               - "lesson": the feedback is a lasting preference for all minutes of this project. Write it
                 as a short general rule, not about this meeting.
               - "meeting": it only concerns this meeting's content.

               Write only these two files. Don't change anything in the repository.
            """);
        return sb.ToString();
    }

    private static StringBuilder Intro(MomWorkspace ws, MomProject project)
    {
        var m = ws.Meeting;
        var sb = new StringBuilder();
        sb.AppendLine($"# Task: minutes of the meeting \"{m.Title}\"").AppendLine();
        sb.AppendLine($"You write meeting minutes for the project \"{project.Name}\". You are running in the " +
                      "project's folder: use its documentation for context and to spell names, products " +
                      "and terms correctly.").AppendLine();
        sb.AppendLine("## The meeting").AppendLine();
        sb.AppendLine($"- Title: {m.Title}");
        sb.AppendLine($"- Date: {m.StartedAt:dddd yyyy-MM-dd}, {m.TimeDisplay}");
        sb.AppendLine($"- Transcript: {ws.TranscriptForAgentPath}");
        sb.AppendLine();
        sb.AppendLine("The transcript comes from Teams live captions: \"[time] Speaker: text\" per line. Speaker " +
                      "names are reliable. The words are sometimes misheard, especially names and technical " +
                      "terms; use the glossary and the repository to work out what was meant.").AppendLine();
        sb.AppendLine("## How to write the minutes").AppendLine();
        sb.AppendLine(project.Prompt.Trim()).AppendLine();
        return sb;
    }

    private static void AppendMemory(StringBuilder sb, ProjectMemory memory)
    {
        void Section(string title, string intro, string content)
        {
            if (content.Length == 0) return;
            sb.AppendLine($"## {title}").AppendLine().AppendLine(intro).AppendLine();
            // Demote the file's own headings so they stay under this section.
            foreach (var line in content.Split('\n'))
                sb.AppendLine(line.StartsWith('#') ? "##" + line.TrimEnd('\r') : line.TrimEnd('\r'));
            sb.AppendLine();
        }

        Section("People", "People in this project's meetings.", memory.Read(ProjectMemory.PeopleFile));
        Section("Glossary", "Terms to spell exactly like this.", memory.Read(ProjectMemory.GlossaryFile));
        Section("Lessons from earlier feedback", "Always follow these rules.", memory.Read(ProjectMemory.LessonsFile));
        Section("Example", "Minutes of an earlier meeting the user approved. Follow its style and structure, " +
                           "not its content.", memory.Example);
    }
}
