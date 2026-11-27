using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop.Mom;

/// <summary>
/// What a project remembers between meetings, as plain Markdown files the user can read and trim:
/// people.md (names and roles), glossary.md (terms, and "wrong -> right" fixes), lessons.md (rules
/// learned from feedback) and examples/latest.md (the last minutes marked final, as a style example).
/// </summary>
public sealed partial class ProjectMemory(string folder)
{
    public const string PeopleFile = "people.md";
    public const string GlossaryFile = "glossary.md";
    public const string LessonsFile = "lessons.md";
    private const string ExampleFile = "examples/latest.md";

    public string Folder { get; } = folder;

    public static ProjectMemory For(MomProject project) => new(project.MemoryInRepo && project.WorkingDirectory.Length > 0
        ? Path.Combine(project.WorkingDirectory, ".mom")
        : Path.Combine(AppSettings.AppDataFolder, "mom-memory", SafeName(project.Name)));

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim(' ', '.').Length == 0 ? "project" : name.Trim(' ', '.');
    }

    /// <summary>Creates the folder and the files, each with a short note on what goes in it.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Folder);
        Create(PeopleFile, """
            # People

            Who takes part in this project's meetings: full name, role, and what others call them.
            For example: "- Matti Virtanen, product owner (also MV)".

            """);
        Create(GlossaryFile, """
            # Glossary

            Project terms, product names and abbreviations, so the minutes spell them right.
            Lines like "- wrong -> right" are also fixed in the transcript before the AI reads it
            (whole words, any case), for words that live captions keep getting wrong.

            """);
        Create(LessonsFile, """
            # Lessons

            Rules learned from feedback on earlier minutes. Every rule here applies to all future
            minutes of this project; remove rules that are no longer wanted.

            """);
    }

    private void Create(string file, string content)
    {
        var path = Path.Combine(Folder, file);
        if (!File.Exists(path)) File.WriteAllText(path, content, Encoding.UTF8);
    }

    public string Read(string file)
    {
        var path = Path.Combine(Folder, file);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
    }

    public string Example => Read(ExampleFile);

    [GeneratedRegex(@"^\s*[-*]\s*(?<from>.+?)\s*(->|→)\s*(?<to>.+?)\s*$")]
    private static partial Regex FixLine();

    /// <summary>The "- wrong -> right" lines of the glossary.</summary>
    public List<TermFix> GlossaryFixes() =>
        Read(GlossaryFile).Split('\n')
            .Select(line => FixLine().Match(line))
            .Where(m => m.Success)
            .Select(m => new TermFix(m.Groups["from"].Value.Trim('"', '“', '”'), m.Groups["to"].Value.Trim('"', '“', '”')))
            .ToList();

    public void AddTerm(string from, string to)
    {
        if (GlossaryFixes().Any(f => f.From.Equals(from, StringComparison.OrdinalIgnoreCase))) return;
        Append(GlossaryFile, $"- {from} -> {to}");
    }

    /// <summary>Adds the person unless people.md already mentions them.</summary>
    public void AddPerson(string name)
    {
        if (Read(PeopleFile).Contains(name, StringComparison.OrdinalIgnoreCase)) return;
        Append(PeopleFile, $"- {name}");
    }

    public void AddLesson(string lesson) => Append(LessonsFile, $"- {lesson.Trim()}");

    public void SaveExample(string minutes)
    {
        var path = Path.Combine(Folder, ExampleFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, minutes, Encoding.UTF8);
    }

    private void Append(string file, string line)
    {
        EnsureCreated();
        var path = Path.Combine(Folder, file);
        var text = File.ReadAllText(path);
        if (text.Length > 0 && !text.EndsWith('\n')) text += Environment.NewLine;
        File.WriteAllText(path, text + line + Environment.NewLine, Encoding.UTF8);
    }
}
