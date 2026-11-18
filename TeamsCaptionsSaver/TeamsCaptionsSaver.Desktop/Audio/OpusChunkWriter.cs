using System.IO;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace TeamsCaptionsSaver.Desktop.Audio;

/// <summary>One audio file and the local time its first sample was recorded.</summary>
public sealed record AudioPart(string Path, DateTime Start);

/// <summary>
/// Writes 48 kHz audio as Ogg Opus files ("parts"), each named after the time it starts
/// (<c>..._audio_14-10-00.ogg</c>), so a transcript line at 14:13:25 is 3:25 into that file.
/// The caller decides when a part starts (<see cref="StartPart"/>). Ogg pages reach the disk as they
/// fill (every few seconds) and <see cref="Flush"/> pushes them out, so even the part being written
/// survives a crash, playable up to the last page.
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

    private FileStream? _stream;
    private OpusOggWriteStream? _ogg;
    private long _finishedBytes;
    private AudioPart[] _parts = [];

    public OpusChunkWriter(string folder, string baseName, string title, int channels, int bitrateKbps,
        string channelNote = "")
    {
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels));
        _folder = folder;
        _baseName = baseName;
        _title = title;
        _channels = channels;
        _channelNote = channelNote;
        if (channels == 2) bitrateKbps = Math.Max(bitrateKbps, MinStereoBitrateKbps);
        _bitrate = Math.Clamp(bitrateKbps, 6, 256) * 1000;
    }

    /// <summary>Number of the part being written (1, 2, ...), or 0 before the first one.</summary>
    public int Part => _parts.Length;

    public string? CurrentPath => _parts.LastOrDefault()?.Path;

    /// <summary>Every part so far, oldest first. A new array each time a part starts, so safe to hand to other threads.</summary>
    public IReadOnlyList<AudioPart> Parts => _parts;

    /// <summary>Bytes in finished parts plus what has been handed to the current file.</summary>
    public long TotalBytes => _finishedBytes + (_stream?.Length ?? 0);

    /// <summary>Writes <paramref name="frames"/> interleaved frames to the current part.</summary>
    public void Write(float[] interleaved, int frames)
    {
        if (_ogg == null) throw new InvalidOperationException("No part started.");
        _ogg.WriteSamples(interleaved, 0, frames * _channels);
    }

    /// <summary>Pushes everything written so far to disk.</summary>
    public void Flush() => _stream?.Flush(flushToDisk: true);

    /// <summary>Finishes the current part and starts a new one whose first sample is at <paramref name="start"/>.</summary>
    public void StartPart(DateTime start)
    {
        FinishPart();
        Directory.CreateDirectory(_folder);

        // Never overwrite: if a file with this name exists (app restarted within the second), add a number.
        var name = $"{_baseName}_audio_{start:HH-mm-ss}";
        var path = Path.Combine(_folder, name + ".ogg");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(_folder, $"{name}_{n}.ogg");

        var encoder = OpusCodecFactory.CreateEncoder(AudioMixer.SampleRate, _channels,
            OpusApplication.OPUS_APPLICATION_VOIP, null);
        encoder.Bitrate = _bitrate;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        encoder.UseVBR = true;
        encoder.Complexity = 5;

        var tags = new OpusTags();
        tags.Fields["TITLE"] = $"{_title} ({start:HH:mm:ss})";
        tags.Fields["DATE"] = start.ToString("yyyy-MM-dd HH:mm:ss");
        if (_channelNote.Length > 0) tags.Fields["COMMENT"] = _channelNote;

        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _ogg = new OpusOggWriteStream(encoder, _stream, tags, AudioMixer.SampleRate, 5, leaveOpen: true);
        _parts = [.. _parts, new AudioPart(path, start)];
        Log.Write($"Recording audio to {Path.GetFileName(path)}");
    }

    /// <summary>
    /// When the part that starts at <paramref name="now"/> should end: the next multiple of
    /// <paramref name="length"/> since midnight (14:00, 14:10, ... for 10 minutes), so file names are
    /// round times. A part never gets shorter than a minute; it runs on to the boundary after.
    /// </summary>
    public static DateTime NextBoundary(DateTime now, TimeSpan length)
    {
        if (length < TimeSpan.FromMinutes(1)) return now + length;
        var sinceMidnight = now - now.Date;
        var boundary = now.Date + length * (Math.Floor(sinceMidnight / length) + 1);
        return boundary - now < TimeSpan.FromMinutes(1) ? boundary + length : boundary;
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
