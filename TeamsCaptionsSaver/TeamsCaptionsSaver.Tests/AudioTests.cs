using System.IO;
using Concentus;
using Concentus.Oggfile;
using NAudio.Wave;
using TeamsCaptionsSaver.Desktop.Audio;
using Xunit;

namespace TeamsCaptionsSaver.Tests;

public class AudioTests
{
    private const int Rate = AudioMixer.SampleRate;

    private static float[] Constant(int count, float value) => Enumerable.Repeat(value, count).ToArray();

    [Fact]
    public void Source_buffer_wraps_around_and_drops_the_oldest_when_full()
    {
        var buffer = new SourceBuffer(5);
        buffer.Write([1, 2, 3]);
        var three = new float[3];
        Assert.Equal(2, buffer.Read(three.AsSpan(0, 2)));
        buffer.Write([4, 5, 6, 7]); // wraps; 5 slots hold 3,4,5,6,7
        buffer.Write([8]);          // full: 3 is dropped
        var all = new float[10];
        int n = buffer.Read(all);
        Assert.Equal([4f, 5, 6, 7, 8], all[..n]);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Stereo_mix_puts_the_microphone_left_and_the_speaker_right()
    {
        var mixer = new AudioMixer(2);
        var mic = new SourceBuffer(Rate * 4);
        var speaker = new SourceBuffer(Rate * 4);
        mixer.AddSource(mic, 0);
        mixer.AddSource(speaker, 1);
        int frames = Rate / 10;
        mic.Write(Constant(frames + AudioMixer.LowWater * 2, 0.25f));
        speaker.Write(Constant(frames + AudioMixer.LowWater * 2, -0.5f));

        var output = new float[frames * 2];
        mixer.Mix(output, frames);

        Assert.All(Enumerable.Range(0, frames), i =>
        {
            Assert.Equal(0.25f, output[2 * i]);
            Assert.Equal(-0.5f, output[2 * i + 1]);
        });
    }

    [Fact]
    public void A_source_without_audio_is_silent_and_partial_audio_comes_first()
    {
        var mixer = new AudioMixer(2);
        var mic = new SourceBuffer(Rate * 4);
        var speaker = new SourceBuffer(Rate * 4); // loopback while nothing plays: no data at all
        mixer.AddSource(mic, 0);
        mixer.AddSource(speaker, 1);
        int frames = 1000;
        mic.Write(Constant(400, 0.5f));

        var output = new float[frames * 2];
        mixer.Mix(output, frames);

        Assert.All(Enumerable.Range(0, 400), i => Assert.Equal(0.5f, output[2 * i]));
        Assert.All(Enumerable.Range(400, 600), i => Assert.Equal(0f, output[2 * i]));
        Assert.All(Enumerable.Range(0, frames), i => Assert.Equal(0f, output[2 * i + 1]));
    }

    [Fact]
    public void Mono_mix_adds_sources_together_and_clips()
    {
        var mixer = new AudioMixer(1);
        var a = new SourceBuffer(Rate);
        var b = new SourceBuffer(Rate);
        mixer.AddSource(a, 0);
        mixer.AddSource(b, 1); // no channel 1 in mono: goes to the only channel
        a.Write([0.25f, 0.75f]);
        b.Write([0.25f, 0.75f]);

        var output = new float[2];
        mixer.Mix(output, 2);

        Assert.Equal([0.5f, 1f], output);
    }

    [Fact]
    public void A_source_running_ahead_is_read_slightly_faster_until_back_in_range()
    {
        var mixer = new AudioMixer(1);
        var source = new SourceBuffer(Rate * 4);
        mixer.AddSource(source, 0);
        int frames = Rate / 10;
        source.Write(Constant(frames + AudioMixer.HighWater + 2000, 0.1f));
        var output = new float[frames];

        int before = source.Count;
        mixer.Mix(output, frames);
        // One sample in a thousand extra, never a jump.
        Assert.Equal(before - frames - frames / 1000, source.Count);
        Assert.All(output, s => Assert.Equal(0.1f, s));
    }

    [Theory]
    [InlineData(1.0003)] // sound card 300 ppm faster than the wall clock
    [InlineData(0.9997)] // 300 ppm slower
    public void Clock_drift_over_a_long_meeting_never_leaves_gaps_or_piles_up(double cardSpeed)
    {
        var mixer = new AudioMixer(1);
        var mic = new SourceBuffer(Rate * 4);
        mixer.AddSource(mic, 0);
        int frames = Rate / 10; // the recorder mixes every ~100 ms
        var output = new float[frames];
        double delivered = 0;

        // The recorder starts mixing 150 ms after capture starts.
        mic.Write(Constant(Rate * 15 / 100, 1f));
        for (int tick = 0; tick < 10 * 60 * 30; tick++) // 30 minutes
        {
            delivered += frames * cardSpeed;
            int packet = (int)delivered;
            delivered -= packet;
            mic.Write(Constant(packet, 1f));

            mixer.Mix(output, frames);
            Assert.True(Array.TrueForAll(output, s => s == 1f), $"gap at {tick / 10.0:0.0} s");
            Assert.InRange(mic.Count, 0, AudioMixer.HighWater + frames);
        }
    }

    [Fact]
    public void A_source_far_behind_the_clock_skips_its_oldest_audio()
    {
        var mixer = new AudioMixer(1);
        var source = new SourceBuffer(Rate * 4);
        mixer.AddSource(source, 0);
        int frames = Rate / 10;
        source.Write(Constant(Rate * 3, 0.1f)); // 3 s waiting, e.g. after the recorder thread stalled

        mixer.Mix(new float[frames], frames);

        Assert.True(source.Count <= AudioMixer.HighWater, $"backlog {source.Count}");
    }

    [Fact]
    public void Converter_downmixes_16_bit_stereo_and_resamples_44_1_to_48_kHz()
    {
        var converter = new SampleConverter(new WaveFormat(44100, 16, 2), Rate);
        int frames = 4410; // 100 ms
        var bytes = new byte[frames * 4];
        for (int f = 0; f < frames; f++)
        {
            BitConverter.GetBytes((short)16384).CopyTo(bytes, f * 4);  // left 0.5
            BitConverter.GetBytes((short)-16384).CopyTo(bytes, f * 4 + 2); // right -0.5 -> mono 0
        }

        int total = 0;
        for (int call = 0; call < 10; call++)
        {
            var output = converter.Convert(bytes, bytes.Length).ToArray();
            Assert.All(output, s => Assert.Equal(0f, s, 4));
            total += output.Length;
        }
        Assert.InRange(total, Rate - 2, Rate + 2); // 1 s in, 1 s out
    }

    [Fact]
    public void Converter_passes_48_kHz_mono_float_through_unchanged()
    {
        var converter = new SampleConverter(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1), Rate);
        float[] samples = [0.1f, -0.2f, 0.3f];
        var bytes = samples.SelectMany(BitConverter.GetBytes).ToArray();
        Assert.Equal(samples, converter.Convert(bytes, bytes.Length).ToArray());
    }

    [Fact]
    public void Writer_splits_audio_into_parts_that_decode_to_the_right_length()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tcs-audio-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var writer = new OpusChunkWriter(folder, "2026-11-17_14-00_Weekly sync", "Weekly sync", 2, 48,
                       TimeSpan.FromSeconds(4)))
            {
                var second = new float[Rate * 2];
                for (int i = 0; i < Rate; i++)
                {
                    second[2 * i] = 0.3f * MathF.Sin(i * 0.05f);
                    second[2 * i + 1] = 0.2f * MathF.Sin(i * 0.03f);
                }
                for (int s = 0; s < 10; s++) writer.Write(second, Rate);
                Assert.Equal(3, writer.Part);
            }

            var files = Directory.GetFiles(folder).Order().ToList();
            Assert.Equal(
                ["2026-11-17_14-00_Weekly sync_audio_001.ogg", "2026-11-17_14-00_Weekly sync_audio_002.ogg", "2026-11-17_14-00_Weekly sync_audio_003.ogg"],
                files.Select(Path.GetFileName));
            Assert.Equal([4.0, 4.0, 2.0], files.Select(DecodedSeconds));

            // A second recording with the same name continues the numbering instead of overwriting.
            using (var again = new OpusChunkWriter(folder, "2026-11-17_14-00_Weekly sync", "Weekly sync", 1, 24, TimeSpan.FromMinutes(1)))
            {
                again.Write(new float[Rate], Rate);
                Assert.EndsWith("_audio_004.ogg", again.CurrentPath);
            }
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static double DecodedSeconds(string path)
    {
        using var stream = File.OpenRead(path);
        var reader = new OpusOggReadStream(OpusCodecFactory.CreateDecoder(Rate, 2, null), stream);
        long samples = 0;
        while (reader.HasNextPacket)
            samples += reader.DecodeNextPacket()?.Length ?? 0;
        return Math.Round(samples / 2.0 / Rate, 1);
    }
}
