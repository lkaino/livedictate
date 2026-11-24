using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TeamsCaptionsSaver.Desktop.Audio;

/// <summary>
/// Measures the sound level on every microphone and output device, so the settings can show which
/// device is hearing you or playing Teams. Runs on its own MTA thread while the settings are open.
/// <para>
/// Windows only meters a microphone while some app has it open, so each microphone is opened here
/// too (the audio itself is thrown away). Output devices are metered without opening anything.
/// </para>
/// </summary>
public sealed class AudioLevelMonitor : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    private readonly HashSet<string> _reportedErrors = [];
    private volatile bool _stop;
    private volatile IReadOnlyDictionary<string, float> _levels = new Dictionary<string, float>();

    /// <summary>Peak level (0-1) per <see cref="Key"/>; a key that's missing means silence.</summary>
    public IReadOnlyDictionary<string, float> Levels => _levels;

    /// <summary>Key for a device ID or a special choice such as <see cref="AudioDevices.Auto"/>.</summary>
    public static string Key(bool output, string choice) => (output ? "out:" : "in:") + choice;

    public AudioLevelMonitor()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Audio level monitor" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>Stops in the background (closing devices can take a moment), without blocking the caller.</summary>
    public void Dispose() => _stop = true;

    private void Run()
    {
        var devices = new List<(MMDevice Device, bool Output)>();
        var openMics = new Dictionary<string, (MMDevice Device, WasapiCapture Capture)>();
        var specials = new Dictionary<string, string[]>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var lastRefresh = DateTime.MinValue;
            while (!_stop)
            {
                if (DateTime.UtcNow - lastRefresh > RefreshInterval)
                {
                    lastRefresh = DateTime.UtcNow;
                    Refresh(enumerator, devices, openMics, specials);
                }

                var levels = new Dictionary<string, float>();
                foreach (var (device, output) in devices)
                {
                    try
                    {
                        levels[Key(output, device.ID)] = device.AudioMeterInformation.MasterPeakValue;
                    }
                    catch (Exception)
                    {
                        // Unplugged since the last refresh; the next refresh drops it.
                    }
                }
                foreach (var (key, keys) in specials)
                    levels[key] = keys.Select(k => levels.GetValueOrDefault(k)).DefaultIfEmpty(0f).Max();
                _levels = levels;

                Thread.Sleep(80);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Audio level monitor failed: {ex.Message}");
        }
        finally
        {
            foreach (var (device, _) in devices) device.Dispose();
            foreach (var (device, capture) in openMics.Values) Close(device, capture);
        }
    }

    private void Refresh(MMDeviceEnumerator enumerator, List<(MMDevice Device, bool Output)> devices,
        Dictionary<string, (MMDevice Device, WasapiCapture Capture)> openMics, Dictionary<string, string[]> specials)
    {
        foreach (var (device, _) in devices) device.Dispose();
        devices.Clear();
        foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                devices.Add((device, flow == DataFlow.Render));

        // Keep every microphone open so Windows meters it.
        var micIds = devices.Where(d => !d.Output).Select(d => d.Device.ID).ToHashSet();
        foreach (var id in openMics.Keys.Where(id => !micIds.Contains(id)).ToList())
        {
            Close(openMics[id].Device, openMics[id].Capture);
            openMics.Remove(id);
        }
        foreach (var id in micIds.Where(id => !openMics.ContainsKey(id)))
        {
            MMDevice? device = null;
            try
            {
                device = enumerator.GetDevice(id); // its own instance: the capture uses it until closed
                var capture = new WasapiCapture(device);
                capture.StartRecording(); // the samples aren't read; only the meter matters
                openMics[id] = (device, capture);
            }
            catch (Exception ex)
            {
                device?.Dispose();
                if (_reportedErrors.Add(id)) Log.Write($"Can't show the level of '{id}': {ex.Message}");
            }
        }

        // What the special choices ("Default microphone" and so on) mean right now.
        specials.Clear();
        foreach (var (output, choices) in new[]
                 {
                     (false, new[] { AudioDevices.Auto, AudioDevices.WindowsDefault, AudioDevices.Communications }),
                     (true, new[] { AudioDevices.Auto, AudioDevices.WindowsDefault, AudioDevices.Communications }),
                 })
        {
            foreach (var choice in choices)
            {
                var resolved = AudioDevices.Resolve(enumerator, output ? DataFlow.Render : DataFlow.Capture, choice);
                specials[Key(output, choice)] = resolved.Select(d => Key(output, d.ID)).ToArray();
                foreach (var d in resolved) d.Dispose();
            }
        }
    }

    private static void Close(MMDevice device, WasapiCapture capture)
    {
        try
        {
            capture.Dispose(); // stops recording and waits for NAudio's capture thread
        }
        catch (Exception)
        {
            // Already gone.
        }
        device.Dispose();
    }
}
