using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop.Mom.Ui;

/// <summary>The saved meetings, newest first, with a way to write minutes for each.</summary>
public partial class MeetingsWindow : Window
{
    private readonly AppSettings _settings;
    private List<MeetingRecord> _meetings = [];

    public MeetingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        CountText.Text = "Looking for meetings…";
        var folder = _settings.OutputFolder;
        _meetings = await Task.Run(() => MeetingCatalog.Scan(folder));
        ApplySearch();
    }

    private void ApplySearch()
    {
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _meetings.Where(m => words.All(w =>
            m.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
            m.DateDisplay.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
            m.SpeakersDisplay.Contains(w, StringComparison.CurrentCultureIgnoreCase))).ToList();
        var selected = (MeetingGrid.SelectedItem as MeetingRecord)?.TranscriptPath;
        MeetingGrid.ItemsSource = shown;
        MeetingGrid.SelectedItem = shown.FirstOrDefault(m => m.TranscriptPath == selected);
        CountText.Text = _meetings.Count == 0
            ? $"No saved meetings in {_settings.OutputFolder} yet."
            : $"{shown.Count} of {_meetings.Count} meetings";
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplySearch();

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnProjectsClick(object sender, RoutedEventArgs e) => new ProjectsWindow(_settings) { Owner = this }.ShowDialog();

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MeetingGrid.SelectedItem != null) OpenMinutes();
    }

    private void OnMinutesClick(object sender, RoutedEventArgs e) => OpenMinutes();

    private void OpenMinutes()
    {
        if (MeetingGrid.SelectedItem is not MeetingRecord meeting)
        {
            MessageBox.Show(this, "Select a meeting first.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new MomWindow(_settings, meeting, _meetings) { Owner = this };
        window.Closed += (_, _) => MeetingGrid.Items.Refresh(); // the Minutes column
        window.Show();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = (MeetingGrid.SelectedItem as MeetingRecord)?.Folder ?? _settings.OutputFolder;
        if (Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
