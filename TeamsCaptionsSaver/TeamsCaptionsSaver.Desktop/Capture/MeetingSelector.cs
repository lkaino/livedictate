namespace TeamsCaptionsSaver.Desktop.Capture;

/// <summary>A Teams window that shows meeting controls (the Leave button) and/or live captions.</summary>
internal sealed record MeetingCandidate(IntPtr Window, string Title, bool HasHangup, bool HasCaptions, bool OnHold)
{
    /// <summary>The window's UI Automation document root.</summary>
    public object? Document { get; init; }

    public object? Hangup { get; init; }
    public object? Captions { get; init; }
}

/// <summary>
/// Decides which meeting to follow when Teams has several open - typically one on hold in the
/// background while you're in another.
/// </summary>
internal static class MeetingSelector
{
    /// <summary>Button names of Teams' on-hold screen (English and Finnish Teams).</summary>
    private static readonly string[] ResumeNames = ["Resume", "Jatka"];

    /// <summary>
    /// The meeting to follow, or null when there is none or every meeting is on hold.
    /// <list type="number">
    /// <item>Meetings on hold are skipped. So are meetings we left for another one
    /// (<paramref name="superseded"/>), unless their window is in front - that's where you click Resume.</item>
    /// <item>A meeting window that wasn't there at the previous scan (<paramref name="known"/>) wins:
    /// Teams puts your other calls on hold when you join a meeting, so the newest one is where you are.</item>
    /// <item>Then the meeting already followed, so the choice doesn't flip between windows.</item>
    /// <item>Then a meeting with captions showing, then the one in the foreground.</item>
    /// </list>
    /// </summary>
    public static MeetingCandidate? Choose(IReadOnlyList<MeetingCandidate> candidates, IntPtr current, IntPtr foreground,
        IReadOnlySet<IntPtr>? known = null, IReadOnlySet<IntPtr>? superseded = null)
    {
        var active = candidates.Where(c => !c.OnHold && (c.HasHangup || c.HasCaptions) &&
            (superseded == null || !superseded.Contains(c.Window) || c.Window == foreground)).ToList();
        if (active.Count == 0) return null;

        var joined = known == null ? []
            : active.Where(c => c.HasHangup && c.Window != current && !known.Contains(c.Window)).ToList();
        return Best(joined, foreground)
            ?? active.FirstOrDefault(c => current != IntPtr.Zero && c.Window == current && c.HasHangup)
            ?? Best(active, foreground);
    }

    private static MeetingCandidate? Best(List<MeetingCandidate> list, IntPtr foreground) =>
        list.FirstOrDefault(c => c.HasHangup && c.HasCaptions)
        ?? list.FirstOrDefault(c => c.HasHangup && foreground != IntPtr.Zero && c.Window == foreground)
        ?? list.FirstOrDefault(c => c.HasHangup)
        ?? list.FirstOrDefault();

    /// <summary>
    /// True when a meeting window shows Teams' on-hold screen, which has a Resume button. Only buttons
    /// count: a caption or chat message saying "on hold" must not end the meeting.
    /// </summary>
    public static bool IsOnHold(UiNode tree) =>
        tree.Descendants().Any(n => n.ControlType == "Button" &&
            (ResumeNames.Any(r => n.Name.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                                  n.Name.StartsWith(r + " ", StringComparison.OrdinalIgnoreCase)) ||
             n.AutomationId.Contains("resume", StringComparison.OrdinalIgnoreCase) ||
             n.AutomationId.Contains("unhold", StringComparison.OrdinalIgnoreCase)));
}
