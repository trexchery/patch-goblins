using System.Drawing.Drawing2D;

namespace PatchGoblins;

/// One account as a slim row: name, one meta line, two hairline meters, and (for a goblin on the team) its job line.
/// Clicking expands it (animated) to show every window, reset times and actions.
public sealed class AccountRow : Control
{
    public event Action<Account>? SwitchClicked;
    public event Action<Account>? TerminalClicked;
    public event Action<Account, string>? MenuAction;
    public event Action<AccountRow>? ExpandRequested;

    public const int BaseWidth = 320;
    static readonly Font NameFont = Ui.Font(9f, semibold: true), MetaFont = Ui.Font(8f), SmallFont = Ui.Font(7.5f);

    readonly Account _a;
    readonly DateTimeOffset _now;
    readonly List<(string Label, Window W, string? Extra)> _windows = [];
    readonly string? _note;
    readonly Job? _job; // null: a visitor (Codex, Grok), or nothing to say
    readonly IReadOnlyList<ProjectMapping> _projects;
    readonly string? _projectsLine; // only when every leaf name fits on one line; the context menu always lists them
    readonly ToolTip _tip = new();
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    readonly List<(Rectangle Rect, string Action, string Label)> _buttons = [];
    Rectangle _projectsRect;
    float _t, _target; // 0 = collapsed, 1 = expanded
    bool _hover, _tipOn;
    string? _hot;

    public Account Account => _a;
    public bool Expanded => _target > 0.5f;

    int Collapsed => Ui.S(_job != null ? 68 : 52);

    public AccountRow(Account a, DateTimeOffset now, IReadOnlyList<ProjectMapping>? projects = null, Job? job = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _a = a;
        _now = now;
        _job = job;
        Width = Ui.S(BaseWidth);
        Height = Collapsed;
        Margin = new Padding(0, 0, 0, Ui.S(2));
        Cursor = Cursors.Hand;

        var u = a.ShownUsage;
        if (u?.FiveHour is { } fh) _windows.Add(("5-hour", fh, null));
        if (u?.SevenDay is { } sd) _windows.Add(("Weekly", sd, null));
        foreach (var m in u?.Scoped ?? []) _windows.Add(($"{m.Name} weekly", m, null));
        if (u?.Spend is { } sp) _windows.Add(("Extra usage", new Window(sp.Pct, sp.ResetsAt), $"{sp.Used:0.00}/{sp.Limit:0.00} {sp.Currency}"));
        var notes = new List<string>();
        if (a.StatusNote is { } sn) notes.Add(char.ToUpper(sn[0]) + sn[1..]);
        if (a.Usage == null && a.LastGoodUsage != null) notes.Add("showing last known usage");
        if (a.LoginExpiresAt is { } exp && exp - now < TimeSpan.FromDays(5)) notes.Add($"login expires {Format.When(exp, now)}");
        _note = notes.Count > 0 ? string.Join(" · ", notes) : null;
        _projects = projects ?? [];
        if (_projects.Count > 0)
        {
            var line = "Projects · " + string.Join(", ", _projects.Select(p => p.Leaf));
            if (TextRenderer.MeasureText(line, MetaFont, Size.Empty, TextFormatFlags.NoPadding).Width <= Ui.S(BaseWidth) - Ui.S(38))
                _projectsLine = line;
        }

        _anim.Tick += (_, _) =>
        {
            _t += (_target - _t) * 0.35f;
            if (Math.Abs(_target - _t) < 0.02f) { _t = _target; _anim.Stop(); }
            Height = Collapsed + (int)((ExpandedHeight - Collapsed) * _t);
        };
    }

    int ExpandedHeight =>
        Collapsed + _windows.Count * Ui.S(22) + (_note != null ? Ui.S(22) : 0) + (_projectsLine != null ? Ui.S(22) : 0) + Ui.S(44);

    public void SetExpanded(bool expanded, bool animate = true)
    {
        _target = expanded ? 1 : 0;
        if (animate && IsHandleCreated) _anim.Start();
        else { _t = _target; Height = expanded ? ExpandedHeight : Collapsed; }
    }

    string Meta()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(_a.Alias)) parts.Add(_a.Email);
        if (_a.Active) parts.Add("active");
        if (_a.Disabled) parts.Add("excluded");
        var status = _a.UsageStatus switch
        {
            "token_expired" => "token expired",
            "relogin_required" => "login expired",
            "unavailable" => _a.LastGoodUsage != null ? "last known" : "no data",
            "api_key" => "API key",
            "no_credentials" => "no login",
            "foreign_credential" => "needs switch",
            _ => null,
        };
        if (status != null) parts.Add(status);
        // Readings older than 5 minutes say so, instead of looking current.
        if ((_a.Usage != null ? _a.UsageAgeSeconds : _a.LastGoodAgeSeconds) is { } age && age >= 300)
            parts.Add(Format.Age(age));
        return parts.Count > 0 ? string.Join(" · ", parts) : $"account {_a.Number}";
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _hot = null; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hot = _buttons.FirstOrDefault(b => b.Rect.Contains(e.Location)).Action;
        if (hot != _hot) { _hot = hot; Invalidate(); }
        var overProjects = _projectsRect.Contains(e.Location);
        if (overProjects != _tipOn)
        {
            _tipOn = overProjects;
            _tip.SetToolTip(this, overProjects ? string.Join("\n", _projects.Select(p => p.Folder)) : null);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Right) { ShowMenu(e.Location); return; }
        var hit = _buttons.FirstOrDefault(b => b.Rect.Contains(e.Location));
        switch (hit.Action)
        {
            case "switch": SwitchClicked?.Invoke(_a); return;
            case "terminal": TerminalClicked?.Invoke(_a); return;
            case "more": ShowMenu(new Point(hit.Rect.Left, hit.Rect.Bottom + Ui.S(2))); return;
        }
        if (e.Y < Collapsed) ExpandRequested?.Invoke(this);
    }

    internal ContextMenuStrip ShowMenu(Point at)
    {
        var menu = new ContextMenuStrip();
        if (!_a.Active) menu.Items.Add("Switch to this account", null, (_, _) => SwitchClicked?.Invoke(_a));
        menu.Items.Add("Open terminal", null, (_, _) => TerminalClicked?.Invoke(_a));
        var cont = new ToolStripMenuItem("Continue in");
        foreach (var t in ProviderCli.HandoffTargets(_a.Provider))
            cont.DropDownItems.Add(ProviderCli.Title(t) + "…", null, (_, _) => MenuAction?.Invoke(_a, "continue:" + t));
        if (cont.HasDropDownItems) menu.Items.Add(cont);
        menu.Items.Add(new ToolStripSeparator());
        if (_a.Provider == "claude") // folder pins, auto-switch and aliases are Claude-engine features
        {
            menu.Items.Insert(menu.Items.Count - 1, new ToolStripMenuItem("Assign project folder…", null, (_, _) => MenuAction?.Invoke(_a, "map")));
            if (_projects.Count > 0)
            {
                var proj = new ToolStripMenuItem("Projects");
                foreach (var p in _projects)
                {
                    var folder = new ToolStripMenuItem(p.Leaf) { ToolTipText = p.Folder };
                    folder.DropDownItems.Add("Open terminal", null, (_, _) => MenuAction?.Invoke(_a, "project-open:" + p.Folder));
                    folder.DropDownItems.Add("Remove", null, (_, _) => MenuAction?.Invoke(_a, "project-remove:" + p.Folder));
                    proj.DropDownItems.Add(folder);
                }
                menu.Items.Insert(menu.Items.Count - 1, proj);
            }
            menu.Items.Add(_a.Disabled ? "Include in auto-switch" : "Exclude from auto-switch", null,
                (_, _) => MenuAction?.Invoke(_a, _a.Disabled ? "enable" : "disable"));
            menu.Items.Add("Rename…", null, (_, _) => MenuAction?.Invoke(_a, "alias"));
        }
        menu.Items.Add("Log in again…", null, (_, _) => MenuAction?.Invoke(_a, "reenroll"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove…", null, (_, _) => MenuAction?.Invoke(_a, "remove"));
        MenuRenderer.Apply(menu);
        MenuRenderer.DisposeOnClose(menu);
        menu.Show(this, at);
        return menu;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var pal = Palette.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? pal.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = Ui.S(12), right = Width - pad;

        if (_hover || _t > 0)
        {
            using var bg = new SolidBrush(pal.Hover);
            using var path = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(6));
            g.FillPath(bg, path);
        }

        // Identity: name, meta line, and the job line with its dot. A visitor has no job, so its active dot stays on the name.
        int x = pad + Ui.S(14);
        if (_a.Active && _job == null)
        {
            using var dot = new SolidBrush(pal.Live);
            float d = Ui.S(6);
            g.FillEllipse(dot, pad, Ui.S(15), d, d);
        }
        var nameWidth = Width - x - Ui.S(150);
        var name = string.IsNullOrEmpty(_a.Alias) ? _a.Email : _a.Alias;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        int nameY = Ui.S(_job != null ? 8 : 9), metaY = Ui.S(_job != null ? 25 : 28);
        var dim = _a.Disabled || _a.Provider != "claude"; // visitors read quieter than the team
        TextRenderer.DrawText(g, name, NameFont, new Rectangle(x, nameY, nameWidth, NameFont.Height), dim ? pal.Muted : pal.Text, flags);
        TextRenderer.DrawText(g, Meta(), MetaFont, new Rectangle(x, metaY, nameWidth, MetaFont.Height), pal.Muted, flags);
        if (_job is { } job)
        {
            using var dot = new SolidBrush(job.Kind switch { JobKind.Working or JobKind.Yours => pal.Live, JobKind.Resting => Palette.Warn, _ => pal.Faint });
            float d = Ui.S(6);
            g.FillEllipse(dot, pad, Ui.S(46) + (MetaFont.Height - d) / 2f, d, d);
            TextRenderer.DrawText(g, job.Text, MetaFont, new Rectangle(x, Ui.S(46), right - x, MetaFont.Height),
                job.Kind == JobKind.Working ? pal.Text : pal.Muted, flags);
        }

        // Hairline meters on the right for the windows this account has (Codex Team: weekly only,
        // Grok: one weekly or monthly pool). No usage yet: two empty placeholders.
        var u = _a.ShownUsage;
        var meters = new List<(string, double?)>();
        if (u?.FiveHour is { } f) meters.Add(("5h", f.Pct));
        if (u?.SevenDay is { } s) meters.Add(("wk", s.Pct));
        if (meters.Count == 0 && u?.Scoped is [var first, ..]) meters.Add(("mo", first.Pct));
        if (meters.Count == 0) meters.AddRange([("5h", null), ("wk", null)]);
        if (meters.Count == 1) Meter(g, pal, meters[0].Item1, meters[0].Item2, right, Ui.S(21));
        else { Meter(g, pal, meters[0].Item1, meters[0].Item2, right, Ui.S(12)); Meter(g, pal, meters[1].Item1, meters[1].Item2, right, Ui.S(30)); }

        _buttons.Clear();
        _projectsRect = Rectangle.Empty;
        if (_t <= 0.01f) return;

        // Expanded: every window with its reset, then actions.
        using var clip = new Region(new Rectangle(0, 0, Width, Height));
        g.Clip = clip;
        int y = Collapsed + Ui.S(2);
        foreach (var (label, w, extra) in _windows)
        {
            TextRenderer.DrawText(g, label, MetaFont, new Point(x, y), pal.Text, TextFormatFlags.NoPadding);
            var reset = w.ResetsAt is { } r ? "resets " + Format.When(r, _now) : "";
            if (extra != null) reset = extra + (reset.Length > 0 ? " · " + reset : "");
            var pct = $"{w.Pct:0}%";
            var pw = TextRenderer.MeasureText(g, pct, MetaFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, pct, MetaFont, new Point(right - pw, y), pal.Meter(w.Pct) is var c && w.Pct >= Palette.WarnPct ? c : pal.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, reset, MetaFont, new Rectangle(x + Ui.S(96), y, right - pw - Ui.S(8) - x - Ui.S(96), MetaFont.Height),
                pal.Muted, flags);
            y += Ui.S(22);
        }
        if (_note != null)
        {
            TextRenderer.DrawText(g, _note, MetaFont, new Rectangle(x, y, right - x, MetaFont.Height), pal.Muted, flags);
            y += Ui.S(22);
        }
        if (_projectsLine != null)
        {
            _projectsRect = new Rectangle(x, y, right - x, MetaFont.Height);
            TextRenderer.DrawText(g, _projectsLine, MetaFont, _projectsRect, pal.Muted, flags);
            y += Ui.S(22);
        }

        y += Ui.S(6);
        int bx = x;
        if (!_a.Active) bx = Button(g, pal, "Switch", "switch", bx, y, primary: true);
        bx = Button(g, pal, "Terminal", "terminal", bx, y);
        Button(g, pal, "More", "more", bx, y);
    }

    void Meter(Graphics g, Palette pal, string label, double? pct, int right, int y)
    {
        int pctW = Ui.S(32), barW = Ui.S(72), labelW = Ui.S(18);
        int barX = right - pctW - Ui.S(6) - barW;
        TextRenderer.DrawText(g, label, SmallFont, new Point(barX - labelW, y + Ui.S(1)), pal.Faint, TextFormatFlags.NoPadding);
        float h = Math.Max(2, Ui.S(3)), by = y + Ui.S(6);
        using (var track = new SolidBrush(pal.Track))
        using (var path = Ui.Round(new RectangleF(barX, by, barW, h), h / 2)) g.FillPath(track, path);
        var text = pct is { } p ? $"{p:0}%" : "–";
        if (pct is { } v && v > 0)
        {
            var c = pal.Meter(v);
            if (_a.Usage == null) c = Color.FromArgb(c.A / 2, c);
            using var fill = new SolidBrush(c);
            using var path = Ui.Round(new RectangleF(barX, by, Math.Max(h, (float)(barW * Math.Clamp(v, 0, 100) / 100)), h), h / 2);
            g.FillPath(fill, path);
        }
        var tw = TextRenderer.MeasureText(g, text, MetaFont, Size.Empty, TextFormatFlags.NoPadding).Width;
        var tc = pct is { } q && q >= Palette.WarnPct ? pal.Meter(q) : pal.Text;
        TextRenderer.DrawText(g, text, MetaFont, new Point(right - tw, y), pct == null ? pal.Faint : tc, TextFormatFlags.NoPadding);
    }

    int Button(Graphics g, Palette pal, string text, string action, int x, int y, bool primary = false)
    {
        var w = TextRenderer.MeasureText(g, text, MetaFont, Size.Empty, TextFormatFlags.NoPadding).Width + Ui.S(22);
        var r = new Rectangle(x, y, w, Ui.S(26));
        using var path = Ui.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Ui.S(5));
        var hot = _hot == action;
        if (primary)
        {
            using var b = new SolidBrush(Color.FromArgb(hot ? 220 : 255, pal.Live));
            g.FillPath(b, path);
        }
        else
        {
            if (hot) { using var b = new SolidBrush(pal.Pressed); g.FillPath(b, path); }
            using var pen = new Pen(pal.Border);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, text, MetaFont, r, primary ? pal.Back : pal.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        _buttons.Add((r, action, text));
        return r.Right + Ui.S(6);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _anim.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}

