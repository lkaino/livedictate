using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop.Capture;

public sealed record CaptureSnapshot(
    bool TeamsRunning,
    bool InMeeting,
    bool CaptionsVisible,
    string MeetingTitle,
    IReadOnlyList<ParsedCaption> Captions,
    int MeetingKey = 0);

/// <summary>
/// Reads live captions from the Microsoft Teams desktop client (new Teams, ms-teams.exe) through
/// Windows UI Automation. Teams renders its UI in WebView2, which exposes the page's accessibility
/// tree to UIA - so we can see the caption elements of the Teams page,
/// including the real speaker names Teams attaches to each caption.
/// </summary>
public sealed class TeamsCaptionReader : IDisposable
{
    private const string HangupButtonId = "hangup-button";
    private const string MoreButtonId = "callingButtons-showMoreBtn";
    private const string LanguageSpeechMenuId = "LanguageSpeechMenuControl-id";
    private const string CaptionsMenuItemId = "closed-captions-button";
    private const string CaptionsPanelDismissId = "captions-panel-dismiss-button";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan WindowRefreshInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FullScanInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleFullScanInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan AutoEnableDelay = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan AutoEnableRetry = TimeSpan.FromSeconds(20);
    private const int MaxAutoEnableAttempts = 3;

    private readonly CacheRequest _subtreeRequest;
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    private List<(IntPtr Hwnd, AutomationElement Root, string WindowTitle)> _documents = [];
    private DateTime _lastWindowRefresh = DateTime.MinValue;
    private DateTime _lastFullScan = DateTime.MinValue;

    private AutomationElement? _hangupButton;
    private AutomationElement? _captionsContainer;
    private AutomationElement? _meetingDocument;
    private IntPtr _meetingWindow;
    private string _meetingTitle = "";

    /// <summary>Changes whenever we start following a different meeting (e.g. one was put on hold).</summary>
    private int _meetingKey;
    private readonly HashSet<string> _reportedOnHold = [];

    private bool _wasInMeeting;
    private DateTime _meetingDetectedAt;
    private int _autoEnableAttempts;
    private DateTime _lastAutoEnableAttempt = DateTime.MinValue;

    public bool AutoEnableCaptions { get; set; }

    /// <summary>Raised on the background capture thread roughly once per second.</summary>
    public event Action<CaptureSnapshot>? SnapshotTaken;

    public TeamsCaptionReader()
    {
        _subtreeRequest = new CacheRequest
        {
            TreeScope = TreeScope.Subtree,
            TreeFilter = Automation.RawViewCondition,
            AutomationElementMode = AutomationElementMode.Full,
        };
        _subtreeRequest.Add(AutomationElement.NameProperty);
        _subtreeRequest.Add(AutomationElement.ClassNameProperty);
        _subtreeRequest.Add(AutomationElement.AutomationIdProperty);
        _subtreeRequest.Add(AutomationElement.ControlTypeProperty);
        _subtreeRequest.Add(AutomationElement.RuntimeIdProperty);
    }

    public void Start()
    {
        if (_thread != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Run(token)) { IsBackground = true, Name = "Teams caption reader" };
        // UIA clients should run on an MTA thread.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _thread = null;
        _cts = null;
    }

    public void Dispose() => Stop();

    private void Run(CancellationToken token)
    {
        Log.Write("Caption reader started.");
        while (!token.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log.Write($"Capture tick failed: {ex.GetType().Name}: {ex.Message}");
                ResetMeetingElements();
            }
            var elapsed = DateTime.UtcNow - started;
            var wait = PollInterval - elapsed;
            if (wait > TimeSpan.Zero) token.WaitHandle.WaitOne(wait);
        }
        Log.Write("Caption reader stopped.");
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        if (now - _lastWindowRefresh > WindowRefreshInterval)
        {
            _documents = FindTeamsDocuments();
            _lastWindowRefresh = now;
        }

        if (_documents.Count == 0)
        {
            ResetMeetingElements();
            UpdateMeetingState(false, now);
            SnapshotTaken?.Invoke(new CaptureSnapshot(IsTeamsRunning(), false, false, "", []));
            return;
        }

        if (_hangupButton != null && !IsAlive(_hangupButton)) _hangupButton = null;

        // A full scan is only needed while we're still looking for the meeting controls or the captions.
        var scanInterval = _wasInMeeting ? FullScanInterval : IdleFullScanInterval;
        if ((_hangupButton == null || _captionsContainer == null) && now - _lastFullScan > scanInterval)
        {
            FullScan();
            _lastFullScan = now;
        }

        IReadOnlyList<ParsedCaption> captions = [];
        if (_captionsContainer != null)
        {
            try
            {
                var cached = _captionsContainer.GetUpdatedCache(_subtreeRequest);
                captions = CaptionParser.Parse(ToNode(cached));
            }
            catch (ElementNotAvailableException)
            {
                Log.Write("Captions region disappeared (captions turned off or meeting ended).");
                _captionsContainer = null;
            }
        }

        bool inMeeting = _hangupButton != null || _captionsContainer != null;
        UpdateMeetingState(inMeeting, now);

        if (inMeeting && _captionsContainer == null && AutoEnableCaptions)
            MaybeAutoEnableCaptions(now);

        SnapshotTaken?.Invoke(new CaptureSnapshot(true, inMeeting, _captionsContainer != null,
            inMeeting ? _meetingTitle : "", captions, _meetingKey));
    }

    private void UpdateMeetingState(bool inMeeting, DateTime now)
    {
        if (inMeeting && !_wasInMeeting)
        {
            _meetingDetectedAt = now;
            _autoEnableAttempts = 0;
            Log.Write($"Meeting detected: '{_meetingTitle}'.");
        }
        else if (!inMeeting && _wasInMeeting)
        {
            Log.Write("Meeting ended.");
        }
        _wasInMeeting = inMeeting;
    }

    private void ResetMeetingElements()
    {
        _hangupButton = null;
        _captionsContainer = null;
        _meetingDocument = null;
        _meetingWindow = IntPtr.Zero;
    }

    // --- Locating Teams ----------------------------------------------------------------------

    private static bool IsTeamsRunning() => Process.GetProcessesByName("ms-teams").Length > 0;

    /// <summary>Finds the WebView2 render hosts inside every top-level Teams window.</summary>
    private static List<(IntPtr, AutomationElement, string)> FindTeamsDocuments()
    {
        var teamsPids = Process.GetProcessesByName("ms-teams").Select(p => (uint)p.Id).ToHashSet();
        var results = new List<(IntPtr, AutomationElement, string)>();
        if (teamsPids.Count == 0) return results;

        foreach (var top in Native.TopLevelWindows())
        {
            Native.GetWindowThreadProcessId(top, out var pid);
            if (!teamsPids.Contains(pid) || !Native.IsWindowVisible(top)) continue;
            var title = Native.GetTitle(top);

            foreach (var child in Native.ChildWindows(top))
            {
                if (Native.GetClass(child) != "Chrome_RenderWidgetHostHWND") continue;
                try
                {
                    results.Add((child, AutomationElement.FromHandle(child), title));
                }
                catch (Exception ex)
                {
                    Log.Write($"Could not attach to Teams window '{title}': {ex.Message}");
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Scans every Teams document for meetings (Leave button, live-captions region) and picks the one
    /// to follow. Meetings on hold are ignored, so a meeting waiting in the background neither keeps
    /// a finished meeting "running" nor lends it its name.
    /// </summary>
    private void FullScan()
    {
        var scanned = new List<(IntPtr Window, AutomationElement Root, string Title, UiNode Tree, SourceNode? Hangup)>();
        foreach (var (hostWindow, root, windowTitle) in _documents)
        {
            UiNode tree;
            try
            {
                tree = ToNode(root.GetUpdatedCache(_subtreeRequest));
            }
            catch (ElementNotAvailableException)
            {
                _lastWindowRefresh = DateTime.MinValue;
                continue;
            }
            scanned.Add((Native.GetAncestor(hostWindow, Native.GA_ROOT), root, windowTitle, tree, FindHangup(tree)));
        }

        bool anyHangup = _hangupButton != null || scanned.Any(s => s.Hangup != null);
        var candidates = new List<MeetingCandidate>();
        foreach (var (window, root, windowTitle, tree, hangup) in scanned)
        {
            var container = FindCaptionsContainer(tree, inMeeting: hangup != null || anyHangup);
            if (hangup == null && container == null) continue;
            var candidate = new MeetingCandidate(window, CleanTitle(windowTitle), hangup != null, container != null,
                MeetingSelector.IsOnHold(tree))
            {
                Document = root,
                Hangup = hangup?.Source,
                Captions = container?.Source,
            };
            candidates.Add(candidate);
            if (candidate.OnHold && _reportedOnHold.Add(candidate.Title))
                Log.Write($"Meeting on hold, not following it: '{candidate.Title}'.");
        }
        foreach (var title in _reportedOnHold.Where(t => !candidates.Any(c => c.OnHold && c.Title == t)).ToList())
            _reportedOnHold.Remove(title);

        var chosen = MeetingSelector.Choose(candidates, _meetingWindow, Native.GetForegroundWindow());
        if (chosen == null)
        {
            // The meeting we follow was put on hold: stop following it. Otherwise keep what we have;
            // Tick drops elements that disappear (meeting ended, captions turned off).
            if (candidates.Any(c => c.OnHold && c.Window == _meetingWindow))
            {
                Log.Write($"'{_meetingTitle}' was put on hold.");
                ResetMeetingElements();
            }
            return;
        }

        bool otherWindow = chosen.Window != _meetingWindow;
        if (_meetingTitle.Length > 0 && chosen.Title != _meetingTitle)
        {
            _meetingKey++;
            _autoEnableAttempts = 0;
            _meetingDetectedAt = DateTime.UtcNow;
            Log.Write($"Now following meeting '{chosen.Title}' (was '{_meetingTitle}').");
        }

        _meetingDocument = (AutomationElement?)chosen.Document;
        _meetingWindow = chosen.Window;
        _meetingTitle = chosen.Title;
        _hangupButton = (AutomationElement?)chosen.Hangup;
        // Captions belong to one meeting window: never keep another window's captions.
        if (_captionsContainer == null || otherWindow)
        {
            _captionsContainer = (AutomationElement?)chosen.Captions;
            if (_captionsContainer != null) Log.Write($"Captions region found in '{chosen.Title}'.");
        }
    }

    private static SourceNode? FindHangup(UiNode tree) =>
        tree.Descendants().OfType<SourceNode>().FirstOrDefault(n =>
            n.AutomationId == HangupButtonId ||
            (n.ControlType == "Button" &&
             (n.Name.Equals("Leave", StringComparison.OrdinalIgnoreCase) ||
              n.Name.StartsWith("Leave (", StringComparison.OrdinalIgnoreCase))));

    /// <summary>
    /// Finds the element that holds the caption items. Prefers an ancestor whose name/id/class
    /// mentions "caption" (in current Teams, the "Live Captions" panel); otherwise, while
    /// in a meeting, falls back to the lowest common ancestor of the caption items.
    /// </summary>
    internal static SourceNode? FindCaptionsContainer(UiNode tree, bool inMeeting)
    {
        var itemPaths = new List<List<UiNode>>();
        CollectItemPaths(tree, [], itemPaths);
        if (itemPaths.Count == 0)
        {
            // Captions are on but nobody has spoken yet: the panel exists with no caption items.
            return tree.Descendants().OfType<SourceNode>().FirstOrDefault(n =>
                n.Name.Equals("Live Captions", StringComparison.OrdinalIgnoreCase));
        }

        foreach (var path in itemPaths)
        {
            // Walk upward from the item (skipping the document root, whose name is the window title).
            for (int i = path.Count - 2; i >= 1; i--)
            {
                var n = path[i];
                if (n.Name.Contains("caption", StringComparison.OrdinalIgnoreCase) ||
                    n.IdOrClassContains("caption"))
                    return n as SourceNode;
            }
        }

        if (!inMeeting) return null;

        // Lowest common ancestor of all items.
        int depth = 0;
        while (itemPaths.All(p => p.Count > depth + 1 && p[depth] == itemPaths[0][depth])) depth++;
        var lca = depth > 0 ? itemPaths[0][depth - 1] : tree;

        // Don't mistake a (compact-density) chat message list for captions.
        bool looksLikeChat = itemPaths[0].Take(depth).Skip(1)
            .Any(n => n.Name.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
                      n.Name.Contains("message", StringComparison.OrdinalIgnoreCase));
        return looksLikeChat ? null : lca as SourceNode;
    }

    private static void CollectItemPaths(UiNode node, List<UiNode> ancestors, List<List<UiNode>> results)
    {
        var path = new List<UiNode>(ancestors) { node };
        if (CaptionParser.IsCaptionItem(node))
        {
            results.Add(path);
            return;
        }
        foreach (var child in node.Children) CollectItemPaths(child, path, results);
    }

    private static string CleanTitle(string windowTitle)
    {
        var first = windowTitle.Split(" | ")[0].Trim();
        if (first.Length == 0 || first.Equals("Microsoft Teams", StringComparison.OrdinalIgnoreCase))
            return "Teams Meeting";
        return first;
    }

    // --- Auto-enable captions -----------------------------------------------------------------

    private void MaybeAutoEnableCaptions(DateTime now)
    {
        if (_meetingDocument == null || _autoEnableAttempts >= MaxAutoEnableAttempts) return;
        if (now - _meetingDetectedAt < AutoEnableDelay || now - _lastAutoEnableAttempt < AutoEnableRetry) return;

        _autoEnableAttempts++;
        _lastAutoEnableAttempt = now;
        Log.Write($"Trying to turn on live captions (attempt {_autoEnableAttempts}).");
        try
        {
            var doc = _meetingDocument;

            if (IsCaptionsPanelVisible(doc))
            {
                Log.Write("Auto-enable: live captions are already on.");
                _autoEnableAttempts = MaxAutoEnableAttempts;
                _lastFullScan = DateTime.MinValue;
                return;
            }

            if (TryToolbarCaptionsButton(doc) || TryMoreMenu(doc) || TryKeyboardShortcut(doc))
            {
                _autoEnableAttempts = MaxAutoEnableAttempts;
                _lastFullScan = DateTime.MinValue; // look for the captions region right away
            }
            else if (_autoEnableAttempts >= MaxAutoEnableAttempts)
            {
                LogCaptionsControls(doc);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Auto-enable failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    private bool TryToolbarCaptionsButton(AutomationElement doc)
    {
        var button = FindById(doc, CaptionsMenuItemId);
        if (button == null || button.Current.ControlType != ControlType.Button) return false;

        Press(button);
        if (WaitForCaptionsPanel(doc))
        {
            Log.Write("Auto-enable: turned on live captions with the toolbar Captions button.");
            return true;
        }
        Log.Write("Auto-enable: pressed the toolbar Captions button but no captions appeared.");
        return false;
    }

    private bool TryMoreMenu(AutomationElement doc)
    {
        var more = FindById(doc, MoreButtonId);
        if (more == null) { Log.Write("Auto-enable: 'More' button not found."); return false; }
        bool pressed = false;
        try
        {
            Activate(more);

            var captions = FindCaptionsMenuEntry(doc, wait: true);
            if (captions == null)
            {
                var language = FindById(doc, LanguageSpeechMenuId);
                if (language != null)
                {
                    Activate(language);
                    captions = FindCaptionsMenuEntry(doc, wait: true);
                }
            }
            if (captions == null)
            {
                Log.Write("Auto-enable: no Captions entry in the More menu.");
                return false;
            }
            if (IsCaptionsOn(captions))
            {
                Log.Write($"Auto-enable: the More menu says captions are already on ('{captions.Current.Name}').");
                return true;
            }
            Press(captions);
            pressed = true;
        }
        finally
        {
            Thread.Sleep(300);
            CloseMenu(more);
        }

        if (pressed && WaitForCaptionsPanel(doc))
        {
            Log.Write("Auto-enable: turned on live captions from the More menu.");
            return true;
        }
        Log.Write("Auto-enable: pressed Captions in the More menu but no captions appeared.");
        return false;
    }

    private static AutomationElement? FindCaptionsMenuEntry(AutomationElement doc, bool wait)
    {
        string[] names = ["Captions", "Live captions", "Show live captions", "Turn on live captions"];
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, CaptionsMenuItemId),
            new OrCondition(names.Select(n => (Condition)new PropertyCondition(AutomationElement.NameProperty, n,
                PropertyConditionFlags.IgnoreCase)).ToArray()));
        for (int attempt = 0; ; attempt++)
        {
            var found = doc.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>()
                .FirstOrDefault(e => e.Current.ControlType == ControlType.CheckBox ||
                                     e.Current.ControlType == ControlType.MenuItem);
            if (found != null || !wait || attempt >= 10) return found;
            Thread.Sleep(200);
        }
    }

    private bool TryKeyboardShortcut(AutomationElement doc)
    {
        if (_meetingWindow == IntPtr.Zero || Native.GetForegroundWindow() != _meetingWindow)
        {
            Log.Write("Auto-enable: Teams isn't the active window, so the Alt+Shift+C shortcut wasn't used.");
            return false;
        }
        Native.SendAltShiftKey('C');
        if (WaitForCaptionsPanel(doc))
        {
            Log.Write("Auto-enable: turned on live captions with Alt+Shift+C.");
            return true;
        }
        Log.Write("Auto-enable: Alt+Shift+C didn't turn on captions.");
        return false;
    }

    private void LogCaptionsControls(AutomationElement doc)
    {
        try
        {
            static string Describe(AutomationElement e) =>
                $"{e.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")} '{e.Current.Name}' [{e.Current.AutomationId}]";

            var buttons = doc.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>().Where(e => e.Current.AutomationId.Length > 0).Select(Describe);
            Log.Write("Auto-enable gave up. Toolbar: " + string.Join(", ", buttons));

            var more = FindById(doc, MoreButtonId);
            if (more == null) return;
            try
            {
                Activate(more);
                Thread.Sleep(800);
                var items = doc.FindAll(TreeScope.Descendants, new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)))
                    .Cast<AutomationElement>().Where(e => e.Current.AutomationId.Length > 0).Select(Describe);
                Log.Write("More menu: " + string.Join(", ", items));
            }
            finally
            {
                CloseMenu(more);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not list the Teams controls: {ex.Message}");
        }
    }

    private static bool IsCaptionsPanelVisible(AutomationElement doc) =>
        FindById(doc, CaptionsPanelDismissId) != null ||
        doc.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Live Captions")) != null;

    private static bool WaitForCaptionsPanel(AutomationElement doc)
    {
        for (int i = 0; i < 20; i++)
        {
            if (IsCaptionsPanelVisible(doc)) return true;
            Thread.Sleep(200);
        }
        return false;
    }

    private static void Press(AutomationElement e)
    {
        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
        {
            ((InvokePattern)inv).Invoke();
        }
        else if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec))
        {
            try { ((ExpandCollapsePattern)ec).Expand(); }
            catch (InvalidOperationException) { }
        }
        else if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var tg))
        {
            ((TogglePattern)tg).Toggle();
        }
        else
        {
            throw new InvalidOperationException($"Element '{e.Current.Name}' can't be clicked via UI Automation.");
        }
    }

    /// <param name="waitForIt">Retry for up to ~2 s, for menu items that are still opening.</param>
    private static AutomationElement? FindById(AutomationElement root, string id, bool waitForIt = false)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, id);
        for (int attempt = 0; ; attempt++)
        {
            var found = root.FindFirst(TreeScope.Descendants, condition);
            if (found != null || !waitForIt || attempt >= 10) return found;
            Thread.Sleep(200);
        }
    }

    private static bool IsCaptionsOn(AutomationElement captionsItem)
    {
        if (captionsItem.TryGetCurrentPattern(TogglePattern.Pattern, out var tg))
            return ((TogglePattern)tg).Current.ToggleState == ToggleState.On;
        return captionsItem.Current.Name.Contains("off", StringComparison.OrdinalIgnoreCase);
    }

    private static void Activate(AutomationElement e)
    {
        if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec))
        {
            var pattern = (ExpandCollapsePattern)ec;
            if (pattern.Current.ExpandCollapseState != ExpandCollapseState.Collapsed) return; // already open
            pattern.Expand();
            return;
        }
        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
        {
            ((InvokePattern)inv).Invoke();
            return;
        }
        if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var tg))
        {
            ((TogglePattern)tg).Toggle();
            return;
        }
        throw new InvalidOperationException($"Element '{e.Current.Name}' can't be clicked via UI Automation.");
    }

    private static void CloseMenu(AutomationElement more)
    {
        try
        {
            if (more.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec) &&
                ((ExpandCollapsePattern)ec).Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                ((ExpandCollapsePattern)ec).Collapse();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not close the More menu: {ex.Message}");
        }
    }

    // --- Diagnostics --------------------------------------------------------------------------

    /// <summary>Text dump of every Teams UI tree, used to adapt the parser if Teams changes its markup.</summary>
    public string DumpTrees()
    {
        var sb = new StringBuilder();
        var docs = FindTeamsDocuments();
        sb.AppendLine($"Teams Captions Saver UI dump - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Teams running: {IsTeamsRunning()}, documents: {docs.Count}");
        foreach (var (hwnd, root, title) in docs)
        {
            sb.AppendLine();
            sb.AppendLine($"=== Window '{title}' (hwnd {hwnd}) ===");
            try
            {
                var tree = ToNode(root.GetUpdatedCache(_subtreeRequest));
                var hangupFound = FindHangup(tree) != null;
                var container = FindCaptionsContainer(tree, hangupFound);
                sb.AppendLine($"Leave button found: {hangupFound}; captions region: " +
                              (container == null ? "not found" : $"'{container.Name}' id='{container.AutomationId}'"));
                if (container != null)
                    foreach (var c in CaptionParser.Parse(container))
                        sb.AppendLine($"  parsed -> {c.Speaker}: {c.Text}");
                DumpNode(sb, tree, 0);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(failed: {ex.Message})");
            }
        }
        return sb.ToString();
    }

    private static void DumpNode(StringBuilder sb, UiNode n, int depth)
    {
        var name = n.Name.Length > 120 ? n.Name[..120] + "…" : n.Name;
        var cls = n.ClassName.Length > 80 ? n.ClassName[..80] + "…" : n.ClassName;
        sb.Append(' ', depth * 2)
          .Append('[').Append(n.ControlType).Append(']')
          .Append(name.Length > 0 ? $" name=\"{name.ReplaceLineEndings(" ")}\"" : "")
          .Append(n.AutomationId.Length > 0 ? $" id=\"{n.AutomationId}\"" : "")
          .Append(cls.Length > 0 ? $" class=\"{cls}\"" : "")
          .AppendLine();
        foreach (var c in n.Children) DumpNode(sb, c, depth + 1);
    }

    // --- UIA -> UiNode ------------------------------------------------------------------------

    /// <summary>UiNode that remembers the live UIA element it was built from.</summary>
    internal sealed class SourceNode : UiNode
    {
        public object? Source { get; init; }
    }

    private static UiNode ToNode(AutomationElement e)
    {
        var children = new List<UiNode>();
        AutomationElementCollection? cachedChildren = null;
        try { cachedChildren = e.CachedChildren; } catch (InvalidOperationException) { }
        if (cachedChildren != null)
            foreach (AutomationElement child in cachedChildren)
                children.Add(ToNode(child));

        var runtimeId = e.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) as int[];
        var controlType = e.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) as ControlType;
        return new SourceNode
        {
            Source = e,
            Key = runtimeId == null ? Guid.NewGuid().ToString("N") : string.Join(".", runtimeId),
            Name = e.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "",
            ClassName = e.GetCachedPropertyValue(AutomationElement.ClassNameProperty) as string ?? "",
            AutomationId = e.GetCachedPropertyValue(AutomationElement.AutomationIdProperty) as string ?? "",
            ControlType = controlType?.ProgrammaticName.Replace("ControlType.", "") ?? "",
            Children = children,
        };
    }

    private static bool IsAlive(AutomationElement e)
    {
        try
        {
            _ = e.Current.ControlType;
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
