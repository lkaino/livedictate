using System.IO;
using System.Text.Json;

namespace TeamsCaptionsSaver.Desktop.Models;

public sealed class AppSettings
{
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Teams Transcripts");

    /// <summary>Variables: {date} {time} {title}</summary>
    public string FilenamePattern { get; set; } = "{date}_{time}_{title}";

    /// <summary>Subfolders of <see cref="OutputFolder"/>, separated by / (variables as above; empty = none).</summary>
    public string SubfolderPattern { get; set; } = "{title}/{date}";

    public bool AutoSaveOnMeetingEnd { get; set; } = true;
    public List<string> AutoSaveFormats { get; set; } = ["txt", "md", "json"];

    /// <summary>Open More &gt; Language and speech &gt; Turn on live captions when a meeting starts.</summary>
    public bool AutoEnableCaptions { get; set; } = true;

    public bool AutoScroll { get; set; } = true;

    /// <summary>While a meeting is on, save the transcript every this many minutes (0 = only at the end).</summary>
    public int PeriodicSaveMinutes { get; set; } = 2;

    /// <summary>Record the meeting's audio (microphone + speaker output) next to the transcript.</summary>
    public bool RecordAudio { get; set; } = true;

    /// <summary>An <c>Audio.AudioDevices</c> choice: a special value or a device ID.</summary>
    public string MicrophoneDevice { get; set; } = ""; // AudioDevices.Auto

    /// <summary>An <c>Audio.AudioDevices</c> choice for the speaker output (what Teams plays).</summary>
    public string OutputDevice { get; set; } = ""; // AudioDevices.Auto

    /// <summary>2 = microphone left, other people right; 1 = mixed together.</summary>
    public int AudioChannels { get; set; } = 2;

    public int AudioBitrateKbps { get; set; } = 48;

    /// <summary>A new audio file is started every this many minutes.</summary>
    public int AudioPartMinutes { get; set; } = 10;

    public Dictionary<string, string> SpeakerAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Speaker names to always ask about before writing minutes (a meeting room's account, say).</summary>
    public List<string> AlwaysAskSpeakers { get; set; } = [];

    /// <summary>Command-line AI agents that can write minutes.</summary>
    public List<Mom.HarnessConfig> Harnesses { get; set; } = [Mom.HarnessConfig.CopilotCli()];

    /// <summary>Projects that minutes are written for.</summary>
    public List<Mom.MomProject> MomProjects { get; set; } = [];

    /// <summary>The project picked last, offered first next time.</summary>
    public string? LastMomProject { get; set; }

    public static string AppDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeamsCaptionsSaver");

    private static string SettingsPath => Path.Combine(AppDataFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                if (loaded != null)
                {
                    // Deserialization replaces the dictionary, losing the case-insensitive comparer.
                    loaded.SpeakerAliases = new(loaded.SpeakerAliases, StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to load settings: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to save settings: {ex.Message}");
        }
    }
}
