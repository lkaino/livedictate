using System.ComponentModel;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace TeamsCaptionsSaver.Desktop.Audio;

/// <summary>One entry in a device list: a special choice or a device, with its live sound level.</summary>
public sealed class AudioDeviceOption(string id, string name) : INotifyPropertyChanged
{
    private double _level;

    public string Id { get; } = id;
    public string Name { get; } = name;

    /// <summary>Sound level right now for the level bar, 0 (silent) to 1 (loud).</summary>
    public double Level
    {
        get => _level;
        set
        {
            if (Math.Abs(_level - value) < 0.005) return;
            _level = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Level)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;
}

/// <summary>
/// Audio device choices for the settings, and turning a saved choice into the devices to record.
/// A choice is either a special value below or a Windows endpoint ID.
/// </summary>
public static class AudioDevices
{
    /// <summary>
    /// Microphone: the Windows default communications microphone (what calling apps use by default).
    /// Output: both the Windows default output and the default communications output, so Teams is
    /// captured whichever of the two it plays through.
    /// </summary>
    public const string Auto = "";

    public const string WindowsDefault = "default";
    public const string Communications = "communications";
    public const string None = "none";

    public static List<AudioDeviceOption> MicrophoneOptions() =>
    [
        new(Auto, "Default communications microphone"),
        new(WindowsDefault, "Default microphone"),
        new(None, "Don't record my microphone"),
        .. ListDevices(DataFlow.Capture),
    ];

    public static List<AudioDeviceOption> OutputOptions() =>
    [
        new(Auto, "Default + default communications output"),
        new(WindowsDefault, "Default output"),
        new(Communications, "Default communications output"),
        new(None, "Don't record other people (speaker)"),
        .. ListDevices(DataFlow.Render),
    ];

    private static IEnumerable<AudioDeviceOption> ListDevices(DataFlow flow)
    {
        var options = new List<AudioDeviceOption>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device) options.Add(new(device.ID, device.FriendlyName));
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not list audio devices: {ex.Message}");
        }
        return options.OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>The devices to record for a saved choice, without duplicates. Missing devices are skipped.</summary>
    internal static List<MMDevice> Resolve(MMDeviceEnumerator enumerator, DataFlow flow, string choice)
    {
        var devices = new List<MMDevice>();
        void Add(MMDevice? device)
        {
            if (device == null) return;
            if (devices.Any(d => d.ID == device.ID)) device.Dispose();
            else devices.Add(device);
        }

        switch (choice)
        {
            case None:
                break;
            case WindowsDefault:
                Add(GetDefault(enumerator, flow, Role.Multimedia));
                break;
            case Communications:
                Add(GetDefault(enumerator, flow, Role.Communications));
                break;
            case Auto:
                Add(GetDefault(enumerator, flow, Role.Communications));
                if (flow == DataFlow.Render) Add(GetDefault(enumerator, flow, Role.Multimedia));
                break;
            default:
                try
                {
                    var device = enumerator.GetDevice(choice);
                    if (device.State == DeviceState.Active) Add(device);
                    else device.Dispose();
                }
                catch (COMException)
                {
                    // Unplugged or removed; the recorder keeps checking and picks it up when it's back.
                }
                break;
        }
        return devices;
    }

    private static MMDevice? GetDefault(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            return enumerator.HasDefaultAudioEndpoint(flow, role) ? enumerator.GetDefaultAudioEndpoint(flow, role) : null;
        }
        catch (COMException)
        {
            return null;
        }
    }
}
