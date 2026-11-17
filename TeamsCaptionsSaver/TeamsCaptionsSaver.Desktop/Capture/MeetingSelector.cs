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
    /// <summary>
    /// The meeting to follow, or null when there is none or every meeting is on hold.
    /// The meeting already being followed wins while it's still active, so the choice doesn't flip
    /// between windows; then a meeting with captions showing, then the one in the foreground.
    /// </summary>
    public static MeetingCandidate? Choose(IReadOnlyList<MeetingCandidate> candidates, IntPtr current, IntPtr foreground)
    {
        var active = candidates.Where(c => !c.OnHold && (c.HasHangup || c.HasCaptions)).ToList();
        if (active.Count == 0) return null;
        return active.FirstOrDefault(c => current != IntPtr.Zero && c.Window == current && c.HasHangup)
            ?? active.FirstOrDefault(c => c.HasHangup && c.HasCaptions)
            ?? active.FirstOrDefault(c => c.HasHangup && foreground != IntPtr.Zero && c.Window == foreground)
            ?? active.FirstOrDefault(c => c.HasHangup)
            ?? active[0];
    }

    /// <summary>
    /// True when a meeting window shows Teams' on-hold screen, which has a Resume button. Only buttons
    /// count: a caption or chat message saying "on hold" must not end the meeting.
    /// </summary>
    public static bool IsOnHold(UiNode tree) =>
        tree.Descendants().Any(n => n.ControlType == "Button" &&
            (n.Name.Equals("Resume", StringComparison.OrdinalIgnoreCase) ||
             n.Name.StartsWith("Resume ", StringComparison.OrdinalIgnoreCase) ||
             n.AutomationId.Contains("resume", StringComparison.OrdinalIgnoreCase)));
}
