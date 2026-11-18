using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;

namespace TeamsCaptionsSaver.Desktop.Audio;

public sealed record AudioRecorderOptions(
    string Folder,
    string BaseName,
    string Title,
    string MicrophoneChoice,
    string OutputChoice,
    int Channels,
    int BitrateKbps,
    TimeSpan PartLength);

/// <summary>What the recorder is doing, for the status line. Replaced as a whole, so it's safe to read from any thread.</summary>
public sealed record AudioRecorderStatus(
    bool Running,
    int Part,
    TimeSpan Recorded,
    long Bytes,
    int Microphones,
    int Outputs,
    string? Error,
    IReadOnlyList<AudioPart> Parts);

/// <summary>
/// Records the microphone and the speaker output (loopback, i.e. what Teams plays) for one meeting
/// into Ogg Opus parts. In stereo, the microphone goes left and the speaker output right.
/// The audio follows the wall clock (silence fills gaps), so a part that starts at 14:10:00 has what
/// was said at 14:13:25 exactly 3:25 in. Parts start on round times (see <see cref="OpusChunkWriter.NextBoundary"/>).
/// Runs on its own MTA thread, which owns all the WASAPI objects.
/// </summary>
public sealed class MeetingAudioRecorder : IDisposable
{
    /// <summary>How far output trails the wall clock, so each source has its latest audio delivered.</summary>
    private const int LagFrames = AudioMixer.SampleRate * 15 / 100;

    /// <summary>A bigger jump in the clock (sleep/hibernate) is skipped instead of filled with silence.</summary>
    private const int MaxGapFrames = AudioMixer.SampleRate * 5;

    private static readonly TimeSpan DeviceCheckInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly AudioRecorderOptions _options;
    private readonly Dictionary<string, string> _reportedErrors = [];
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _paused;
    private volatile AudioRecorderStatus _status = new(false, 0, TimeSpan.Zero, 0, 0, 0, null, []);

    public MeetingAudioRecorder(AudioRecorderOptions options) => _options = options;

    /// <summary>While paused, silence is recorded so the timeline still matches the transcript.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public AudioRecorderStatus Status => _status;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Audio recorder" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Stops recording and finishes the current file.</summary>
    public void Stop()
    {
        _stop = true;
        if (_thread != null && !_thread.Join(TimeSpan.FromSeconds(10)))
            Log.Write("The audio recorder didn't stop in time; the last part may be incomplete.");
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Run()
    {
        var mixer = new AudioMixer(_options.Channels);
        var sources = new List<CaptureSource>();
        OpusChunkWriter? writer = null;
        string? error = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var note = _options.Channels == 2
                ? "Left channel: my microphone. Right channel: other people (speaker output)."
                : "Microphone and speaker output mixed together.";
            writer = new OpusChunkWriter(_options.Folder, _options.BaseName, _options.Title, _options.Channels,
                _options.BitrateKbps, note);

            UpdateSources(enumerator, mixer, sources);
            var clock = Stopwatch.StartNew();
            var startedAt = DateTime.Now;
            var lastDeviceCheck = TimeSpan.Zero;
            var lastFlush = TimeSpan.Zero;
            long written = 0;
            long offset = 0; // frames of wall-clock time skipped (sleep)
            var buffer = new float[AudioMixer.SampleRate * mixer.Channels];

            // Frame n of the recording was captured at this local time.
            DateTime TimeOf(long frame) => startedAt.AddSeconds((double)(frame + offset) / AudioMixer.SampleRate);
            long partEnd = 0; // frame where the next part starts
            DateTime partEndTime = default;
            void StartPart(DateTime start)
            {
                writer.StartPart(start);
                partEndTime = OpusChunkWriter.NextBoundary(start, _options.PartLength);
                partEnd = written + Math.Max(1, (long)Math.Round((partEndTime - start).TotalSeconds * AudioMixer.SampleRate));
            }
            StartPart(TimeOf(0));

            while (!_stop)
            {
                Thread.Sleep(100);
                var now = clock.Elapsed;
                long target = (long)(now.TotalSeconds * AudioMixer.SampleRate) - LagFrames - offset;

                if (target - written > MaxGapFrames)
                {
                    Log.Write($"Audio clock jumped {(target - written) / AudioMixer.SampleRate} s (sleep?); skipping the gap.");
                    offset += target - written;
                    target = written;
                    // The times in the file name must stay true, so continue in a new part.
                    StartPart(TimeOf(written));
                }

                while (written < target)
                {
                    if (written >= partEnd) StartPart(partEndTime);
                    int frames = (int)Math.Min(Math.Min(target - written, AudioMixer.SampleRate), partEnd - written);
                    mixer.Mix(buffer, frames);
                    if (_paused) Array.Clear(buffer, 0, frames * mixer.Channels);
                    writer.Write(buffer, frames);
                    written += frames;
                }

                if (now - lastDeviceCheck >= DeviceCheckInterval)
                {
                    lastDeviceCheck = now;
                    UpdateSources(enumerator, mixer, sources);
                }
                if (now - lastFlush >= FlushInterval)
                {
                    lastFlush = now;
                    writer.Flush();
                }

                _status = new(true, writer.Part, TimeSpan.FromSeconds((double)written / AudioMixer.SampleRate),
                    writer.TotalBytes, sources.Count(s => !s.IsLoopback), sources.Count(s => s.IsLoopback), null,
                    writer.Parts);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Write($"Audio recording failed: {ex.Message}");
        }
        finally
        {
            foreach (var source in sources) source.Dispose();
            try
            {
                writer?.Dispose();
                if (writer?.Parts.Count > 0)
                    Log.Write($"Audio saved: {string.Join(", ", writer.Parts.Select(p => Path.GetFileName(p.Path)))}");
            }
            catch (Exception ex)
            {
                error ??= ex.Message;
                Log.Write($"Could not finish the audio file: {ex.Message}");
            }
            var last = _status;
            _status = last with { Running = false, Error = error, Parts = writer?.Parts ?? last.Parts };
        }
    }

    /// <summary>
    /// Starts the devices the settings ask for and stops the ones no longer wanted - so recording
    /// follows a headset being plugged in or the Windows default device changing mid-meeting.
    /// </summary>
    private void UpdateSources(MMDeviceEnumerator enumerator, AudioMixer mixer, List<CaptureSource> sources)
    {
        var wanted = new List<(MMDevice Device, bool Loopback)>();
        wanted.AddRange(AudioDevices.Resolve(enumerator, DataFlow.Capture, _options.MicrophoneChoice).Select(d => (d, false)));
        wanted.AddRange(AudioDevices.Resolve(enumerator, DataFlow.Render, _options.OutputChoice).Select(d => (d, true)));
        try
        {
            var wantedKeys = wanted.Select(w => CaptureSource.KeyFor(w.Device, w.Loopback)).ToHashSet();
            foreach (var source in sources.Where(s => s.Failed || !wantedKeys.Contains(s.Key)).ToList())
            {
                if (!source.Failed) Log.Write($"Stopped recording '{source.Name}' (device changed).");
                mixer.RemoveSource(source.Buffer);
                source.Dispose();
                sources.Remove(source);
            }

            foreach (var (device, loopback) in wanted)
            {
                var key = CaptureSource.KeyFor(device, loopback);
                if (sources.Any(s => s.Key == key)) continue;
                try
                {
                    var source = CaptureSource.Start(device, loopback);
                    // Stereo: microphone left, speaker output right. Mono: everything in one channel.
                    mixer.AddSource(source.Buffer, loopback ? 1 : 0);
                    sources.Add(source);
                    _reportedErrors.Remove(key);
                    Log.Write($"Recording {(loopback ? "speaker output" : "microphone")}: {device.FriendlyName}");
                }
                catch (Exception ex)
                {
                    // Retried on every check; only log a given problem once.
                    if (!_reportedErrors.TryGetValue(key, out var previous) || previous != ex.Message)
                        Log.Write($"Could not record '{device.FriendlyName}': {ex.Message}");
                    _reportedErrors[key] = ex.Message;
                }
            }
        }
        finally
        {
            foreach (var (device, _) in wanted) device.Dispose();
        }
    }
}
