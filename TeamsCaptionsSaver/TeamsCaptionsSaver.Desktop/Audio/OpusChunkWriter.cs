using System.IO;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace TeamsCaptionsSaver.Desktop.Audio;

/// <summary>
/// Writes 48 kHz audio as Ogg Opus files, starting a new file ("part") every <c>chunkLength</c>, so a
/// long meeting is saved piece by piece. Ogg pages reach the disk as they fill (every few seconds) and
/// <see cref="Flush"/> pushes them out, so even the part being written survives a crash, playable up to
/// the last page.
/// </summary>
public sealed class OpusChunkWriter : IDisposable
{
    /// <summary>
    /// Below this, Opus collapses stereo into near-mono, which would mix "me" and "others" back together
    /// (measured: 32 kbps gives identical channels, 44 kbps keeps them fully apart).
    /// </summary>
    public const int MinStereoBitrateKbps = 48;

    static OpusChunkWriter()
    {
        // Use the bundled managed encoder rather than probing for an opus DLL on the PATH.
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
    }

    private readonly string _folder;
    private readonly string _baseName;
    private readonly string _title;
    private readonly string _channelNote;
    private readonly int _channels;
    private readonly int _bitrate;
    private readonly long _framesPerPart;
    private readonly DateTime _startedAt;
    private readonly List<string> _files = [];

    private FileStream? _stream;
    private OpusOggWriteStream? _ogg;
    private long _framesInPart;
    private long _finishedBytes;

    public OpusChunkWriter(string folder, string baseName, string title, int channels, int bitrateKbps,
        TimeSpan partLength, string channelNote = "")
    {
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels));
        _folder = folder;
        _baseName = baseName;
        _title = title;
        _channels = channels;
        _channelNote = channelNote;
        if (channels == 2) bitrateKbps = Math.Max(bitrateKbps, MinStereoBitrateKbps);
        _bitrate = Math.Clamp(bitrateKbps, 6, 256) * 1000;
        _framesPerPart = Math.Max(1, (long)(partLength.TotalSeconds * AudioMixer.SampleRate));
        _startedAt = DateTime.Now;
    }

    /// <summary>Number of the part being written (1, 2, ...), or 0 before the first write.</summary>
    public int Part { get; private set; }

    public string? CurrentPath { get; private set; }

    public IReadOnlyList<string> Files => _files;

    /// <summary>Bytes in finished parts plus what has been handed to the current file.</summary>
    public long TotalBytes => _finishedBytes + (_stream?.Length ?? 0);

    /// <summary>Writes <paramref name="frames"/> interleaved frames, starting new parts as needed.</summary>
    public void Write(float[] interleaved, int frames)
    {
        int offset = 0;
        while (frames > 0)
        {
            if (_ogg == null || _framesInPart >= _framesPerPart) StartNextPart();
            int n = (int)Math.Min(frames, _framesPerPart - _framesInPart);
            _ogg!.WriteSamples(interleaved, offset * _channels, n * _channels);
            _framesInPart += n;
            offset += n;
            frames -= n;
        }
    }

    /// <summary>Pushes everything written so far to disk.</summary>
    public void Flush() => _stream?.Flush(flushToDisk: true);

    private void StartNextPart()
    {
        FinishPart();
        Directory.CreateDirectory(_folder);

        // Never overwrite: if a file with this name exists (same meeting recorded twice), skip ahead.
        string path;
        do
        {
            Part++;
            path = Path.Combine(_folder, $"{_baseName}_audio_{Part:000}.ogg");
        } while (File.Exists(path));

        var encoder = OpusCodecFactory.CreateEncoder(AudioMixer.SampleRate, _channels,
            OpusApplication.OPUS_APPLICATION_VOIP, null);
        encoder.Bitrate = _bitrate;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        encoder.UseVBR = true;
        encoder.Complexity = 5;

        var tags = new OpusTags();
        tags.Fields["TITLE"] = $"{_title} (part {Part})";
        tags.Fields["DATE"] = _startedAt.ToString("yyyy-MM-dd HH:mm");
        if (_channelNote.Length > 0) tags.Fields["COMMENT"] = _channelNote;

        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _ogg = new OpusOggWriteStream(encoder, _stream, tags, AudioMixer.SampleRate, 5, leaveOpen: true);
        _framesInPart = 0;
        CurrentPath = path;
        _files.Add(path);
        Log.Write($"Recording audio to {Path.GetFileName(path)}");
    }

    private void FinishPart()
    {
        if (_ogg == null || _stream == null) return;
        try
        {
            _ogg.Finish();
            _stream.Flush(flushToDisk: true);
            _finishedBytes += _stream.Length;
        }
        finally
        {
            _stream.Dispose();
            _ogg = null;
            _stream = null;
        }
    }

    /// <summary>Finishes the current part so it is a complete file.</summary>
    public void Dispose() => FinishPart();
}
