namespace TeamsCaptionsSaver.Desktop.Audio;

/// <summary>
/// A queue of mono samples from one capture source, written by its capture thread and read by the
/// recorder thread. When it is full, the oldest audio is dropped.
/// </summary>
public sealed class SourceBuffer
{
    private readonly object _lock = new();
    private readonly float[] _data;
    private int _start;
    private int _count;

    public SourceBuffer(int capacity) => _data = new float[capacity];

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            if (samples.Length > _data.Length) samples = samples[^_data.Length..];
            int overflow = _count + samples.Length - _data.Length;
            if (overflow > 0) DropUnlocked(overflow);

            int end = (_start + _count) % _data.Length;
            int first = Math.Min(samples.Length, _data.Length - end);
            samples[..first].CopyTo(_data.AsSpan(end));
            samples[first..].CopyTo(_data);
            _count += samples.Length;
        }
    }

    /// <summary>Moves up to <c>dest.Length</c> samples into <paramref name="dest"/>; returns how many there were.</summary>
    public int Read(Span<float> dest)
    {
        lock (_lock)
        {
            int n = Math.Min(dest.Length, _count);
            int first = Math.Min(n, _data.Length - _start);
            _data.AsSpan(_start, first).CopyTo(dest);
            _data.AsSpan(0, n - first).CopyTo(dest[first..]);
            DropUnlocked(n);
            return n;
        }
    }

    public void Drop(int n)
    {
        lock (_lock) DropUnlocked(Math.Clamp(n, 0, _count));
    }

    private void DropUnlocked(int n)
    {
        _start = (_start + n) % _data.Length;
        _count -= n;
    }
}

/// <summary>
/// Mixes capture sources into one interleaved 48 kHz stream whose length follows the wall clock,
/// so the microphone and the speaker audio stay in step with each other and with the transcript:
/// <list type="bullet">
/// <item>A source with nothing to give contributes silence. Windows stops delivering loopback audio
/// while nothing is playing, so this is normal, not an error.</item>
/// <item>Sound cards run slightly faster or slower than the wall clock. A source whose backlog drifts
/// out of range is read 0.1% faster or slower (an inaudible stretch) until it is back in range.</item>
/// <item>A source that gets far ahead (after a stall) skips its oldest audio.</item>
/// </list>
/// </summary>
public sealed class AudioMixer
{
    public const int SampleRate = 48000;

    /// <summary>Backlog above this (after the read) makes a source play slightly faster.</summary>
    internal const int HighWater = SampleRate / 4; // 250 ms

    /// <summary>Backlog below this (after the read) makes a source play slightly slower.</summary>
    internal const int LowWater = SampleRate / 20; // 50 ms

    /// <summary>Backlog above this is dropped outright.</summary>
    internal const int MaxBacklog = SampleRate * 2;

    private readonly object _lock = new();
    private readonly List<(SourceBuffer Buffer, int Channel)> _sources = [];
    private float[] _scratch = new float[SampleRate];

    public AudioMixer(int channels)
    {
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels));
        Channels = channels;
    }

    public int Channels { get; }

    /// <summary>Adds a source to the given output channel (0 = left/mono, 1 = right).</summary>
    public void AddSource(SourceBuffer buffer, int channel)
    {
        lock (_lock) _sources.Add((buffer, Math.Min(channel, Channels - 1)));
    }

    public void RemoveSource(SourceBuffer buffer)
    {
        lock (_lock) _sources.RemoveAll(s => s.Buffer == buffer);
    }

    /// <summary>Writes exactly <paramref name="frames"/> interleaved frames to <paramref name="output"/>.</summary>
    public void Mix(float[] output, int frames)
    {
        int total = frames * Channels;
        Array.Clear(output, 0, total);
        lock (_lock)
        {
            foreach (var (buffer, channel) in _sources)
            {
                int backlog = buffer.Count;
                if (backlog > MaxBacklog + frames)
                {
                    buffer.Drop(backlog - frames - HighWater);
                    backlog = buffer.Count;
                }

                // 0.1% faster or slower; short reads (under 1000 frames) are never corrected.
                int correction = frames / 1000;
                int want = correction == 0 ? frames
                    : backlog - frames > HighWater ? frames + correction
                    : backlog >= frames && backlog - frames < LowWater ? frames - correction
                    : frames;
                if (_scratch.Length < want) _scratch = new float[want];

                int got = buffer.Read(_scratch.AsSpan(0, want));
                if (got == want && want != frames)
                {
                    // Stretch or squeeze by repeating or skipping one sample in every thousand.
                    for (int i = 0; i < frames; i++)
                        output[i * Channels + channel] += _scratch[(int)((long)i * want / frames)];
                }
                else
                {
                    // Anything the source didn't have stays silent.
                    for (int i = 0; i < Math.Min(got, frames); i++)
                        output[i * Channels + channel] += _scratch[i];
                }
            }
        }
        for (int i = 0; i < total; i++) output[i] = Math.Clamp(output[i], -1f, 1f);
    }
}
