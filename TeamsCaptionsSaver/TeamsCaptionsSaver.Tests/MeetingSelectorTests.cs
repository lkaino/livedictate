using TeamsCaptionsSaver.Desktop.Capture;
using Xunit;

namespace TeamsCaptionsSaver.Tests;

public class MeetingSelectorTests
{
    private static readonly IntPtr WeeklyWindow = 100, StandupWindow = 200, MainTeamsWindow = 300;

    private static MeetingCandidate Meeting(IntPtr window, string title, bool captions = false, bool onHold = false,
        bool hangup = true) => new(window, title, hangup, captions, onHold);

    [Fact]
    public void A_meeting_on_hold_is_never_followed()
    {
        var onHold = Meeting(WeeklyWindow, "Weekly sync", onHold: true);
        Assert.Null(MeetingSelector.Choose([onHold], current: WeeklyWindow, foreground: WeeklyWindow));
    }

    [Fact]
    public void The_active_meeting_wins_over_one_on_hold()
    {
        var onHold = Meeting(WeeklyWindow, "Weekly sync", onHold: true);
        var active = Meeting(StandupWindow, "Standup");
        Assert.Same(active, MeetingSelector.Choose([onHold, active], current: WeeklyWindow, foreground: IntPtr.Zero));
    }

    [Fact]
    public void The_meeting_already_followed_stays_chosen_while_active()
    {
        var current = Meeting(WeeklyWindow, "Weekly sync");
        var other = Meeting(StandupWindow, "Standup", captions: true);
        Assert.Same(current, MeetingSelector.Choose([other, current], current: WeeklyWindow, foreground: StandupWindow));
    }

    [Fact]
    public void Otherwise_captions_then_the_foreground_window_decide()
    {
        var plain = Meeting(WeeklyWindow, "Weekly sync");
        var withCaptions = Meeting(StandupWindow, "Standup", captions: true);
        Assert.Same(withCaptions, MeetingSelector.Choose([plain, withCaptions], IntPtr.Zero, IntPtr.Zero));

        var foreground = Meeting(StandupWindow, "Standup");
        Assert.Same(foreground, MeetingSelector.Choose([plain, foreground], IntPtr.Zero, foreground: StandupWindow));
    }

    [Fact]
    public void A_newly_joined_meeting_wins_even_when_Teams_hold_screen_is_not_recognised()
    {
        // You're followed in Weekly sync, then join Standup: Teams holds Weekly sync in the background.
        var weekly = Meeting(WeeklyWindow, "Weekly sync", captions: true);
        var standup = Meeting(StandupWindow, "Standup");
        var known = new HashSet<IntPtr> { WeeklyWindow };
        Assert.Same(standup, MeetingSelector.Choose([weekly, standup], WeeklyWindow, WeeklyWindow, known));

        // Next scan: Standup is known and followed, so it stays.
        known.Add(StandupWindow);
        var superseded = new HashSet<IntPtr> { WeeklyWindow };
        Assert.Same(standup, MeetingSelector.Choose([weekly, standup], StandupWindow, WeeklyWindow, known, superseded));
    }

    [Fact]
    public void A_meeting_left_for_another_is_only_followed_again_from_its_own_window()
    {
        // Standup ended; Weekly sync is still on hold in the background.
        var weekly = Meeting(WeeklyWindow, "Weekly sync");
        var known = new HashSet<IntPtr> { WeeklyWindow };
        var superseded = new HashSet<IntPtr> { WeeklyWindow };
        Assert.Null(MeetingSelector.Choose([weekly], IntPtr.Zero, MainTeamsWindow, known, superseded));

        // You bring its window to the front to resume it.
        Assert.Same(weekly, MeetingSelector.Choose([weekly], IntPtr.Zero, WeeklyWindow, known, superseded));
    }

    [Fact]
    public void The_first_scan_treats_no_meeting_as_newly_joined()
    {
        var weekly = Meeting(WeeklyWindow, "Weekly sync");
        var standup = Meeting(StandupWindow, "Standup", captions: true);
        Assert.Same(standup, MeetingSelector.Choose([weekly, standup], IntPtr.Zero, IntPtr.Zero, known: null));
    }

    [Fact]
    public void A_window_with_only_captions_is_used_when_no_window_has_meeting_controls()
    {
        var captionsOnly = Meeting(MainTeamsWindow, "Weekly sync", captions: true, hangup: false);
        Assert.Same(captionsOnly, MeetingSelector.Choose([captionsOnly], IntPtr.Zero, IntPtr.Zero));
    }

    [Fact]
    public void On_hold_is_recognised_by_the_Resume_button_but_not_by_spoken_words()
    {
        UiNode Tree(params UiNode[] nodes) => new() { ControlType = "Document", Children = nodes.ToList() };

        Assert.True(MeetingSelector.IsOnHold(Tree(new UiNode { ControlType = "Button", Name = "Resume" })));
        Assert.True(MeetingSelector.IsOnHold(Tree(new UiNode { ControlType = "Button", Name = "Resume call" })));
        Assert.True(MeetingSelector.IsOnHold(Tree(new UiNode { ControlType = "Button", AutomationId = "resume-call-button" })));
        Assert.True(MeetingSelector.IsOnHold(Tree(new UiNode { ControlType = "Button", Name = "Jatka" })));

        Assert.False(MeetingSelector.IsOnHold(Tree(
            new UiNode { ControlType = "Button", Name = "Leave" },
            new UiNode { ControlType = "Text", Name = "Let's put the release on hold and resume next week." })));
    }
}
