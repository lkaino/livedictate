using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop.Mom.Ui;

/// <summary>Edits the MoM projects and the AI agents (harnesses). Works on copies; Save writes them back.</summary>
public partial class ProjectsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<MomProject> _projects;
    private readonly ObservableCollection<HarnessConfig> _harnesses;
    private MomProject? _editing;
    private bool _loading;

    public ProjectsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _projects = new(settings.MomProjects.Select(Copy));
        _harnesses = new(settings.Harnesses.Select(h => new HarnessConfig { Name = h.Name, Command = h.Command, Arguments = h.Arguments }));
        ProjectList.ItemsSource = _projects;
        HarnessGrid.ItemsSource = _harnesses;
        if (_projects.Count > 0) ProjectList.SelectedIndex = 0;
    }

    private static MomProject Copy(MomProject p) => new()
    {
        Name = p.Name, WorkingDirectory = p.WorkingDirectory, HarnessName = p.HarnessName,
        Prompt = p.Prompt, MemoryInRepo = p.MemoryInRepo,
    };

    private void OnProjectSelected(object sender, SelectionChangedEventArgs e)
    {
        StoreForm();
        _editing = ProjectList.SelectedItem as MomProject;
        ProjectForm.IsEnabled = _editing != null;
        HarnessBox.ItemsSource = _harnesses.Select(h => h.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (_editing == null) return;
        _loading = true;
        NameBox.Text = _editing.Name;
        FolderBox.Text = _editing.WorkingDirectory;
        HarnessBox.SelectedItem = _editing.HarnessName;
        if (HarnessBox.SelectedIndex < 0 && HarnessBox.Items.Count > 0) HarnessBox.SelectedIndex = 0;
        MemoryInRepoBox.IsChecked = _editing.MemoryInRepo;
        PromptBox.Text = _editing.Prompt;
        _loading = false;
    }

    /// <summary>Copies the form into the project being edited.</summary>
    private void StoreForm()
    {
        if (_editing == null) return;
        _editing.Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "Unnamed project";
        _editing.WorkingDirectory = FolderBox.Text.Trim();
        _editing.HarnessName = HarnessBox.SelectedItem as string ?? _editing.HarnessName;
        _editing.MemoryInRepo = MemoryInRepoBox.IsChecked == true;
        _editing.Prompt = PromptBox.Text;
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_editing == null || _loading) return;
        _editing.Name = NameBox.Text;
        ProjectList.Items.Refresh();
    }

    private void OnAddProjectClick(object sender, RoutedEventArgs e)
    {
        var project = new MomProject { HarnessName = _harnesses.FirstOrDefault()?.Name ?? "" };
        _projects.Add(project);
        ProjectList.SelectedItem = project;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnRemoveProjectClick(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        if (MessageBox.Show(this, $"Remove the project \"{_editing.Name}\"? Its memory files are kept.", Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var project = _editing;
        _editing = null;
        _projects.Remove(project);
        if (_projects.Count > 0) ProjectList.SelectedIndex = 0;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = FolderBox.Text };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void OnAddCopilotClick(object sender, RoutedEventArgs e) => _harnesses.Add(HarnessConfig.CopilotCli());

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        HarnessGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        StoreForm();
        var missing = _projects.FirstOrDefault(p => p.WorkingDirectory.Length == 0);
        if (missing != null)
        {
            MessageBox.Show(this, $"Choose a folder for the project \"{missing.Name}\".", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            ProjectList.SelectedItem = missing;
            return;
        }
        _settings.MomProjects = _projects.ToList();
        _settings.Harnesses = _harnesses.Where(h => !string.IsNullOrWhiteSpace(h.Name) && !string.IsNullOrWhiteSpace(h.Command)).ToList();
        _settings.Save();
        DialogResult = true;
    }
}
