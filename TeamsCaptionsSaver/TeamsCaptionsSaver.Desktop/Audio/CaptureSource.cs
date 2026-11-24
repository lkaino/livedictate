using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TeamsCaptionsSaver.Desktop.Audio;

/// <summary>
/// Captures one device - a microphone, or the loopback of a speaker/headset - as mono 48 kHz samples
/// into a <see cref="SourceBuffer"/>. Create, use and dispose it on an MTA thread (the recorder thread).
/// </summary>
internal sealed class CaptureSource : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly SampleConverter _converter;
    private volatile bool _failed;
    private volatile bool _disposing;

    private CaptureSource(MMDevice device, bool loopback, string key, WaveFormat? format)
    {
        Key = key;
        Name = device.FriendlyName;
        IsLoopback = loopback;
        _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        try
        {
            if (format != null) _capture.WaveFormat = format;
            _converter = new SampleConverter(_capture.WaveFormat, AudioMixer.SampleRate);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
        }
        catch
        {
            _capture.Dispose();
            throw;
        }
    }

    /// <summary>Identifies the device and direction, to tell whether the wanted devices changed.</summary>
    public string Key { get; }
    public string Name { get; }
    public bool IsLoopback { get; }
    public SourceBuffer Buffer { get; } = new(AudioMixer.MaxBacklog * 2);

    /// <summary>True when capture stopped on its own (device unplugged or disabled).</summary>
    public bool Failed => _failed;

    public static string KeyFor(MMDevice device, bool loopback) => (loopback ? "out:" : "in:") + device.ID;

    public static CaptureSource Start(MMDevice device, bool loopback)
    {
        var key = KeyFor(device, loopback);
        try
        {
            // Let Windows convert to 48 kHz mono float (NAudio asks for AUTOCONVERTPCM in shared mode).
            return new CaptureSource(device, loopback, key, WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, 1));
        }
        catch (Exception ex)
        {
            Log.Write($"'{device.FriendlyName}' refused 48 kHz mono ({ex.Message}); converting it here instead.");
            return new CaptureSource(device, loopback, key, format: null);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0) Buffer.Write(_converter.Convert(e.Buffer, e.BytesRecorded));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposing) return;
        _failed = true;
        Log.Write($"Audio from '{Name}' stopped{(e.Exception != null ? $": {e.Exception.Message}" : "")}.");
    }

    public void Dispose()
    {
        _disposing = true;
        try
        {
            _capture.StopRecording();
        }
        catch
        {
            // Already stopped.
        }
        _capture.Dispose();
    }
}

/// <summary>
/// Turns captured bytes in the device's format into mono float samples at the output rate.
/// Only used in full when Windows didn't agree to deliver 48 kHz mono float itself.
/// </summary>
internal sealed class SampleConverter
{
    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;
    private readonly double _step;
    private float[] _mono = [];
    private float[] _resampled = [];
    private double _position;
    private float _previous;

    public SampleConverter(WaveFormat format, int outputRate)
    {
        _channels = format.Channels;
        _bytesPerSample = format.BitsPerSample / 8;
        _isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        bool supported = _isFloat ? _bytesPerSample == 4
            : format.Encoding == WaveFormatEncoding.Pcm && _bytesPerSample is 2 or 3 or 4;
        if (!supported || _channels < 1)
            throw new NotSupportedException($"Unsupported audio format: {format}");
        _step = (double)format.SampleRate / outputRate;
    }

    public ReadOnlySpan<float> Convert(byte[] buffer, int bytes)
    {
        int frameBytes = _bytesPerSample * _channels;
        int frames = bytes / frameBytes;
        if (_mono.Length < frames) _mono = new float[frames];

        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            int at = f * frameBytes;
            for (int c = 0; c < _channels; c++, at += _bytesPerSample) sum += Sample(buffer, at);
            _mono[f] = sum / _channels;
        }

        if (_step == 1.0) return _mono.AsSpan(0, frames);
        return Resample(frames);
    }

    private float Sample(byte[] b, int i) => _isFloat
        ? BitConverter.ToSingle(b, i)
        : _bytesPerSample switch
        {
            2 => BitConverter.ToInt16(b, i) / 32768f,
            3 => ((b[i] << 8 | b[i + 1] << 16 | b[i + 2] << 24) >> 8) / 8388608f,
            _ => BitConverter.ToInt32(b, i) / 2147483648f,
        };

    /// <summary>Linear interpolation across calls; position 0 is the last sample of the previous call.</summary>
    private ReadOnlySpan<float> Resample(int frames)
    {
        int max = (int)(frames / _step) + 2;
        if (_resampled.Length < max) _resampled = new float[max];
        int n = 0;
        while (_position < frames)
        {
            int i = (int)_position;
            float a = i == 0 ? _previous : _mono[i - 1];
            float b = _mono[i];
            _resampled[n++] = a + (b - a) * (float)(_position - i);
            _position += _step;
        }
        _position -= frames;
        if (frames > 0) _previous = _mono[frames - 1];
        return _resampled.AsSpan(0, n);
    }
}
