using System.IO;
using TeamsCaptionsSaver.Desktop.Audio;
using TeamsCaptionsSaver.Desktop.Models;
using TeamsCaptionsSaver.Desktop.Transcript;
using Xunit;

namespace TeamsCaptionsSaver.Tests;

public class SaveLayoutTests
{
    private static TranscriptSession Session(string title = "Weekly sync") => new(s => s) { Title = title };

    [Fact]
    public void Meetings_go_in_a_folder_per_title_with_a_folder_per_date()
    {
        var s = Session();
        Assert.Equal(Path.Combine("Weekly sync", s.StartedAt.ToString("yyyy-MM-dd")),
            TranscriptExporter.BuildSubfolder("{title}/{date}", s));
        Assert.Equal(Path.Combine("out", "Weekly sync", s.StartedAt.ToString("yyyy-MM-dd")),
            TranscriptExporter.FolderFor(s, "out", "{title}/{date}"));
        Assert.Equal("out", TranscriptExporter.FolderFor(s, "out", ""));
    }

    [Fact]
    public void A_slash_or_dots_in_the_title_never_create_or_leave_folders()
    {
        var s = Session("Q4 / planning");
        Assert.Equal("Q4 _ planning", TranscriptExporter.BuildSubfolder("{title}", s));
        Assert.Equal("", TranscriptExporter.BuildSubfolder("{title}", Session("..")));
    }

    [Fact]
    public void Audio_parts_start_on_round_times_and_never_last_under_a_minute()
    {
        var ten = TimeSpan.FromMinutes(10);
        Assert.Equal(new DateTime(2026, 11, 18, 14, 10, 0), OpusChunkWriter.NextBoundary(new DateTime(2026, 11, 18, 14, 3, 25), ten));
        Assert.Equal(new DateTime(2026, 11, 18, 14, 20, 0), OpusChunkWriter.NextBoundary(new DateTime(2026, 11, 18, 14, 10, 0), ten));
        Assert.Equal(new DateTime(2026, 11, 18, 14, 20, 0), OpusChunkWriter.NextBoundary(new DateTime(2026, 11, 18, 14, 9, 30), ten));
        Assert.Equal(new DateTime(2026, 11, 19, 0, 0, 0), OpusChunkWriter.NextBoundary(new DateTime(2026, 11, 18, 23, 55, 0), ten));
    }

    [Fact]
    public void A_transcript_line_points_to_the_audio_file_and_position()
    {
        var s = Session();
        s.AudioParts =
        [
            new AudioPart(@"C:\x\2026-11-18_14-03_Weekly sync_audio_14-03-07.ogg", new DateTime(2026, 11, 18, 14, 3, 7)),
            new AudioPart(@"C:\x\2026-11-18_14-03_Weekly sync_audio_14-10-00.ogg", new DateTime(2026, 11, 18, 14, 10, 0)),
        ];

        // 2 s early, because captions appear just after the words.
        Assert.Equal("14-10-00 @ 3:23", TranscriptExporter.AudioReference(s, new DateTime(2026, 11, 18, 14, 13, 25)));
        Assert.Equal("14-03-07 @ 6:52", TranscriptExporter.AudioReference(s, new DateTime(2026, 11, 18, 14, 10, 1)));
        Assert.Equal("14-03-07 @ 0:00", TranscriptExporter.AudioReference(s, new DateTime(2026, 11, 18, 14, 3, 8)));
        Assert.Null(TranscriptExporter.AudioReference(s, new DateTime(2026, 11, 18, 14, 0, 0)));
        Assert.Null(TranscriptExporter.AudioReference(Session(), DateTime.Now));
    }

    [Fact]
    public void Saved_transcripts_show_the_audio_position_of_each_line()
    {
        var s = Session();
        s.AudioParts = [new AudioPart(Path.Combine("x", "Weekly sync_audio_00-00-00.ogg"), DateTime.Now.AddMinutes(-5))];
        s.Merge([new ParsedCaption("c1", "Bob", "Hi")]);

        Assert.Matches(@"\] Bob: Hi   🔊 00-00-00 @ 4:5\d", TranscriptExporter.ToText(s));
        Assert.Contains("🔊 00-00-00 @ 4:5", TranscriptExporter.ToMarkdown(s));
        Assert.Contains("\"positionSeconds\": 29", TranscriptExporter.ToJson(s));
        Assert.Contains("\"Weekly sync_audio_00-00-00.ogg\",\"4:5", TranscriptExporter.ToCsv(s));
    }
}
