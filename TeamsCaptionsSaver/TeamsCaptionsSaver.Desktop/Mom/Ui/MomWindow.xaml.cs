using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop.Mom.Ui;

/// <summary>A speaker Teams didn't name, and the name the user gives them.</summary>
public sealed class SpeakerQuestion
{
    public required string Name { get; init; }
    public string Answer { get; set; } = "";
    public List<string> Suggestions { get; init; } = [];
}

/// <summary>
/// Writes and revises one meeting's minutes with the project's AI agent. Feedback is written in plain
/// words; the agent revises the minutes and sorts the feedback into things worth remembering, which
/// the user ticks before they go into the project memory.
/// </summary>
public partial class MomWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MomWorkspace _workspace;
    private readonly IReadOnlyList<MeetingRecord> _allMeetings;
    private readonly StringBuilder _output = new();
    private CancellationTokenSource? _running;

    public MomWindow(AppSettings settings, MeetingRecord meeting, IReadOnlyList<MeetingRecord> allMeetings)
    {
        InitializeComponent();
        _settings = settings;
        _allMeetings = allMeetings;
        _workspace = new MomWorkspace(meeting);
        Title = $"Minutes - {meeting.Title}";
        MeetingTitle.Text = meeting.Title;
        MeetingInfo.Text = $"{meeting.DateDisplay}, {meeting.TimeDisplay} · {meeting.SpeakersDisplay}";
        LoadProjects();
        LoadVersions();
        LoadSpeakerQuestions();
        ShowProposals();
        UpdateButtons();
    }

    private MomProject? Project => ProjectBox.SelectedItem as MomProject;

    // --- Projects -----------------------------------------------------------------------------

    private void LoadProjects()
    {
        var wanted = (ProjectBox.SelectedItem as MomProject)?.Name ?? _workspace.State.Project ?? _settings.LastMomProject;
        ProjectBox.ItemsSource = _settings.MomProjects;
        ProjectBox.SelectedItem = _settings.MomProjects.FirstOrDefault(p => p.Name == wanted) ?? _settings.MomProjects.FirstOrDefault();
        if (_settings.MomProjects.Count == 0)
            StatusText.Text = "Add a project first: its folder, the AI agent and how the minutes should look (Projects and AI agents…).";
    }

    private void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadSpeakerQuestions(); // suggestions come partly from the project's people list
        UpdateButtons();
    }

    private void OnProjectsClick(object sender, RoutedEventArgs e)
    {
        if (new ProjectsWindow(_settings) { Owner = this }.ShowDialog() == true) LoadProjects();
    }

    private void OnMemoryClick(object sender, RoutedEventArgs e)
    {
        if (Project == null) return;
        var memory = ProjectMemory.For(Project);
        memory.EnsureCreated();
        OpenInExplorer(memory.Folder);
    }

    // --- Speakers -----------------------------------------------------------------------------

    /// <summary>Asks for the real names of placeholder speakers ("Speaker 1") until they have one.</summary>
    private void LoadSpeakerQuestions()
    {
        List<string> ask;
        try
        {
            ask = _workspace.SpeakersToAsk(_settings.AlwaysAskSpeakers);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't read the transcript: {ex.Message}";
            return;
        }
        var previous = (SpeakerList.ItemsSource as List<SpeakerQuestion>)?.ToDictionary(q => q.Name, q => q.Answer) ?? [];
        var suggestions = NameSuggestions();
        SpeakerList.ItemsSource = ask.Select(name => new SpeakerQuestion
        {
            Name = name,
            Answer = previous.GetValueOrDefault(name, ""),
            Suggestions = suggestions,
        }).ToList();
        SpeakersPanel.Visibility = ask.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Names from earlier meetings with the same title, and the project's people list.</summary>
    private List<string> NameSuggestions()
    {
        var alwaysAsk = _settings.AlwaysAskSpeakers;
        var names = new List<string>();
        foreach (var other in _allMeetings.Where(m => m.Title == _workspace.Meeting.Title))
        {
            names.AddRange(other.Speakers);
            names.AddRange(new MomWorkspace(other).State.Speakers.Values);
        }
        if (Project != null)
            foreach (var line in ProjectMemory.For(Project).Read(ProjectMemory.PeopleFile).Split('\n'))
                if (line.TrimStart().StartsWith("- "))
                    names.Add(line.TrimStart()[2..].Split(',', '(')[0].Trim());
        return names.Where(n => n.Length > 0 && !SpeakerNames.IsGeneric(n, alwaysAsk))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Saves the names given so far; the transcript files are corrected when any were given.</summary>
    private void StoreSpeakerAnswers()
    {
        if (SpeakerList.ItemsSource is not List<SpeakerQuestion> questions) return;
        var answered = questions.Where(q => q.Answer.Trim().Length > 0).ToList();
        if (answered.Count == 0) return;
        foreach (var q in answered) _workspace.State.Speakers[q.Name] = q.Answer.Trim();
        _workspace.SaveState();
        _workspace.ApplyCorrectionsToTranscript();
        if (Project != null)
        {
            var memory = ProjectMemory.For(Project);
            foreach (var q in answered) memory.AddPerson(q.Answer.Trim());
        }
        LoadSpeakerQuestions();
    }

    // --- Versions -----------------------------------------------------------------------------

    private void LoadVersions(int? select = null)
    {
        var versions = _workspace.Versions();
        VersionBox.ItemsSource = versions.Select(v => $"Version {v}").Reverse().ToList();
        VersionBox.Tag = versions.AsEnumerable().Reverse().ToList();
        if (versions.Count == 0)
        {
            MomText.Text = "No minutes yet. Pick the project and click Create minutes.";
            return;
        }
        var list = (List<int>)VersionBox.Tag;
        VersionBox.SelectedIndex = Math.Max(0, list.IndexOf(select ?? versions[^1]));
    }

    private int SelectedVersion =>
        VersionBox.Tag is List<int> list && VersionBox.SelectedIndex >= 0 ? list[VersionBox.SelectedIndex] : 0;

    private void OnVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        var version = SelectedVersion;
        if (version == 0) return;
        try
        {
            MomText.Text = File.ReadAllText(_workspace.VersionPath(version));
        }
        catch (Exception ex)
        {
            MomText.Text = $"Couldn't read version {version}: {ex.Message}";
        }
        UpdateButtons();
    }

    // --- Running the agent --------------------------------------------------------------------

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (_workspace.LatestVersion > 0 && MessageBox.Show(this,
                "These minutes already exist. Write them again from scratch? The current versions are kept.",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await RunAgentAsync(feedback: null);
    }

    private async void OnReviseClick(object sender, RoutedEventArgs e)
    {
        var feedback = FeedbackBox.Text.Trim();
        if (feedback.Length == 0)
        {
            MessageBox.Show(this, "Write what's wrong with the minutes first.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            FeedbackBox.Focus();
            return;
        }
        if (await RunAgentAsync(feedback)) FeedbackBox.Clear();
    }

    /// <summary>Creates the minutes (<paramref name="feedback"/> null) or revises the latest version with the feedback.</summary>
    private async Task<bool> RunAgentAsync(string? feedback)
    {
        var project = Project;
        if (project == null)
        {
            OnProjectsClick(this, new RoutedEventArgs());
            return false;
        }
        var harness = _settings.Harnesses.FirstOrDefault(h => h.Name == project.HarnessName) ?? _settings.Harnesses.FirstOrDefault();
        if (harness == null)
        {
            MessageBox.Show(this, "Add an AI agent in Projects and AI agents first.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (!Directory.Exists(project.WorkingDirectory))
        {
            MessageBox.Show(this, $"The project folder doesn't exist:\n{project.WorkingDirectory}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _running = new CancellationTokenSource();
        UpdateButtons();
        lock (_output) _output.Clear();
        LogBox.Clear();
        LogExpander.IsExpanded = true;
        int version = _workspace.LatestVersion + 1;
        bool revise = feedback != null;
        try
        {
            StatusText.Text = "Preparing the transcript…";
            StoreSpeakerAnswers();
            var memory = ProjectMemory.For(project);
            memory.EnsureCreated();
            _workspace.State.Project = project.Name;
            _workspace.SaveState();
            _settings.LastMomProject = project.Name;
            _settings.Save();

            _workspace.WriteTranscriptForAgent(memory.GlossaryFixes());
            var task = revise
                ? MomTask.Revise(_workspace, project, memory, version, feedback!)
                : MomTask.Create(_workspace, project, memory, version);
            await File.WriteAllTextAsync(_workspace.TaskPath, task, Encoding.UTF8);
            if (revise)
            {
                _workspace.AppendFeedback(version - 1, feedback!);
                File.Delete(_workspace.ProposalsPath);
                ShowProposals();
            }

            StatusText.Text = revise
                ? $"{harness.Name} is revising the minutes (version {version})… This can take a few minutes."
                : $"{harness.Name} is writing the minutes… This can take a few minutes.";
            var values = new Dictionary<string, string>
            {
                ["prompt"] = MomTask.Prompt(_workspace.TaskPath),
                ["task"] = _workspace.TaskPath,
                ["momFolder"] = _workspace.Folder,
                ["workdir"] = project.WorkingDirectory,
                ["log"] = _workspace.LogPath(version),
            };
            int exitCode = await HarnessRunner.RunAsync(harness, project.WorkingDirectory, values,
                AppendOutput, _running.Token);
            string output;
            lock (_output) output = _output.ToString();
            await File.WriteAllTextAsync(Path.Combine(_workspace.Folder, $"agent-output-v{version}.log"), output);

            if (!File.Exists(_workspace.VersionPath(version)))
            {
                StatusText.Text = $"{harness.Name} finished (exit code {exitCode}) without writing the minutes. See the agent output below.";
                return false;
            }
            _workspace.PublishLatest();
            _workspace.State.Final = false;
            _workspace.SaveState();
            LoadVersions(version);
            ShowProposals();
            StatusText.Text = $"Version {version} is ready" + (exitCode != 0 ? $" (the agent exited with code {exitCode})." : ".") +
                              (ProposalsPanel.Visibility == Visibility.Visible ? " Choose below what to remember." : "");
            Log.Write($"Minutes v{version} written for '{_workspace.Meeting.Title}' with {harness.Name}.");
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Stopped.";
            return false;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't run {harness.Name}: {ex.Message}";
            Log.Write($"Minutes failed: {ex.Message}");
            return false;
        }
        finally
        {
            _running.Dispose();
            _running = null;
            UpdateButtons();
        }
    }

    /// <summary>Called on the agent's output threads.</summary>
    private void AppendOutput(string line)
    {
        lock (_output) _output.AppendLine(line);
        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _running?.Cancel();

    private void UpdateButtons()
    {
        bool running = _running != null;
        bool hasMinutes = _workspace.LatestVersion > 0;
        CreateButton.IsEnabled = !running && Project != null;
        ReviseButton.IsEnabled = !running && hasMinutes && Project != null;
        FinalButton.IsEnabled = !running && hasMinutes && Project != null;
        ProjectBox.IsEnabled = !running;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CreateButton.Content = hasMinutes ? "Create again" : "Create minutes";
        FinalButton.Content = _workspace.State.Final ? "✔ Final" : "Mark as final";
    }

    // --- What to remember ---------------------------------------------------------------------

    private void ShowProposals()
    {
        var proposals = Proposal.Load(_workspace.ProposalsPath).Where(p => p.CanRemember).ToList();
        ProposalList.ItemsSource = proposals;
        ProposalsPanel.Visibility = proposals.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRememberClick(object sender, RoutedEventArgs e)
    {
        if (Project == null || ProposalList.ItemsSource is not List<Proposal> proposals) return;
        var memory = ProjectMemory.For(Project);
        bool transcriptChanged = false;
        try
        {
            foreach (var p in proposals.Where(p => p.IsSelected))
            {
                switch (p.Type)
                {
                    case Proposal.Speaker:
                        // "from" may be a name we gave earlier ("Speaker 2" -> "Matti"): correct that mapping.
                        var key = _workspace.State.Speakers.FirstOrDefault(kv => kv.Value.Equals(p.From, StringComparison.OrdinalIgnoreCase)).Key ?? p.From;
                        _workspace.State.Speakers[key] = p.To;
                        memory.AddPerson(p.To);
                        transcriptChanged = true;
                        break;
                    case Proposal.Term:
                        _workspace.State.Terms.RemoveAll(t => t.From.Equals(p.From, StringComparison.OrdinalIgnoreCase));
                        _workspace.State.Terms.Add(new TermFix(p.From, p.To));
                        memory.AddTerm(p.From, p.To);
                        transcriptChanged = true;
                        break;
                    case Proposal.Lesson:
                        memory.AddLesson(p.Text);
                        break;
                }
            }
            _workspace.SaveState();
            if (transcriptChanged) _workspace.ApplyCorrectionsToTranscript();
            File.Delete(_workspace.ProposalsPath);
            ShowProposals();
            StatusText.Text = "Saved to the project memory" + (transcriptChanged ? "; the transcript is corrected too." : ".");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        File.Delete(_workspace.ProposalsPath);
        ShowProposals();
    }

    // --- Other actions ------------------------------------------------------------------------

    private void OnFinalClick(object sender, RoutedEventArgs e)
    {
        if (Project == null || SelectedVersion == 0) return;
        ProjectMemory.For(Project).SaveExample(File.ReadAllText(_workspace.VersionPath(SelectedVersion)));
        File.Copy(_workspace.VersionPath(SelectedVersion), _workspace.Meeting.MomPath, overwrite: true);
        _workspace.State.Final = true;
        _workspace.SaveState();
        StatusText.Text = $"Version {SelectedVersion} is final, and is now the project's example of good minutes.";
        UpdateButtons();
    }

    private void OnOpenInEditorClick(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion == 0) return;
        Process.Start(new ProcessStartInfo(_workspace.VersionPath(SelectedVersion)) { UseShellExecute = true });
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) =>
        OpenInExplorer(Directory.Exists(_workspace.Folder) ? _workspace.Folder : _workspace.Meeting.Folder);

    private static void OpenInExplorer(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_running == null) return;
        if (MessageBox.Show(this, "The AI agent is still working. Stop it and close?", Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            e.Cancel = true;
            return;
        }
        _running.Cancel();
    }
}
