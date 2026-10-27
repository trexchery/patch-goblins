using System.Runtime.InteropServices;

namespace PatchGoblins;

/// Tray flyout. Pinned, it becomes a movable window that stays on the desktop.
public sealed class DashboardForm : Form
{
    readonly FlowLayoutPanel _list;
    readonly Label _title, _status;
    readonly Panel _header, _footer;
    readonly Pill _pin, _close, _agents, _refresh;
    readonly System.Windows.Forms.Timer _shine = new() { Interval = 40 };
    long _shineT0;
    readonly ToolTip _tips = new();
    string? _expandedEmail;
    Icon? _goblin;
    string _refreshed = "", _updated = "";
    string? _team;
    bool _visitorsOpen; // ponytail: per run, not saved; a setting if people want it kept
    (Snapshot? Snap, string? Error, IReadOnlyList<(string Provider, Snapshot? Snap, string? Error)>? Others, IReadOnlyList<ProjectMapping>? Projects, IReadOnlyDictionary<string, Job>? Jobs) _last;

    /// The refresh states: `Offline` is the one the header shows (it outranks the team's line); the rest live in the refresh button's tip.
    public const string Updating = "updating…", Offline = "offline";

    public bool Pinned { get; private set; }
    /// Above 0 while a dialog of the flyout's own is up (a folder picker): losing focus to it must not hide the flyout.
    public int HoldOpen { get; set; }

    SettingsPanel? _settings;
    public bool SettingsOpen => _settings != null;
    /// Questions only the owner answers (starting agents, a goal's checks), asked here instead of in a popup.
    readonly List<(string Title, string Body, TaskCompletionSource<bool> Answer)> _asks = [];

    public event Action<Account>? SwitchRequested;
    public event Action<Account>? TerminalRequested;
    public event Action<Account, string>? AccountAction;
    public event Action? RefreshRequested, SettingsRequested, AddRequested, Moved;
    public event Action<bool>? PinToggled;
    /// The agents launcher: (anchor, menu) where menu is a right-click.
    public event Action<Control, bool>? AgentsRequested;

    public DashboardForm()
    {
        Text = "Patch Goblins";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        Width = Ui.S(AccountRow.BaseWidth + 16);
        Icon = TrayIcons.App;
        Padding = new Padding(1);

        // The header: the goblin, the name, and under it one live line about the team.
        _header = new Panel { Dock = DockStyle.Top, Height = Ui.S(48) };
        _header.Paint += (_, e) => e.Graphics.DrawIcon(_goblin ??= new Icon(TrayIcons.App, Ui.S(24), Ui.S(24)), new Rectangle(Ui.S(14), Ui.S(12), Ui.S(24), Ui.S(24)));
        _title = new Label { Text = "Patch Goblins", Font = Ui.Font(9.5f, semibold: true), AutoSize = true, Location = new Point(Ui.S(46), Ui.S(7)) };
        _status = new Label
        {
            Font = Ui.Font(8f),
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(Ui.S(46), Ui.S(27)),
            Size = new Size(Width - Ui.S(46) - 4 * Ui.S(30) - Ui.S(14), Ui.S(16)),
        };
        var settings = HeaderIcon(Ui.Settings, "Settings", 0);
        var add = HeaderIcon(Ui.Add, "Add account", 1);
        _pin = HeaderIcon(Ui.Pin, "Pin to desktop", 2);
        _close = HeaderIcon(Ui.Close, "Hide", 3);
        _close.Visible = false;
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        add.Click += (_, _) => AddRequested?.Invoke();
        _pin.Click += (_, _) => { SetPinned(!Pinned); PinToggled?.Invoke(Pinned); };
        _close.Click += (_, _) => Hide();
        _header.Controls.AddRange([_title, _status, settings, add, _pin, _close]);
        foreach (var c in new Control[] { _header, _title, _status }) c.MouseDown += DragWindow;

        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(Ui.S(8), 0, 0, Ui.S(4)),
        };

        // The footer: one labeled Agents button (right-click for its menu) and refresh.
        _footer = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(42) };
        _footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Palette.Current.Border);
            e.Graphics.DrawLine(pen, Ui.S(20), 0, _footer.Width - Ui.S(20), 0);
        };
        _agents = new Pill("Agents", PillStyle.Subtle, Ui.Agents) { TabStop = false, Location = new Point(Ui.S(14), Ui.S(6)) };
        _agents.MouseUp += (_, e) => { if (e.Button is MouseButtons.Left or MouseButtons.Right) AgentsRequested?.Invoke(_agents, e.Button == MouseButtons.Right); };
        _tips.SetToolTip(_agents, "Agents terminal");
        _refresh = new Pill("", PillStyle.Icon, Ui.Refresh) { TabStop = false };
        _refresh.Location = new Point(Width - Ui.S(14) - _refresh.Width, Ui.S(7));
        _refresh.Click += (_, _) => RefreshRequested?.Invoke();
        _tips.SetToolTip(_refresh, "Refresh now");
        _footer.Controls.AddRange([_agents, _refresh]);
        // The agents button glints every few seconds, whatever Windows' animation setting says. One timer, only while shown.
        // 40 ms only while it sweeps; between sweeps the timer sleeps until the next one (most of every 4.5 s).
        _shine.Tick += (_, _) =>
        {
            var at = (Environment.TickCount64 - _shineT0) % ShinePeriodMs;
            Shimmer(at / (float)ShineSweepMs);
            _shine.Interval = at < ShineSweepMs ? 40 : (int)Math.Max(40, ShinePeriodMs - at);
        };
        VisibleChanged += (_, _) =>
        {
            _shineT0 = Environment.TickCount64;
            _shine.Enabled = Visible;
            if (!Visible) Shimmer(-1);
        };

        Controls.Add(_list);
        Controls.Add(_header);
        Controls.Add(_footer);
        ApplyTheme();
        Deactivate += (_, _) => { if (!Pinned && HoldOpen == 0) Hide(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { if (_settings != null) CloseSettings(); else Hide(); } };
        ResizeEnd += (_, _) => { if (Pinned) Moved?.Invoke(); };
    }

    Pill HeaderIcon(string glyph, string tip, int slot)
    {
        var b = new Pill("", PillStyle.Icon, glyph) { TabStop = false };
        b.Location = new Point(Width - Ui.S(14) - (slot + 1) * Ui.S(30), Ui.S(10));
        _tips.SetToolTip(b, tip);
        return b;
    }

    void DragWindow(object? sender, MouseEventArgs e)
    {
        if (!Pinned || e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
        Moved?.Invoke();
    }

    public void SetPinned(bool pinned)
    {
        Pinned = pinned;
        TopMost = !pinned; // a pinned window behaves like a normal desktop window
        _pin.Glyph = pinned ? Ui.Unpin : Ui.Pin;
        _tips.SetToolTip(_pin, pinned ? "Unpin" : "Pin to desktop");
        _close.Visible = pinned;
        _pin.Invalidate();
        _header.Cursor = pinned ? Cursors.SizeAll : Cursors.Default;
    }

    void ApplyTheme()
    {
        var pal = Palette.Current;
        BackColor = _list.BackColor = _header.BackColor = _footer.BackColor = pal.Back;
        _agents.BackColor = _refresh.BackColor = pal.Back; // opaque: a transparent pill is composited twice when the window is captured
        _title.ForeColor = pal.Text;
        _status.ForeColor = pal.Muted;
        if (IsHandleCreated) Ui.ThemeWindow(_list, pal);
        Invalidate(true);
    }

    /// The refresh state: a time, or `Updating`/`Offline`. Only `Offline` shows in the header; the refresh button's tip carries the rest.
    public void SetStatus(string text)
    {
        _refreshed = text;
        _updated = text is Updating or Offline or "" ? text : "updated " + text;
        ShowStatus();
    }

    /// The team's one-line summary ("2 working · 1 resting"), or null when there is none.
    public void SetTeam(string? summary)
    {
        if (_team == summary) return;
        _team = summary;
        ShowStatus();
    }

    internal string StatusText => _status.Text;

    void ShowStatus()
    {
        _status.Text = _refreshed == Offline ? Offline : _team ?? "";
        _tips.SetToolTip(_refresh, _updated.Length > 0 ? "Refresh now · " + _updated : "Refresh now");
    }

    const int ShinePeriodMs = 4500, ShineSweepMs = 800;

    /// Phase of the agents pill's sweep: 0 to 1 while sweeping, anything else is at rest. Repaints only the pill, and only on change.
    internal void Shimmer(float phase)
    {
        var p = phase is >= 0 and <= 1 ? phase : -1;
        if (p < 0 && _agents.Shine < 0) return;
        _agents.Shine = p;
        _agents.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _shine.Dispose(); _tips.Dispose(); _goblin?.Dispose(); }
        base.Dispose(disposing);
    }

    /// The agents button's dot (null: nothing to show) and tooltip.
    public void SetAgents(Color? badge, string tip)
    {
        if (_agents.Badge == badge && _tips.GetToolTip(_agents) == tip) return;
        _agents.Badge = badge;
        _tips.SetToolTip(_agents, tip);
        _agents.Invalidate();
    }

    /// `others`: Codex/Grok snapshots (or their error), the visitors below the team. `projects`: folder assignments; each row shows its own.
    /// `jobs`: what each Claude account is doing (AccountPicker.Key), the line under its name.
    public void Render(Snapshot? snap, string? error, IReadOnlyList<(string Provider, Snapshot? Snap, string? Error)>? others = null,
        IReadOnlyList<ProjectMapping>? projects = null, IReadOnlyDictionary<string, Job>? jobs = null)
    {
        _last = (snap, error, others, projects, jobs);
        // A refresh that changes nothing shown is no rebuild: tearing every row down and back up is what flickers.
        var shown = Shown(snap, error, others, projects, jobs);
        if (shown == _shown && _list.Controls.Count > 0) return;
        _shown = shown;
        ApplyTheme();
        // Painting off while the rows are rebuilt, then one paint: no row-by-row flash. Shown only: WM_SETREDRAW on sets
        // WS_VISIBLE, which put a hidden flyout on screen blank and never activated, so never hidden again.
        var redraw = IsHandleCreated && Visible;
        if (redraw) SendMessage(Handle, 0x000B /* WM_SETREDRAW */, 0, 0);
        _list.SuspendLayout();
        // Copy first: Dispose removes the control, so enumerating live would skip every other one.
        foreach (var c in _list.Controls.Cast<Control>().ToList()) c.Dispose();
        _list.Controls.Clear();
        foreach (var ask in _asks) _list.Controls.Add(AskCard(ask));
        var visitors = (others ?? []).Where(o => o.Snap is { Accounts.Count: > 0 } || o.Error != null).ToList();
        var now = DateTimeOffset.Now;
        void AddRows(Snapshot? s, Func<Account, Job?> job)
        {
            foreach (var a in (s?.Accounts ?? []).OrderByDescending(a => a.Active).ThenBy(a => a.Number))
            {
                var row = new AccountRow(a, now, projects?.Where(m => m.Matches(a)).ToList(), job(a));
                row.SwitchClicked += x => SwitchRequested?.Invoke(x);
                row.TerminalClicked += x => TerminalRequested?.Invoke(x);
                row.MenuAction += (x, act) => AccountAction?.Invoke(x, act);
                row.ExpandRequested += ToggleRow;
                row.SizeChanged += (_, _) => FitHeight();
                if (Key(a) == _expandedEmail) row.SetExpanded(true, animate: false);
                _list.Controls.Add(row);
            }
        }

        if (snap is { Accounts.Count: > 0 } || error != null) _list.Controls.Add(SectionLabel("Team"));
        if (error != null) _list.Controls.Add(Message("Couldn't refresh · " + error));
        AddRows(snap, a => jobs?.TryGetValue(AccountPicker.Key(a), out var j) == true ? j : null);
        if (visitors.Count > 0)
        {
            _list.Controls.Add(VisitorsLabel(visitors.Select(v => v.Provider)));
            if (_visitorsOpen)
                foreach (var (provider, s, err) in visitors)
                {
                    _list.Controls.Add(SectionLabel(ProviderCli.Title(provider)));
                    if (err != null) _list.Controls.Add(Message("Couldn't refresh · " + err));
                    AddRows(s, _ => null);
                }
        }
        var any = snap is { Accounts.Count: > 0 } || visitors.Any(v => v.Snap is { Accounts.Count: > 0 });
        if (!any && snap == null && error == null)
            _list.Controls.Add(Message("Loading…"));
        else if (!any && error == null)
        {
            _list.Controls.Add(Message("No accounts yet. Add the account you use with Claude Code."));
            var add = new Pill("Add account", PillStyle.Primary, Ui.Add) { Margin = new Padding(Ui.S(12), Ui.S(2), 0, Ui.S(12)) };
            add.Click += (_, _) => AddRequested?.Invoke();
            _list.Controls.Add(add);
        }
        _list.ResumeLayout();
        FitHeight();
        if (redraw)
        {
            SendMessage(Handle, 0x000B /* WM_SETREDRAW */, 1, 0);
            Refresh();
        }
    }

    string? _shown;

    /// What a render would draw, as text: equal text, equal flyout. Minutes, not seconds, for times shown as "in 2h 5m".
    string Shown(Snapshot? snap, string? error, IReadOnlyList<(string Provider, Snapshot? Snap, string? Error)>? others,
        IReadOnlyList<ProjectMapping>? projects, IReadOnlyDictionary<string, Job>? jobs) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            snap = snap?.Accounts.Select(a => a with { UsageAgeSeconds = null, UsageFetchedAt = null }),
            error,
            others = others?.Select(o => new { o.Provider, accounts = o.Snap?.Accounts.Select(a => a with { UsageAgeSeconds = null, UsageFetchedAt = null }), o.Error }),
            projects,
            jobs = jobs?.OrderBy(j => j.Key).Select(j => j.Key + "=" + j.Value),
            asks = _asks.Count,
            _visitorsOpen,
            minute = DateTimeOffset.Now.ToString("yyyyMMddHHmm"),
            dark = Palette.Current.IsDark,
        });

    /// Opens or closes the Visitors (Codex, Grok) rows and redraws.
    internal void SetVisitorsOpen(bool open)
    {
        if (_visitorsOpen == open) return;
        _visitorsOpen = open;
        Render(_last.Snap, _last.Error, _last.Others, _last.Projects, _last.Jobs);
    }

    void ToggleRow(AccountRow row)
    {
        var open = !row.Expanded;
        foreach (var r in _list.Controls.OfType<AccountRow>()) if (r != row && r.Expanded) r.SetExpanded(false);
        row.SetExpanded(open);
        _expandedEmail = open ? Key(row.Account) : null;
    }

    static string Key(Account a) => a.Provider + ":" + a.Email;

    /// Small muted caps above a group of rows: Team, then the visitors' providers once they are opened.
    static Label SectionLabel(string title) => new()
    {
        Text = title.ToUpperInvariant(),
        Tag = "section",
        ForeColor = Palette.Current.Faint,
        Font = Ui.Font(7f, semibold: true),
        AutoSize = true,
        Margin = new Padding(Ui.S(12), Ui.S(8), 0, Ui.S(2)),
    };

    /// The visitors' one row: who they are, closed until clicked (Codex and Grok only show usage and switch by hand).
    Label VisitorsLabel(IEnumerable<string> providers)
    {
        var label = SectionLabel((_visitorsOpen ? "– " : "+ ") + "Visitors · " + string.Join(" · ", providers.Select(ProviderCli.Title)));
        label.ForeColor = Palette.Current.Muted;
        label.Cursor = Cursors.Hand;
        label.Click += (_, _) => BeginInvoke(() => SetVisitorsOpen(!_visitorsOpen)); // after the click: this label is gone once it redraws
        return label;
    }

    Label Message(string text) => new()
    {
        Text = text,
        ForeColor = Palette.Current.Muted,
        Font = Ui.Font(8.5f),
        AutoSize = true,
        MaximumSize = new Size(Ui.S(AccountRow.BaseWidth - 24), 0),
        Margin = new Padding(Ui.S(12), Ui.S(6), 0, Ui.S(12)),
    };

    /// Settings in place of the accounts, the flyout widened to fit them; Cancel, Save or Escape bring the accounts back.
    public void ShowSettings(SettingsPanel panel)
    {
        CloseSettings();
        _settings = panel;
        panel.Dock = DockStyle.Fill;
        _list.Visible = _footer.Visible = false;
        _title.Text = "Settings";
        SetWidth(Math.Max(Ui.S(AccountRow.BaseWidth + 16), panel.Width + Padding.Horizontal));
        Controls.Add(panel);
        panel.BringToFront();
        FitHeight();
        panel.Search.Focus();
    }

    public void CloseSettings()
    {
        if (_settings is not { } panel) return;
        _settings = null;
        ActiveControl = _list; // focus off the panel first: disposing the focused box would deactivate, and so hide, the flyout
        Controls.Remove(panel);
        panel.Dispose();
        _title.Text = "Patch Goblins";
        _list.Visible = _footer.Visible = true;
        SetWidth(Ui.S(AccountRow.BaseWidth + 16));
        FitHeight();
    }

    /// A new width, the right edge kept (the flyout sits at the tray), the header's icons moved along.
    void SetWidth(int w)
    {
        if (w == Width) return;
        var dx = w - Width;
        var right = Right;
        foreach (var c in _header.Controls.OfType<Pill>()) c.Left += dx;
        _status.Width += dx;
        _refresh.Left += dx;
        Width = w;
        if (!Pinned) Left = right - w;
    }

    /// Asks the owner here, on a card above the accounts, and shows the flyout. No answer within `expire` is a no.
    public async Task<bool> Ask(string title, string body, TimeSpan expire)
    {
        var ask = (title, body, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        _asks.Add(ask);
        CloseSettings();
        Rerender();
        if (await Task.WhenAny(ask.Item3.Task, Task.Delay(expire)) != ask.Item3.Task) Answer(ask, false);
        return await ask.Item3.Task;
    }

    void Answer((string, string, TaskCompletionSource<bool> Answer) ask, bool yes)
    {
        if (!_asks.Remove(ask)) return;
        ask.Answer.TrySetResult(yes);
        Rerender();
    }

    internal int Pending => _asks.Count;

    void Rerender() => Render(_last.Snap, _last.Error, _last.Others, _last.Projects, _last.Jobs);

    Control AskCard((string Title, string Body, TaskCompletionSource<bool> Answer) ask)
    {
        var pal = Palette.Current;
        var w = Ui.S(AccountRow.BaseWidth - 16);
        var card = new Panel { Width = w, BackColor = pal.Card, Margin = new Padding(Ui.S(4), Ui.S(8), 0, Ui.S(4)), Tag = "ask" };
        var title = new Label { Text = ask.Title, Font = Ui.Font(9f, semibold: true), ForeColor = pal.Text, AutoSize = true, MaximumSize = new Size(w - Ui.S(24), 0), Location = new Point(Ui.S(12), Ui.S(10)) };
        var body = new Label { Text = ask.Body, Font = Ui.Font(8f), ForeColor = pal.Muted, AutoSize = true, MaximumSize = new Size(w - Ui.S(24), 0), Location = new Point(Ui.S(12), title.Bottom + Ui.S(6)) };
        card.Controls.AddRange([title, body]);
        body.Top = title.Top + title.PreferredHeight + Ui.S(6);
        var yes = new Pill("Approve", PillStyle.Primary) { BackColor = pal.Card };
        var no = new Pill("Decline") { BackColor = pal.Card };
        var y = body.Top + body.PreferredHeight + Ui.S(10);
        yes.Location = new Point(w - Ui.S(12) - yes.Width, y);
        no.Location = new Point(yes.Left - Ui.S(8) - no.Width, y);
        yes.Click += (_, _) => BeginInvoke(() => Answer(ask, true)); // after the click: this card is gone once it redraws
        no.Click += (_, _) => BeginInvoke(() => Answer(ask, false));
        card.Controls.AddRange([yes, no]);
        card.Height = y + yes.Height + Ui.S(12);
        return card;
    }

    /// Grows/shrinks with content, keeping the edge nearest the taskbar fixed.
    void FitHeight()
    {
        var content = _settings?.ContentHeight ?? _list.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical) + _list.Padding.Vertical;
        var screen = Screen.FromPoint(Visible ? Location : Cursor.Position).WorkingArea;
        var h = Math.Min(_header.Height + (_settings == null ? _footer.Height : 0) + content + Padding.Vertical, (int)(screen.Height * 0.8));
        if (h == Height) return;
        var bottom = Bottom;
        Height = h;
        if (Visible && !Pinned) Top = bottom - h;
    }

    public void ShowNearTray()
    {
        FitHeight();
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Right - Width - Ui.S(12), wa.Bottom - Height - Ui.S(12));
        Show();
        Activate();
    }

    /// Shows the pinned window at a saved spot, pulled back on-screen if a monitor went away.
    public void ShowPinnedAt(int? x, int? y)
    {
        FitHeight();
        var wa = Screen.FromPoint(new Point(x ?? 0, y ?? 0)).WorkingArea;
        var px = Math.Clamp(x ?? wa.Right - Width - Ui.S(12), wa.Left, Math.Max(wa.Left, wa.Right - Width));
        var py = Math.Clamp(y ?? wa.Top + Ui.S(12), wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        Location = new Point(px, py);
        Show();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Palette.Current.IsDark ? Color.FromArgb(56, 56, 56) : Color.FromArgb(220, 220, 220));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.RoundCorners(Handle);
        Ui.ThemeWindow(_list, Palette.Current);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}
