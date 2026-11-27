namespace TeamsCaptionsSaver.Desktop.Mom;

/// <summary>
/// A command-line AI agent that can write minutes, run in a project folder.
/// <see cref="Arguments"/> is split like a command line (double quotes group words), then these are
/// filled in: {prompt} (a one-line instruction to read the task file), {task} (the task file),
/// {momFolder} (where the minutes go), {workdir} (the project folder), {log} (a session log file).
/// </summary>
public sealed class HarnessConfig
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";

    /// <summary>
    /// GitHub Copilot CLI without questions (-p, --no-ask-user), allowed to read the repository and
    /// write the minutes folder, but not to run shell commands (a deny rule beats --allow-all-tools).
    /// </summary>
    public static HarnessConfig CopilotCli() => new()
    {
        Name = "GitHub Copilot CLI",
        Command = "copilot",
        Arguments = "-p \"{prompt}\" --add-dir \"{momFolder}\" --allow-all-tools --deny-tool shell --no-ask-user --no-color --share \"{log}\"",
    };
}

/// <summary>A project that minutes are written for: its repository, the agent to use and the instructions.</summary>
public sealed class MomProject
{
    public string Name { get; set; } = "New project";

    /// <summary>The project's repository or documentation folder; the agent runs here and can read it.</summary>
    public string WorkingDirectory { get; set; } = "";

    public string HarnessName { get; set; } = "GitHub Copilot CLI";

    /// <summary>What the minutes should contain and look like.</summary>
    public string Prompt { get; set; } = DefaultPrompt;

    /// <summary>
    /// True: the project memory (people, glossary, lessons) lives in the repository's .mom folder,
    /// versioned with it. False: in the app's own data folder.
    /// </summary>
    public bool MemoryInRepo { get; set; } = true;

    public override string ToString() => Name;

    public const string DefaultPrompt =
        """
        Write the minutes of this meeting in Markdown, in the language the meeting was held in.

        Use these sections:
        1. Summary: three to five sentences on what the meeting was about and what came out of it.
        2. Participants.
        3. Decisions: each decision, with who made or confirmed it.
        4. Action items: a table with the task, the owner and the due date (write "not set" when none was said).
        5. Open questions and risks.
        6. Discussion: the main topics, a short paragraph each.

        Stick to what was said. Don't invent owners, dates or decisions. Use the repository's
        documentation to spell names, products and terms correctly.
        """;
}
