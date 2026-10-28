namespace PatchGoblins;

public sealed partial class TrayApp : ApplicationContext
{
    readonly Engine _engine;
    readonly DockSettings _settings = DockSettings.Load();
    readonly NotifyIcon _tray;
    readonly DashboardForm _dash = new();
    readonly System.Windows.Forms.Timer _timer = new(), _watch = new();
    readonly ContextMenuStrip _menu = new();
    Snapshot? _snap;
    string? _error;
    // Codex / Grok: polled on the same tick, each failure kept to its own section.
    readonly Dictionary<string, (Snapshot? Snap, string? Error)> _others = [];
    bool _busy;
    bool? _pending; // a request made during a busy pass: null none, else whether it forces a refresh
    bool _pendingAuto; // whether a request made during a busy pass wants the auto-switch and reopen steps too
    DateTime _dashHiddenAt;
    DateTimeOffset? _lastExhaustedNotice;
    string? _lastBlockedNotice;
    readonly Dictionary<string, DateTime> _resumed = ResumeStopped.LoadMoves(MovesPath); // "session id#account" → when it was last reopened there
    readonly HashSet<string> _told = [], _reported = [], _skipped = []; // busy windows at a limit named in a balloon, limits handed to the engine, skips logged
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Limit> _agentLimits = []; // session id → a limit error seen in its subagent/workflow files
    readonly LimitWatch _limitWatch;
    readonly LimitSwitch _limits;
    readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    int _engineSwitches; // the engine's own auto check or a menu switch is running: a window move must not race it
    readonly SemaphoreSlim _movePass = new(1, 1); // one pass of reopening windows at a time, whoever asks
    bool _retry, _watching; // a nudge that had to wait (typing, a window busy at its limit): look again on the next poll; a poll running
    double? _engineThreshold, _engineHysteresis; // the engine's own, so windows move at the line the default login switches at
    int _engineSwitchBuffer; // autoswitch.switchBufferSeconds, for the settings box
    double Threshold => _settings.AutoLine && _snap?.Accounts.FirstOrDefault(a => a.Active) is { } on
        ? _line.For(on, null) // the default login's automatic line
        : _engineThreshold ?? 97; // the one fallback for it, until the engine's is read
    readonly SwitchLine _line = new();
    readonly Dictionary<string, DateTime> _busySince = []; // busy windows at a limit → when first seen so: only ones still busy after a grace are told about
    static readonly TimeSpan BusyGrace = TimeSpan.FromSeconds(10);
    readonly Dictionary<int, DateTime> _freed = []; // account → when its `cswap run` windows were last moved to the default login
    readonly HashSet<int> _heldNotice = []; // pinned accounts the user was told a working window holds, while a free is due
    (int N, string Text)? _held; // this pass's held warning, shown only if the pass shows no other balloon (a second replaces the first)
    int _notices; // balloons shown so far
    int _switches; // switches the tray made: an account list started before one is older than the state they left

    /// While auto-switch is on, its check runs at least this often, whatever the dashboard refresh is:
    /// a window that hits a limit should not wait out a long refresh before anything happens.
    const int AutoSeconds = 30;
    /// The poll under the file watchers: live windows' transcripts are read from where they were last read, and a nudge
    /// that had to wait is tried again. Local only, no engine call.
    const int WatchMs = 2000;
    /// A window stopped at a limit is moved only after this much keyboard quiet anywhere, so nobody is closed mid-keystroke.
    static readonly TimeSpan TypingPause = TimeSpan.FromSeconds(5);
    int TimerMs => (_settings.AutoSwitch ? Math.Min(_settings.RefreshSeconds, AutoSeconds) : _settings.RefreshSeconds) * 1000;
    QuotaAlerts _quota = new();

    public TrayApp(Engine engine, bool minimized)
    {
        _engine = engine;
        _limits = new(() => _snap, ResumeStopped.DefaultLogin, () => LiveOrLaunching(),
            _engine.ImportUsageAsync, _engine.SwitchNowAsync, _reported, Program.Log, threshold: () => Threshold, hysteresis: () => _engineHysteresis ?? 10);
        _limitWatch = new(hit =>
        {
            try { _ui.Post(_ => _ = OnLimitAsync(hit), null); }
            catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { } // exiting: the UI is gone
        }, Program.Log);
        _tray = new NotifyIcon { Icon = TrayIcons.App, Text = "Patch Goblins", Visible = true, ContextMenuStrip = _menu };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleDashboard(); };
        _menu.Opening += (_, _) => BuildMenu();

        _dash.SwitchRequested += a => _ = SwitchAsync(a);
        _dash.TerminalRequested += a => _ = AccountActionAsync(a, "terminal-folder");
        _dash.AccountAction += (a, act) => _ = AccountActionAsync(a, act);
        _dash.RefreshRequested += () => _ = TickAsync(runAuto: false, refresh: true);
        _dash.SettingsRequested += () => { if (_dash.SettingsOpen) _dash.CloseSettings(); else _ = ShowSettingsAsync(); };
        _dash.AddRequested += PickProviderToAdd;
        _dash.VisibleChanged += (_, _) => { if (!_dash.Visible) _dashHiddenAt = DateTime.UtcNow; };
        _dash.PinToggled += pinned => { _settings.Pinned = pinned; SavePinnedPosition(); };
        _dash.Moved += SavePinnedPosition;
        _dash.AgentsRequested += (anchor, menu) => { if (menu) ShowAgentsMenu(anchor); else LaunchAgents(); };

        _ = Task.Run(HardenStores); // the first run after a move re-permissions every file under the data folder (worktrees too): minutes, never on the UI thread
        if (AgentStore.HasWork(AgentStore.DefaultRoot)) EnsureAgents(); // authorized earlier: open terminals link up, other runs stop
        _timer.Interval = TimerMs;
        _timer.Tick += (_, _) => _ = TickAsync(runAuto: true);
        _timer.Start();
        _watch.Interval = WatchMs;
        _watch.Tick += (_, _) => _ = WatchAsync();
        _watch.Start();
        _ = TickAsync(runAuto: true);
        if (_settings.Pinned)
        {
            _dash.SetPinned(true);
            RenderDash();
            _dash.ShowPinnedAt(_settings.PinnedX, _settings.PinnedY);
        }
        else if (!minimized) _dash.ShowNearTray();
    }

    void HardenStores()
    {
        foreach (var dir in new[] { StoreSecurity.BackupRoot, DockSettings.Dir })
        {
            try { if (!Directory.Exists(dir) || !StoreSecurity.IsHardened(dir)) StoreSecurity.Harden(dir); }
            catch (Exception e) { _ui.Post(_ => Notify("Could not restrict folder permissions", $"{dir}: {e.Message}", ToolTipIcon.Warning), null); }
        }
        ClaudeCli.PurgeEnrollLeftovers(ClaudeCli.EnrollRoot);
    }

    void ToggleDashboard()
    {
        if (_dash.Visible && _dash.Pinned) { _dash.Activate(); return; }
        if (_dash.Visible) { _dash.Hide(); return; }
        // A click on the tray first deactivates (hides) the flyout; don't reopen it.
        if ((DateTime.UtcNow - _dashHiddenAt).TotalMilliseconds < 300) return;
        ShowDashboard();
    }

    void ShowDashboard()
    {
        _ = TickAsync(runAuto: false, refresh: true); // opening it is when freshness matters
        RenderDash();
        if (_dash.Pinned) { _dash.ShowPinnedAt(_settings.PinnedX, _settings.PinnedY); _dash.Activate(); }
        else _dash.ShowNearTray();
    }

    void SavePinnedPosition()
    {
        if (_dash.Pinned) (_settings.PinnedX, _settings.PinnedY) = (_dash.Left, _dash.Top);
        _settings.Save();
    }

    async Task TickAsync(bool runAuto, bool refresh = false)
    {
        if (_busy)
        {
            _pending = refresh || _pending == true; // one re-run right after the current pass
            _pendingAuto |= runAuto;
            return;
        }
        _busy = true;
        _dash.SetStatus(DashboardForm.Updating);
        var fresh = new List<(string Provider, Snapshot Snap)>(); // this pass's readings only; a failed poll never feeds the quota alerts
        try
        {
            if (runAuto && _settings.AutoSwitch)
            {
                await ReportLimitsAsync(); // the engine learns of a limit the API just gave before it decides
                await AutoTickAsync();
            }
            var reread = _settings.AutoPrime && Prime.WantsReread(DateTimeOffset.Now);
            var switches = _switches;
            var listed = await _engine.ListAsync(refresh || reread);
            if (switches == _switches || _snap == null) _snap = listed;
            else (_pending, _pendingAuto) = (refresh || _pending == true, _pendingAuto | runAuto); // listed before a switch the tray made: listed again
            _line.Observe(listed.Accounts, DateTimeOffset.Now); // the automatic line's burn rates
            if (reread) Prime.Reread(DateTimeOffset.Now); // spent only on a reading that came back
            if (_settings.AutoPrime && Prime.Observe(_snap.Accounts, DateTimeOffset.Now)) Prime.Save();
            if (_settings.AutoPrime && Prime.Problem != _primeProblem && (_primeProblem = Prime.Problem) != null)
                Notify("Auto-Prime paused", _primeProblem, ToolTipIcon.Warning);
            fresh.Add(("claude", _snap));
            if (runAuto && _settings.AutoSwitch) await ResumeStoppedAsync(_snap);
            await UpdateAgentAccountsAsync();
            _error = null;
            _dash.SetStatus($"{DateTime.Now:HH:mm}");
        }
        catch (Exception e)
        {
            _error = Engine.Redact(e.Message);
            _dash.SetStatus(DashboardForm.Offline);
        }
        try { await PollProvidersAsync(refresh, fresh); }
        finally
        {
            _busy = false;
        }
        if (_settings.QuotaNotifications
            && QuotaAlerts.Balloon(fresh.SelectMany(f => _quota.Observe(f.Provider, f.Snap.Accounts)).ToList()) is { } balloon)
            Notify(balloon.Title, balloon.Text);
        // Tray dot and tooltip follow Claude, unless Claude is unused and exactly one other provider is.
        var withAccounts = _others.Values.Where(o => o.Snap is { Accounts.Count: > 0 }).ToList();
        var basis = _snap is not { Accounts.Count: > 0 } && withAccounts.Count == 1 ? withAccounts[0].Snap : _snap;
        var active = basis?.Accounts.FirstOrDefault(a => a.Active);
        var old = _tray.Icon;
        _tray.Icon = TrayIcons.WithDot(active?.ShownUsage == null ? null : Format.Peak(active), _settings.AutoSwitch);
        if (old != TrayIcons.App) old?.Dispose();
        _tray.Text = Format.Tooltip(basis, _settings.AutoSwitch);
        if (_dash.Visible) RenderDash();
        if (_pending is { } again)
        {
            var auto = _pendingAuto;
            (_pending, _pendingAuto) = (null, false);
            _ = TickAsync(runAuto: auto, refresh: again);
        }
    }

    /// The live login, as listed; a default-login stop is charged to it only while the engine's record agrees (an in-app /login, or a switch not listed yet, parts them).
    /// Accounts with a `cswap run` window, and those a window was just reopened on (its marker appears a few seconds later).
    HashSet<int> LiveOrLaunching()
    {
        var live = ResumeStopped.LiveProfiles(ResumeStopped.ConfigDirs(null));
        live.UnionWith(ResumeStopped.Launching(_resumed, DateTime.UtcNow)); // the UI thread's, as is every writer of it
        return live;
    }

    List<StoppedSession> ScanStops(Snapshot? snap, bool includeBusy = false)
    {
        var (recorded, changed) = ResumeStopped.DefaultLogin();
        var live = snap?.ActiveAccountNumber;
        foreach (var (id, l) in _agentLimits) if (l.ResetsAt?.UtcDateTime <= DateTime.UtcNow) _agentLimits.TryRemove(id, out _); // reset: no longer a stop
        return ResumeStopped.Scan(ResumeStopped.ConfigDirs(live), DateTime.UtcNow, recorded == live ? changed : DateTime.MaxValue, includeBusy, _agentLimits);
    }

    /// The net under the file watchers, every couple of seconds: watchers are (re)started, live windows' files are read from
    /// where they were last read (a missed event, or an overflow, costs nothing), and a nudge that had to wait is retried.
    async Task WatchAsync()
    {
        if (_watching || !_settings.AutoSwitch) return;
        _watching = true;
        try
        {
            await Task.Run(() =>
            {
                var dirs = ResumeStopped.ConfigDirs(null);
                _limitWatch.Sync(dirs.Select(d => d.Dir));
                _limitWatch.Poll(ResumeStopped.LiveTranscripts(dirs, DateTime.UtcNow));
            });
            if (_retry && _snap != null && _engineThreshold != null) await ResumeStoppedAsync(_snap); // the pass reads the threshold itself: not every 2 s while that fails
        }
        catch (Exception e) { Program.Log(e); }
        finally { _watching = false; }
    }

    /// A usage-limit error was just written (any window, subagent or workflow agent): the default login leaves the refused
    /// account within a second or two, from the latest reading, then windows stopped at the limit are reopened. Nothing
    /// waits for a refresh, a tray pass or a quiet spell.
    async Task OnLimitAsync(LimitHit hit)
    {
        if (!_settings.AutoSwitch) return;
        try
        {
            // A hit in a subagent or workflow file stops its window too: mark the session for the move pass.
            if (ResumeStopped.AgentSession(hit.ConfigDir, hit.File) is { } agentOf) _agentLimits[agentOf] = hit.Limit;
            if (_snap == null) { _ = TickAsync(runAuto: true); return; } // no reading yet: the tray pass lists, then looks at the windows
            var o = await _limits.OnLimitAsync(hit); // null: seen before; the windows are still looked at
            if (o?.Switched is { Switched: true } sw)
            {
                _lastBlockedNotice = null;
                Notify("", Format.SwitchNotice(sw.From, sw.To), ToolTipIcon.None);
                // The list is re-read at the next pass; until then the reopened windows must see where the default login is.
                if (sw.To?.Number is { } to) Switched(to);
            }
            else if (o is { NoRoom: true } && o.Earliest != _lastExhaustedNotice)
            {
                _lastExhaustedNotice = o.Earliest;
                Notify("All accounts near their limits", o.Earliest is { } t ? $"Earliest reset {Format.When(t, DateTimeOffset.Now)}." : "No reset time known.", ToolTipIcon.Warning);
            }
            else if (o?.Error != null)
            {
                if (o.Error != _lastBlockedNotice) // once, not per refusal: every window and agent keeps hitting it until the login moves
                {
                    _lastBlockedNotice = o.Error;
                    Notify("Auto-switch can't move the default login", o.Error, ToolTipIcon.Warning);
                }
                _ = TickAsync(runAuto: true); // the engine's own check, with its usage fetch; a burst coalesces into one re-run
            }
            if (_snap != null) await ResumeStoppedAsync(_snap);
        }
        catch (Exception e) { Program.Log(e); _ = TickAsync(runAuto: true); }
    }

    /// Hands the engine the limits the API gave stopped windows (`cswap import-usage -`), so its next check sees the
    /// account full and switches at once instead of waiting for its own usage reading.
    async Task ReportLimitsAsync()
    {
        if (_snap == null) return;
        try
        {
            var stops = await Task.Run(() => ScanStops(_snap));
            if (ResumeStopped.LimitReport(stops, _snap.Accounts, DateTimeOffset.UtcNow, _reported) is { } doc)
                await _engine.CommandWithInputAsync(doc, "import-usage", "-");
        }
        catch (Exception e) when (e is EngineException or IOException) { Program.Log(e); }
    }

    async Task PollProvidersAsync(bool refresh, List<(string Provider, Snapshot Snap)> fresh)
    {
        foreach (var p in ProviderCli.All)
        {
            if (!ProviderCli.Present(p)) { _others.Remove(p); continue; }
            try
            {
                var s = await _engine.ListAsync(refresh, p);
                _others[p] = (s, null);
                fresh.Add((p, s));
            }
            catch (Exception e) { _others[p] = (_others.GetValueOrDefault(p).Snap, Engine.Redact(e.Message)); }
        }
    }

    Dictionary<string, Job> _jobs = [];
    HashSet<int> _live = [];

    void RenderDash()
    {
        _jobs = TeamJobs();
        _dash.SetTeam(Jobs.Summary(_jobs.Values, _agents?.Repos ?? []));
        _dash.Render(_snap, _error, ProviderCli.All.Where(_others.ContainsKey).Select(p => (p, _others[p].Snap, _others[p].Error)).ToList(), ProjectMap.Load(), _jobs);
    }

    /// What each Claude account is doing: an agent run, the owner's own use, rest, or nothing.
    Dictionary<string, Job> TeamJobs() =>
        _snap is { } s ? Jobs.For(s.Accounts, _agents?.Repos ?? [], _live, Threshold, DateTimeOffset.Now, a => _agents?.CooledUntil(a)) : [];

    /// The agents moved between ticks: the header's line follows at once, and the flyout is redrawn only if a job line changed.
    void RefreshTeam()
    {
        _dash.SetTeam(Jobs.Summary(_jobs.Values, _agents?.Repos ?? []));
        var jobs = TeamJobs();
        if (jobs.Count == _jobs.Count && jobs.All(kv => _jobs.TryGetValue(kv.Key, out var j) && j == kv.Value)) return;
        RenderDash();
    }

    /// "+" with several CLIs installed: a small themed menu picks which provider to add.
    void PickProviderToAdd()
    {
        var installed = ProviderCli.All.Where(p => ProviderCli.Find(p) != null).ToList();
        if (installed.Count == 0) { _ = EnrollAsync(null); return; }
        var menu = new ContextMenuStrip();
        foreach (var p in installed.Prepend("claude"))
            menu.Items.Add(ProviderCli.Title(p) + " account…", null, (_, _) => _ = EnrollAsync(null, p));
        MenuRenderer.Apply(menu);
        MenuRenderer.DisposeOnClose(menu);
        menu.Show(Cursor.Position);
    }

    async Task AutoTickAsync()
    {
        // Counted before it waits: a move not yet at its check drops itself, one past it finishes its launch before the engine may pick that account.
        Interlocked.Increment(ref _engineSwitches);
        List<AutoEvent> events;
        try
        {
            await _movePass.WaitAsync();
            _movePass.Release();
            (_, events) = await _engine.AutoOnceAsync(_settings.AutoLine ? Threshold : null);
        }
        finally { Interlocked.Decrement(ref _engineSwitches); }
        foreach (var e in events)
        {
            switch (e.Event)
            {
                case "switch" when !e.DryRun:
                    _lastBlockedNotice = null;
                    Notify("", Format.SwitchNotice(e.From, e.To), ToolTipIcon.None);
                    break;
                case "account-quarantined":
                    Notify("Account needs re-enrollment", $"{e.Email}: {e.Reason}", ToolTipIcon.Warning);
                    break;
                // The engine never moves the default login onto an account a pinned terminal is using
                // (two copies of one rotating login). Say so once, rather than leave a silent stall:
                // windows stopped at the limit are reopened on that account instead, and the account is freed once its windows are idle.
                case "no-switch" when e.Reason == "no-viable-target" && _lastBlockedNotice != e.Reason:
                case "error" when _lastBlockedNotice != e.Message:
                    _lastBlockedNotice = e.Event == "error" ? e.Message : e.Reason;
                    Notify("Auto-switch can't move the default login",
                        e.Event == "error" ? e.Message ?? "engine error"
                            : "Every account with room left is in use by a pinned terminal. Windows stopped at a limit are reopened there instead; once that terminal is idle, it is moved to the default login and the default login follows.",
                        ToolTipIcon.Warning);
                    break;
                case "all-exhausted" when e.EarliestResetAt != _lastExhaustedNotice:
                    _lastExhaustedNotice = e.EarliestResetAt;
                    Notify("All accounts near their limits",
                        e.EarliestResetAt is { } t ? $"Earliest reset {Format.When(t, DateTimeOffset.Now)}." : "No reset time known.",
                        ToolTipIcon.Warning);
                    break;
            }
        }
    }

    /// Reopens windows stopped at a usage limit, and moves `cswap run` windows whose account reached the
    /// switch threshold once their turn is done (see ResumeStopped), onto an account with room.
    async Task ResumeStoppedAsync(Snapshot snap)
    {
        await _movePass.WaitAsync();
        // The caller's snapshot may predate a switch made while this pass waited: a reopened window must see where the default login is now.
        try
        {
            (_held, var shown) = (null, _notices);
            await MovePassAsync(_snap ?? snap);
            // Not yet told if anything else showed: the next quiet pass tells it, while a free of that account is still due.
            if (_held is { } h && _notices == shown && _heldNotice.Add(h.N))
                Notify($"#{h.N} is held by a pinned terminal", h.Text, ToolTipIcon.Warning);
        }
        finally { _movePass.Release(); }
    }

    async Task MovePassAsync(Snapshot snap)
    {
        List<StoppedSession> stopped;
        try { stopped = await Task.Run(() => ScanStops(snap, includeBusy: true)); }
        catch (Exception e) { Program.Log(e); return; }
        if (_engineThreshold == null) await ReadEngineThresholdAsync(); // the free below judges the default login at the engine's line too
        // First, so the default login's stopped windows are reopened where it lands, not in the profile being freed.
        if (await FreePinnedAsync(snap)) { _retry = true; return; } // the next poll's pass sees where the default login is now
        var (threshold, hysteresis) = (Threshold, _engineHysteresis ?? 10);
        var now = DateTimeOffset.UtcNow;
        var load = new Dictionary<int, int>();
        var plan = new List<(StoppedSession S, Account To, string Dir)>();
        var left = new List<string>();
        var login = ResumeStopped.DefaultLogin().Account;
        var retry = false;
        foreach (var s in stopped)
        {
            // The agents terminal is never closed (its bridge flags would not come back): its owner is told once to reopen it.
            if (s.Status == "agents")
            {
                if (s.AtLimit && _told.Add($"{s.SessionId}@{s.Limit?.At.Ticks}"))
                    Notify("Agents terminal stopped at a limit", $"The agents terminal in {Path.GetFileName(s.Cwd.TrimEnd('\\'))} (#{s.Account}) refuses every prompt until its reset. Close it and open it again from the Agents menu to carry on.", ToolTipIcon.Warning);
                continue;
            }
            if (plan.Any(p => p.S.SessionId == s.SessionId)) continue; // one live window per session moves per pass
            var target = s.Movable || _settings.MoveWorkflows && s.Running ? ResumeStopped.Target(s, snap.Accounts, n => ResumeStopped.ProfileDir(n) != null, threshold, hysteresis, now, load, _limits.RefusedUntil, scopedRefused: _limits.ScopedRefusedUntil,
                line: _settings.AutoLine ? a => _line.For(a, s.Model) : null) : null;
            // One try per session per account per ten minutes, kept across restarts: a reopened window that stops again waits for room, not a loop.
            var key = $"{s.SessionId}#{target}";
            var step = ResumeStopped.Decide(s, ResumeStopped.UserIdle(), TypingPause, target, _resumed.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(10),
                ResumeStopped.MovesInLastHour(_resumed, s.SessionId, DateTime.UtcNow), () => ResumeStopped.Unchanged(s, workflows: _settings.MoveWorkflows), _settings.MoveWorkflows, DateTime.UtcNow);
            switch (step)
            {
                case ResumeStopped.Step.Left: // never closed while working; reopened once it is idle at the limit (the poll looks again)
                    // The grace is per error (a window refused again gets its own), the balloon per reset: every agent of a busy window is refused
                    // on its own. A window still marked busy just after writing its error is about to go idle: told about only if it stays busy past the grace.
                    var told = $"{s.SessionId}@{s.Limit?.ResetsAt ?? s.Limit?.At}";
                    if (!_told.Contains(told) && ResumeStopped.BusyPast(_busySince, $"{s.SessionId}@{s.Limit?.At.Ticks}", DateTime.UtcNow, BusyGrace) && _told.Add(told))
                    {
                        // A default-login window's account is the live login, not the one that refused: no number for it.
                        left.Add(Path.GetFileName(s.Cwd.TrimEnd('\\')) + (s.ConfigDir == ResumeStopped.DefaultDir ? "" : $" (#{s.Account})"));
                        Program.Log($"nudge {s.SessionId} left {s.Status}");
                    }
                    retry = true;
                    break;
                case ResumeStopped.Step.Wait:
                    retry |= s.AtLimit;
                    break;
                case ResumeStopped.Step.NoRoom when s.AtLimit: // a finished turn below the threshold is just fine
                    Skip(s, "no account has room");
                    break;
                case ResumeStopped.Step.Recent:
                    Skip(s, $"reopened on #{target} under ten minutes ago");
                    break;
                case ResumeStopped.Step.Capped:
                    if (Skip(s, $"moved {ResumeStopped.MovesPerHour} times in the last hour"))
                        Notify("A window keeps stopping at limits", $"{Path.GetFileName(s.Cwd.TrimEnd('\\'))} was moved {ResumeStopped.MovesPerHour} times in the last hour and stays put.", ToolTipIcon.Warning);
                    break;
                case ResumeStopped.Step.Move:
                    var account = snap.Accounts.First(a => a.Number == target);
                    _resumed[key] = DateTime.UtcNow;
                    load[target!.Value] = load.GetValueOrDefault(target.Value) + 1;
                    plan.Add((s, account, account.Active ? ResumeStopped.DefaultDir : ResumeStopped.ProfileDir(target.Value)!));
                    break;
            }
        }
        _retry = retry;
        if (left.Count > 0)
            Notify("Left running at a limit", $"{string.Join(", ", left)}: busy, so not closed. They cannot carry on until reopened: each is reopened on an account with room once it is idle (or close it yourself and resume).");
        if (plan.Count == 0) return;
        try { ResumeStopped.SaveMoves(MovesPath, _resumed, DateTime.UtcNow); } catch (Exception e) { Program.Log(e); }
        var done = await Task.WhenAll(plan.Select(m => MoveAsync(m.S, m.To, m.Dir, login))); // windows are independent
        var changed = false;
        foreach (var (m, d) in plan.Zip(done))
        {
            if (d == null) changed |= _resumed.Remove($"{m.S.SessionId}#{m.To.Number}"); // not tried after all: no lockout
            else { _agentLimits.TryRemove(m.S.SessionId, out _); _limits.Forget(m.S.SessionId); _told.RemoveWhere(t => t.StartsWith(m.S.SessionId + "@")); _resumed[ResumeStopped.MoveStamp(m.S.SessionId, DateTime.UtcNow)] = DateTime.UtcNow; changed = true; } // one per move, for the cap per hour
        }
        if (changed) try { ResumeStopped.SaveMoves(MovesPath, _resumed, DateTime.UtcNow); } catch (Exception e) { Program.Log(e); }
        var resumed = done.Where(d => d is { AtLimit: true }).Select(d => d!.Value.Label).ToList();
        var moved = done.Where(d => d is { AtLimit: false }).Select(d => d!.Value.Label).ToList();
        if (resumed.Count > 0)
            Notify(resumed.Count == 1 ? "Reopened a window stopped at a limit" : $"Reopened {resumed.Count} windows stopped at a limit", string.Join(", ", resumed));
        if (moved.Count > 0)
            Notify(moved.Count == 1 ? "Moved a window before its limit" : $"Moved {moved.Count} windows before their limit", string.Join(", ", moved));
    }

    async Task<(bool AtLimit, string Label)?> MoveAsync(StoppedSession s, Account account, string dir, int? login)
    {
        try
        {
            // Copied before anything opens (a failed copy leaves the window untouched), then again just before the new window launches.
            // The default login can change hands before `cswap run` looks (the engine, a limit hit), and it then runs in ~/.claude
            // instead of the profile: the transcript is put where either would look.
            string[] dirs = [.. new[] { dir, ResumeStopped.DefaultDir, ResumeStopped.ProfileDir(account.Number) }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
            // The copies carry the old errors again: remembered, they are not news, while a new refusal written there still is.
            if (s.Limit is { } old) _limitWatch.Remember(old.At);
            if (_agentLimits.TryGetValue(s.SessionId, out var sub)) _limitWatch.Remember(sub.At);
            void Copy() { foreach (var d in dirs) ResumeStopped.CopyTranscript(s, d); }
            await Task.Run(Copy);
            // The copy can take long: someone may have typed, or the window closed, or an account been refused and the login moved on, meanwhile.
            // A switch still running counts as the login moving: launching the account it moves to would leave two live copies of it.
            if (!ResumeStopped.Unchanged(s, workflows: _settings.MoveWorkflows) || ResumeStopped.UserIdle() < (s.AtLimit ? TypingPause : TimeSpan.FromSeconds(60))) return null;
            if (!ResumeStopped.SafeToClose(account.Number, _limits.RefusedUntil, _limits.Switching || _engineSwitches > 0, login, ResumeStopped.DefaultLogin().Account, DateTimeOffset.UtcNow))
            {
                Program.Log($"move {s.SessionId} to #{account.Number} dropped: the login or the limits moved since the plan");
                return null;
            }
            _resumed[$"{s.SessionId}#{account.Number}"] = DateTime.UtcNow; // before the launch, which can wait 10 s: a concurrent switch must not pick this account meanwhile
            var spot = WindowSpot.Of(s.Pid); // the new window opens where this one is, on top of it
            // Workflows running (MoveWorkflows, `Paused`): the old window is closed first, as its runs must stop before the new one resumes them.
            var args = Launch.Run(account.Number, _settings.PreserveRemoteControl, ResumeStopped.ResumeArgs(s, s.Running ? dir : null));
            var up = false;
            async Task<bool> Open()
            {
                up = await WindowSpot.OpenAtAsync(spot, () =>
                {
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            _engine.LaunchConsole($"Claude · {account.ShortName} · {(s.AtLimit ? "resumed" : "moved")}", s.Cwd, args);
                            break;
                        }
                        catch (Exception e) when (attempt == 0) { Program.Log(e); } // once more
                    }
                });
                return up;
            }
            async Task<bool> OpenPaused()
            {
                try { await Open(); return true; } // a window not seen still opened
                catch (Exception e) { Program.Log(e); return false; }
            }
            // The new window first, the old one only once it is up: a launch that fails leaves the old window as it was.
            var swapped = await ResumeStopped.SwapAsync(s.Running,
                async () =>
                {
                    await Task.Run(Copy); // what the transcript holds now: the new window then owns it
                    _resumed[$"{s.SessionId}#{account.Number}"] = DateTime.UtcNow; // the launching window counts from the launch, not the plan
                },
                s.Running ? OpenPaused : Open,
                // Someone typed or a turn started in the old window while the new one opened: it is not closed under them.
                () => ResumeStopped.Unchanged(s) && ResumeStopped.UserIdle() >= TimeSpan.FromSeconds(60),
                () => ResumeStopped.CloseAsync(s.Pid, s.Record));
            var folder = Path.GetFileName(s.Cwd.TrimEnd('\\'));
            if (s.Running)
            {
                var runs = string.Join(", ", s.Runs!.Select(r => r.RunId));
                Program.Log($"workflows {runs} in {folder} paused: #{s.Account} to #{account.Number}{(swapped == true ? "" : ", relaunch failed")}");
                if (swapped != true)
                {
                    Notify("Could not reopen a paused workflow", $"Run {runs} again in {s.Cwd}: cswap {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}", ToolTipIcon.Warning);
                    return null;
                }
            }
            else if (swapped == null)
            {
                Program.Log($"old window of {s.SessionId} kept: {(up ? "it was used while the new one opened" : "the new window was not seen")}");
                Notify("Opened a new window; the old one is still open", $"{folder} is on #{account.Number} in the new window. Close whichever you do not want.", ToolTipIcon.Warning);
                return (s.AtLimit, $"{folder} on #{account.Number}");
            }
            else Program.Log($"nudge {s.SessionId} relaunched #{s.Account} on #{account.Number}");
            return (s.AtLimit, $"{folder} on #{account.Number}");
        }
        catch (Exception e)
        {
            Program.Log(e);
            Error("Could not reopen a stopped window", e); // the old window is still there: nothing closed before the new one was up
            return null;
        }
    }

    /// Frees the `cswap run` account the default login needs (see `ResumeStopped.FreeTarget`) before it sits at its limit: the
    /// profile's windows, every one idle, are closed; once nothing runs on the profile (each wrapper's exit seal hands the login
    /// back to the backup), the default login switches there, `--exclusive` so anything started on it since refuses it, and the
    /// windows are reopened with `cswap run N`, which on the default login's own account runs in ~/.claude. A refused switch
    /// reopens them in the profile, where they were. True when windows were closed.
    async Task<bool> FreePinnedAsync(Snapshot snap)
    {
        var (threshold, hysteresis) = (Threshold, _engineHysteresis ?? 10);
        var due = ResumeStopped.FreeTargets(snap.Accounts, LiveOrLaunching(), _limits.RefusedUntil, DateTimeOffset.UtcNow, threshold, hysteresis);
        if (due.Count == 0) _heldNotice.Clear(); // nothing due: a later stall is news again
        foreach (var n in due) // one a working window holds must not hide the next
            if (await FreePinnedAsync(snap, n)) return true;
        return false;
    }

    async Task<bool> FreePinnedAsync(Snapshot snap, int n)
    {
        // A free that is due but held back is said once, like a working window: the default login waits at its limit meanwhile.
        void Held(string text) { if (!_heldNotice.Contains(n)) _held = (n, text); }
        if (ResumeStopped.ProfileDir(n) is not { } dir) return false;
        if (_freed.TryGetValue(n, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(30)) // once per half hour per account: never a loop
        {
            Held($"#{n} was freed for the default login under half an hour ago and is in use again, so the default login stays at its limit. #{n} is freed again {Format.When(new DateTimeOffset(last.AddMinutes(30)), DateTimeOffset.UtcNow)} at the earliest.");
            return false;
        }
        List<StoppedSession> windows;
        var account = snap.Accounts.First(a => a.Number == n);
        bool agents;
        try
        {
            windows = await Task.Run(() => ResumeStopped.Scan([(dir, n)], DateTime.UtcNow, includeBusy: true, agentLimits: _agentLimits));
            var (records, runs) = await Task.Run(() => ResumeStopped.LiveCounts(dir));
            // Every window idle and each run without one an agent's: the agents yield the account, as at a usage limit.
            agents = ResumeStopped.AgentsOnly(windows, records, runs, _agents?.RunsOn(account) ?? 0);
            if (!agents && !ResumeStopped.Releasable(windows, records, runs))
            {
                // Never closed while working; the user is the only one who can free it sooner, so say once what holds it.
                if (ResumeStopped.Holders(windows, records, runs) is { Count: > 0 } held)
                    Held($"{string.Join(", ", held)} keeps #{n}, so the default login stays at its limit. " + (windows.Any(w => w.Status == "agents")
                        ? $"It never moves on its own: close the agents terminal to free #{n}, then open it again from the Agents menu."
                        : windows.Count == records && windows.All(w => w.Idle)
                        ? _agents?.RunsOn(account) > 0 // no window to close: only runs hold it
                            ? $"It moves to #{n} once that run ends; pausing agents frees #{n} sooner."
                            : $"It moves to #{n} once that run ends; stop the headless `cswap run {n}` to free #{n} sooner."
                        : $"It moves to #{n} once that terminal is idle; finish or close it to free #{n} sooner."));
                return false;
            }
            if (windows.Count > 0 && ResumeStopped.UserIdle() < TimeSpan.FromSeconds(60))
            {
                Held($"#{n}'s idle windows move to the default login once nobody has typed for a minute; until then the default login stays at its limit.");
                return false;
            }
            if (windows.FirstOrDefault(w => ResumeStopped.MovesInLastHour(_resumed, w.SessionId, DateTime.UtcNow) >= ResumeStopped.MovesPerHour
                    || _resumed.TryGetValue($"{w.SessionId}#{n}", out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(10)) is { } recent) // just moved here: not closed again so soon
            {
                Held($"{Path.GetFileName(recent.Cwd.TrimEnd('\\'))} was just moved to #{n} and is not closed again so soon, so the default login stays at its limit. Close that window to free #{n} sooner.");
                return false;
            }
            foreach (var w in windows) ResumeStopped.CopyTranscript(w, ResumeStopped.DefaultDir); // a failed copy closes nothing
        }
        catch (Exception e) { Program.Log(e); return false; }
        Program.Log($"freeing #{n} for the default login: {windows.Count} idle window(s)" + (agents ? ", agent runs yield it" : ""));
        var closed = new List<StoppedSession>();
        var spots = new Dictionary<string, WindowSpot?>();
        foreach (var w in windows)
        {
            // Someone typed, or a turn started: that window keeps the profile, so the switch refuses and the closed ones go back.
            if (!ResumeStopped.Unchanged(w) || ResumeStopped.UserIdle() < TimeSpan.FromSeconds(60)) break;
            if (w.Limit is { } old) _limitWatch.Remember(old.At);
            spots[w.SessionId] = WindowSpot.Of(w.Pid);
            try { await ResumeStopped.CloseAsync(w.Pid, w.Record); closed.Add(w); }
            catch (Exception e) { Program.Log(e); break; }
        }
        // The agents yield only once every window is closed: stopped for a switch a window still blocks, their work would be lost for nothing.
        var yielded = agents && closed.Count == windows.Count;
        if (closed.Count == 0 && !yielded) return false; // nothing closed: no lockout, the next idle poll tries again
        _freed[n] = DateTime.UtcNow; // the agents rest #n as long: one free per half hour, never a loop
        if (yielded)
            try { await _agents!.YieldAsync(account, TimeSpan.FromMinutes(30)); }
            catch (Exception e) { Program.Log(e); } // the runs keep #n: the switch is not made and the closed windows go back
        // Each wrapper's exit seal hands the login back first: the default login never runs a second live copy of it.
        var free = false;
        for (var i = 0; i < 60 && !(free = await Task.Run(() => ResumeStopped.LiveProfiles([(dir, n)]).Count == 0)); i++) await Task.Delay(500);
        SwitchResult? sw = null;
        if (free)
            try { sw = await _engine.SwitchNowAsync(n); }
            catch (Exception e) { Program.Log($"free #{n}: switch refused: {Engine.Redact(e.Message)}"); }
        foreach (var w in closed)
        {
            try { ResumeStopped.CopyTranscript(w, ResumeStopped.DefaultDir); } catch (Exception e) { Program.Log(e); } // what it wrote on the way out
            _resumed[$"{w.SessionId}#{n}"] = _resumed[ResumeStopped.MoveStamp(w.SessionId, DateTime.UtcNow)] = DateTime.UtcNow;
            await WindowSpot.OpenAtAsync(spots.GetValueOrDefault(w.SessionId), () => _engine.LaunchConsole($"Claude · {account.ShortName} · moved", w.Cwd,
                Launch.Run(n, _settings.PreserveRemoteControl, ResumeStopped.ResumeArgs(w))));
            Program.Log($"nudge {w.SessionId} relaunched on #{n}");
        }
        try { ResumeStopped.SaveMoves(MovesPath, _resumed, DateTime.UtcNow); } catch (Exception e) { Program.Log(e); }
        if (sw is { Switched: true })
        {
            _lastBlockedNotice = null;
            // One balloon (a second replaces the first): the switch, that these windows are no longer pinned, and where the agents went.
            var gave = yielded ? $" Agents gave #{n} up and continue their work on another account." : "";
            if (closed.Count == 0) Notify($"Agents freed #{n} for the default login", $"{Format.SwitchNotice(sw.From, sw.To)}.{gave}");
            else Notify(closed.Count == 1 ? "Moved a pinned window to the default login" : $"Moved {closed.Count} pinned windows to the default login",
                $"{Format.SwitchNotice(sw.From, sw.To)}. {string.Join(", ", closed.Select(w => Path.GetFileName(w.Cwd.TrimEnd('\\'))))} now follow the default login, no longer pinned to #{n}.{gave}");
            Switched(n);
        }
        return true;
    }

    /// The tray just moved the default login to `n`: the listed state says so at once, and no list started before it replaces it.
    void Switched(int n)
    {
        _switches++;
        if (_snap is { } s) _snap = s with { ActiveAccountNumber = n, Accounts = [.. s.Accounts.Select(a => a with { Active = a.Number == n })] };
    }

    /// Why a stopped window was left alone, logged once per session and reason.
    bool Skip(StoppedSession s, string reason)
    {
        if (!_skipped.Add($"{s.SessionId} {reason}")) return false;
        Program.Log($"left {s.SessionId} (#{s.Account}) stopped: {reason}");
        return true;
    }

    static string MovesPath => Path.Combine(DockSettings.Dir, "resumed.json");

    static string Name(AccountRef? r) => r == null ? "(none)" : $"#{r.Number} {r.Email}";

    /// A small Patch Goblins card at the bottom right (not Windows' balloon); a click opens the flyout.
    void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notices++;
        Toast.Show(title, Engine.Redact(text), icon switch { ToolTipIcon.Warning => Palette.Warn, ToolTipIcon.Error => Palette.Critical, _ => null }, ShowDashboard);
    }

    void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add("Open dashboard", null, (_, _) => ShowDashboard());

        MenuHeader("Team");
        var sw = new ToolStripMenuItem("Switch to");
        foreach (var a in _snap?.Accounts ?? [])
        {
            var label = $"#{a.Number} {(string.IsNullOrEmpty(a.Alias) ? a.Email : a.Alias)}";
            sw.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => _ = SwitchAsync(a)) { Checked = a.Active, Enabled = !a.Active });
        }
        sw.Enabled = sw.DropDownItems.Count > 0;
        _menu.Items.Add(sw);
        _menu.Items.Add("Add account…", null, (_, _) => _ = EnrollAsync(null));

        MenuHeader("Work");
        _menu.Items.Add("New agent", null, (_, _) => OpenNewAgent());
        if (_agents?.Repos.Count > 0)
        {
            var agents = new ToolStripMenuItem("Agents");
            AddAgentItems(agents.DropDownItems);
            _menu.Items.Add(agents);
        }
        // The folder's assigned account if it has one, else the default login: the dashboard's Terminal path.
        _menu.Items.Add("Open terminal", null, (_, _) => OpenFolderTerminal());
        var proj = new ToolStripMenuItem("Project terminals");
        foreach (var m in ProjectMap.Load())
            proj.DropDownItems.Add(new ToolStripMenuItem($"{m.Leaf} · {m.Email}", null, (_, _) => OpenProjectTerminal(m)) { ToolTipText = m.Folder });
        if (proj.HasDropDownItems) _menu.Items.Add(proj);
        var cont = new ToolStripMenuItem("Continue interrupted work");
        foreach (var src in ProviderCli.Every)
            foreach (var t in ProviderCli.HandoffTargets(src))
                cont.DropDownItems.Add($"{ProviderCli.Title(src)} → {ProviderCli.Title(t)}…", null, (_, _) => _ = ContinueAsync(src, t));
        cont.Enabled = cont.HasDropDownItems;
        _menu.Items.Add(cont);

        MenuHeader("Upkeep");
        _menu.Items.Add(new ToolStripMenuItem("Auto-switch near limit", null, (_, _) => SetAutoSwitch(!_settings.AutoSwitch)) { Checked = _settings.AutoSwitch });
        if (_settings.AutoPrime && _snap != null) AddPrimeItems(_snap.Accounts);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Refresh now", null, (_, _) => _ = TickAsync(runAuto: false, refresh: true));
        _menu.Items.Add("Settings…", null, (_, _) => _ = ShowSettingsAsync());
        _menu.Items.Add("Exit", null, (_, _) => _ = ExitAsync());
        MenuRenderer.Apply(_menu);
    }

    /// A quiet group name between separators: the same Team / Work / Upkeep as the settings.
    void MenuHeader(string title)
    {
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(title.ToUpperInvariant()) { Enabled = false, Font = Ui.Font(7.5f, semibold: true) });
    }

    Primer? _primer;
    Primer Prime => _primer ??= new(Primer.DefaultPath);
    bool _priming;
    string? _primeProblem;

    /// One click primes every fresh window; the status lines say what happened and what's next.
    void AddPrimeItems(IReadOnlyList<Account> accounts)
    {
        var now = DateTimeOffset.Now;
        var ready = Prime.Ready(accounts, now);
        if (ready.Count > 0)
            _menu.Items.Add(new ToolStripMenuItem($"Prime {ready.Count} fresh 5-hour window{(ready.Count == 1 ? "" : "s")}", null, (_, _) => _ = PrimeAsync(ready))
            {
                Enabled = !_priming,
                ToolTipText = string.Join(", ", ready.Select(a => $"#{a.Number} {a.ShortName}")),
            });
        var status = new ToolStripMenuItem("Auto-Prime");
        foreach (var line in Prime.Status(accounts, now).DefaultIfEmpty("No fresh 5-hour windows right now"))
            status.DropDownItems.Add(new ToolStripMenuItem(line) { Enabled = false });
        _menu.Items.Add(status);
    }

    /// Silent: no window, no transcript, no notification unless something needs the owner.
    async Task PrimeAsync(List<Account> shown)
    {
        if (_priming) return;
        _priming = true;
        var failed = new List<string>();
        try
        {
            // Eligibility from a reading taken now, not the menu's: a window may have started or a limit moved since.
            var snap = await _engine.ListAsync(refresh: true);
            var now = DateTimeOffset.Now;
            if (Prime.Observe(snap.Accounts, now)) Prime.Save();
            foreach (var a in Prime.Ready(snap.Accounts, now, Primer.SendReadingAge).Where(a => shown.Any(s => Primer.Same(s, a))))
            {
                if (!Prime.Begin(a, DateTimeOffset.Now)) break; // unrecorded, a send could prime this window twice
                var (result, detail) = await _engine.PrimeAsync(a);
                Prime.Finish(a, result, detail);
                if (result == PrimeResult.Refused) failed.Add($"#{a.Number} {a.ShortName}: {detail}");
                if (result == PrimeResult.Unknown) failed.Add($"#{a.Number} {a.ShortName}: {detail} (it may have gone out, so not again for this window)");
            }
            if (Prime.Problem != null) failed.Add(Prime.Problem);
        }
        catch (Exception e) { failed.Add(e.Message); } // e.g. no fresh reading: nothing sent
        finally { _priming = false; }
        if (failed.Count > 0) Notify("Auto-Prime", "Could not prime " + string.Join("; ", failed), ToolTipIcon.Warning);
        _ = TickAsync(runAuto: false);
    }

    async Task SwitchAsync(Account a)
    {
        if (a.Provider != "claude")
        {
            // Only the login file is rewritten atomically; running sessions are never touched.
            try
            {
                var pr = await _engine.SwitchAsync(a.Number, a.Provider);
                Notify(pr.Switched ? $"Switched {ProviderCli.Title(a.Provider)} account" : "No switch",
                    pr.Switched ? $"Now using {Name(pr.To)}. {pr.Note}" : pr.Reason ?? "Already active");
            }
            catch (Exception e) { Error("Switch failed", e); }
            await TickAsync(runAuto: false);
            return;
        }
        var running = ClaudeCli.RunningCount();
        if (running > 0 && MessageBox.Show(
                $"{running} Claude Code process(es) are running.\n\n" +
                "Switching never stops them. A response already in progress finishes on the current account; " +
                "sessions on the default login use the new account from their next message (the first one may " +
                "use extra quota while its cache rebuilds). Terminals opened for another account keep it; " +
                "one opened for the active account follows switches.\n\n" +
                $"Switch to #{a.Number} {a.Email}?",
                "Patch Goblins", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            return;
        try
        {
            Interlocked.Increment(ref _engineSwitches);
            SwitchResult r;
            try { r = await _engine.SwitchAsync(a.Number); }
            finally { Interlocked.Decrement(ref _engineSwitches); }
            if (r.Switched) { Switched(a.Number); Notify("", Format.SwitchNotice(r.From, r.To), ToolTipIcon.None); }
            else Notify("No switch", r.Reason ?? "Already active");
        }
        catch (Exception e) { Error("Switch failed", e); }
        await TickAsync(runAuto: false);
    }

    void OpenTerminal(Account a, string? folder)
    {
        try
        {
            if (a.Provider != "claude")
            {
                var title = $"{ProviderCli.Title(a.Provider)} · {a.Email}";
                // The live login runs the plain CLI: `run` refuses it, since two copies would share a single-use refresh token.
                if (a.Active && ProviderCli.Find(a.Provider) is { } cli) Engine.LaunchConsoleWith(cli, title, folder);
                else _engine.LaunchConsole(title, folder, a.Provider, "run", a.Number.ToString());
                return;
            }
            // `cswap run N` pins this console to account N via an isolated profile; global switches
            // and auto-switch never touch it. When N is the active login, the engine runs plain `claude`
            // instead (no second copy of the refresh token), which follows later switches.
            _engine.LaunchConsole($"Claude · {a.ShortName}", folder, Launch.Run(a.Number, _settings.PreserveRemoteControl));
        }
        catch (Exception e) { Error("Could not open terminal", e); }
    }

    /// The tray menu's terminal: in the picked folder, its assigned account, else the default login as it is at launch
    /// (`cswap run` with no account), never one read when the menu opened.
    void OpenFolderTerminal()
    {
        if (PickFolder("Open a terminal in…", _settings.TerminalFolder ?? StoreSecurity.UserProfile) is not { } f) return;
        if (MappedAccount(f) is { } mapped) { OpenTerminal(mapped, f); return; }
        // A mapped folder with "Use project accounts automatically" off: `cswap run` alone would take the mapping, so the login is named.
        if (!_settings.AutoProjectAccount && ProjectMap.AccountFor(f, _snap?.Accounts ?? [], ProjectMap.Load()) != null
            && _snap?.Accounts.FirstOrDefault(a => a.Active) is { } active) { OpenTerminal(active, f); return; }
        try { _engine.LaunchConsole("Claude · default login", f, Launch.Run(null, _settings.PreserveRemoteControl)); }
        catch (Exception e) { Error("Could not open terminal", e); }
    }

    /// A plain Claude session on the default login in the general folder (the Agents button's menu, and Work in the tray menu).
    void OpenNewAgent()
    {
        try
        {
            Directory.CreateDirectory(Launch.GeneralFolder);
            _engine.LaunchConsole("Claude · new agent", Launch.GeneralFolder, Launch.Run(null, _settings.PreserveRemoteControl));
        }
        catch (Exception e) { Error("Could not open terminal", e); }
    }

    /// With "Use project accounts automatically", the account assigned to `folder` (else null).
    Account? MappedAccount(string folder) =>
        _settings.AutoProjectAccount ? ProjectMap.AccountFor(folder, _snap?.Accounts ?? [], ProjectMap.Load()) : null;

    /// `cswap run` with no account in the folder: the engine resolves the assignment itself.
    void OpenProjectTerminal(ProjectMapping m)
    {
        if (!Directory.Exists(m.Folder)) { Notify("Folder not found", m.Folder, ToolTipIcon.Warning); return; }
        try { _engine.LaunchConsole($"Claude · {m.Leaf} · {m.Email}", m.Folder, Launch.Run(null, _settings.PreserveRemoteControl)); }
        catch (Exception e) { Error("Could not open terminal", e); }
    }

    /// Starts a new session in `target` with notes from the interrupted `source` session.
    /// Not a resume: the notes are built locally and Patch Goblins sends nothing; the
    /// target agent reads them like any prompt.
    async Task ContinueAsync(string source, string target)
    {
        if (PickFolder($"Folder of the interrupted {ProviderCli.Title(source)} work") is not { } folder) return;
        try
        {
            var exe = ProviderCli.FindAny(target) ?? throw new EngineException($"{ProviderCli.Title(target)} CLI not found.");
            var h = await _engine.HandoffAsync(source, folder);
            var prompt = $"Read the hand-off notes in {h.Path} and continue the interrupted task.";
            var mapped = target == "claude" ? MappedAccount(folder) : null;
            var who = mapped ?? (target == "claude" ? _snap : _others.GetValueOrDefault(target).Snap)?.Accounts.FirstOrDefault(a => a.Active);
            var title = $"{ProviderCli.Title(target)} · continue" + (who == null ? "" : " · " + who.ShortName);
            if (target == "grok")
            {
                // ponytail: no known initial-prompt argument for grok; pass it as an arg once one exists.
                Clipboard.SetText(prompt);
                Engine.LaunchConsoleWith(exe, title, folder);
                Notify("Hand-off ready", "Paste the copied line into Grok to start with the notes.");
                return;
            }
            // Claude goes through `cswap run` so Remote Control can be restored. In a folder assigned to an
            // account the engine picks it itself; else the active account, which runs plain claude. No known account: direct.
            if (target == "claude" && Launch.Continue(mapped, who, _settings.PreserveRemoteControl, prompt) is { } run)
                _engine.LaunchConsole(title, folder, run);
            else Engine.LaunchConsoleWith(exe, title, folder, prompt);
            Notify("Hand-off ready", $"New {ProviderCli.Title(target)} session started with notes from the interrupted one.");
        }
        catch (Exception e) { Error("Could not continue", e); }
    }

    async Task AccountActionAsync(Account a, string action)
    {
        try
        {
            switch (action)
            {
                case not null when action.StartsWith("continue:"):
                    await ContinueAsync(a.Provider, action["continue:".Length..]);
                    return;
                case "terminal-folder":
                    if (PickFolder($"Open a terminal as #{a.Number} in…", _settings.TerminalFolder ?? StoreSecurity.UserProfile) is { } f)
                        OpenTerminal(a.Provider == "claude" ? MappedAccount(f) ?? a : a, f);
                    return;
                case "map":
                    if (PickFolder($"Project folder for #{a.Number} (`cswap run` there uses this account)") is not { } m) return;
                    Notify("Project folder assigned", await _engine.CommandAsync("map", a.Number.ToString(), m));
                    break;
                case not null when action.StartsWith("project-open:"):
                    var dir = action["project-open:".Length..];
                    if (Directory.Exists(dir)) OpenTerminal(a, dir);
                    else Notify("Folder not found", dir, ToolTipIcon.Warning);
                    return;
                case not null when action.StartsWith("project-remove:"):
                    Notify("Project folder removed", await _engine.CommandAsync("unmap", action["project-remove:".Length..]));
                    break;
                case "enable" or "disable":
                    await _engine.CommandAsync(action, a.Number.ToString());
                    break;
                case "alias":
                    var alias = Prompt.Show("Set alias", $"Short name for {a.Email} (blank removes it):", a.Alias ?? "");
                    if (alias == null) return;
                    await _engine.CommandAsync(alias.Length == 0
                        ? ["alias", a.Number.ToString(), "--unset"]
                        : ["alias", a.Number.ToString(), alias]);
                    break;
                case "reenroll":
                    await EnrollAsync(a, a.Provider);
                    return;
                case "remove" when a.Provider != "claude":
                    if (MessageBox.Show($"Remove {ProviderCli.Title(a.Provider)} #{a.Number} {a.Email} from Patch Goblins?\n\n" +
                                        "Its stored login is deleted. The current login is not logged out.",
                            "Patch Goblins", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    await _engine.CommandAsync(a.Provider, "remove", a.Number.ToString(), "--json");
                    break;
                case "remove":
                    if (MessageBox.Show($"Remove #{a.Number} {a.Email} from Patch Goblins?\n\nIts stored backup is deleted. " +
                                        "Your current Claude Code login is not logged out.",
                            "Patch Goblins", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    await _engine.CommandWithInputAsync("y\n", "remove", a.Number.ToString());
                    break;
            }
        }
        catch (Exception e) { Error("Action failed", e); }
        await TickAsync(runAuto: false);
    }

    async Task EnrollAsync(Account? existing, string provider = "claude")
    {
        if (provider != "claude")
        {
            var title = ProviderCli.Title(provider);
            if (MessageBox.Show(
                    (existing == null ? $"Add a {title} account.\n\n" : $"Log in again as {existing.Email}.\n\n") +
                    $"A console opens with the official login (`{provider} login`). Sign in in the browser " +
                    "(a private window avoids approving the account you're already signed in with).\n\n" +
                    "This uses a separate, temporary profile: your current login and running sessions are not affected.",
                    "Patch Goblins", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            try
            {
                // First account: keep the login already on this machine too (errors when there is none).
                if (_others.GetValueOrDefault(provider).Snap is not { Accounts.Count: > 0 })
                    try { await _engine.CommandAsync(provider, "add-current", "--json"); } catch (EngineException) { }
                await ProviderCli.EnrollAsync(_engine, provider);
                Notify("Account enrolled", $"{title} account added.");
            }
            catch (Exception e) { Error("Enrollment failed", e); }
            await TickAsync(runAuto: false);
            return;
        }
        if (MessageBox.Show(
                (existing == null ? "Add a Claude account.\n\n" : $"Log in again as {existing.Email}.\n\n") +
                "A console opens with the official Claude Code login (`claude auth login`).\n\n" +
                "1. Open the login link in a private browser window, so you don't approve the account you're already signed in with.\n" +
                "2. Sign in and authorize.\n" +
                "3. If a code is shown, paste it into the console and press Enter. Nothing appears while you paste; that is normal.\n\n" +
                "The console closes when done. Your password is only ever entered in the browser, never in the console.\n\n" +
                "This uses a separate, temporary profile: your current login and running sessions are not affected.",
                "Patch Goblins", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        try
        {
            // First run: keep the login already on this machine as an account too.
            if (_snap is { Accounts.Count: 0 })
            {
                var status = await _engine.RunAsync(["status", "--json"]);
                using var doc = System.Text.Json.JsonDocument.Parse(status.Stdout);
                if (doc.RootElement.TryGetProperty("active", out var act) && act.ValueKind == System.Text.Json.JsonValueKind.Object
                    && act.TryGetProperty("managed", out var managed) && managed.ValueKind == System.Text.Json.JsonValueKind.False)
                    await _engine.CommandAsync("add");
            }
            var msg = await ClaudeCli.EnrollAsync(_engine);
            Notify("Account enrolled", msg);
        }
        catch (Exception e) { Error("Enrollment failed", e); }
        await TickAsync(runAuto: false);
    }

    void SetAutoSwitch(bool on)
    {
        if (on && !_settings.AutoSwitchPolicyAccepted)
        {
            var ok = MessageBox.Show(
                "Auto-switch moves Claude Code to another of your accounts when the active one nears its limit.\n\n" +
                "• You are responsible for following Anthropic's Consumer Terms and Usage Policy (anthropic.com/legal), " +
                "including any rules on multiple accounts and usage limits. Use only accounts that are yours.\n" +
                "• It never bypasses a limit: an exhausted account stays exhausted until it resets.\n" +
                "• Excluded accounts, accounts whose login has expired, and API-key accounts are never chosen.\n" +
                "• Sessions on the default login move to the new account from their next message; a response in " +
                "progress is not interrupted. Terminals opened for another account keep it; one opened for the " +
                "active account follows switches.\n" +
                "• Polling is conservative: the engine spaces out usage checks per account.\n\n" +
                "Patch Goblins is an independent project, not affiliated with or endorsed by Anthropic.\n\nEnable auto-switch?",
                "Patch Goblins — auto-switch", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes;
            if (!ok) return;
            _settings.AutoSwitchPolicyAccepted = true;
        }
        _settings.AutoSwitch = on;
        _settings.Save();
        _timer.Interval = TimerMs;
        _ = TickAsync(runAuto: true);
    }

    async Task ShowSettingsAsync()
    {
        var f = new SettingsPanel();
        // The engine's threshold as the tray already read it: asking the engine again starts its process, a second's wait on every open.
        if (_engineThreshold == null) await ReadEngineThresholdAsync();
        if (_engineThreshold == null) { f.Dispose(); Error("Could not read engine settings", new EngineException("cswap config did not answer")); return; }
        f.Threshold.Value = Math.Clamp((decimal)_engineThreshold.Value, f.Threshold.Minimum, f.Threshold.Maximum);
        f.SwitchBuffer.Value = Math.Clamp(_engineSwitchBuffer, (int)f.SwitchBuffer.Minimum, (int)f.SwitchBuffer.Maximum);
        f.AutoLine.Checked = _settings.AutoLine;
        f.AutoSwitch.Checked = _settings.AutoSwitch;
        f.Startup.Checked = Startup.IsEnabled();
        f.AutoProject.Checked = _settings.AutoProjectAccount;
        f.TerminalFolder.Text = _settings.TerminalFolder ?? "";
        f.QuotaNotify.Checked = _settings.QuotaNotifications;
        f.AutoPrime.Checked = _settings.AutoPrime;
        f.AgentIssues.Value = Math.Clamp(_settings.AgentIssues, (int)f.AgentIssues.Minimum, (int)f.AgentIssues.Maximum);
        f.AgentOpenPrs.Value = Math.Clamp(_settings.AgentOpenPrs, (int)f.AgentOpenPrs.Minimum, (int)f.AgentOpenPrs.Maximum);
        f.DraftPrs.Checked = _settings.AgentDraftPrs;
        f.Ultracode.Checked = _settings.AgentUltracode;
        f.AskApproval.Checked = _settings.AskApproval;
        f.MoveWorkflows.Checked = _settings.MoveWorkflows;
        f.CancelClicked += _dash.CloseSettings;
        f.SaveClicked += () => _ = SaveSettingsAsync(f);
        if (!_dash.Visible) ShowDashboard();
        _dash.ShowSettings(f);
    }

    async Task SaveSettingsAsync(SettingsPanel f)
    {
        var threshold = (double)f.Threshold.Value;
        try
        {
            Startup.Set(f.Startup.Checked);
            _settings.AutoProjectAccount = f.AutoProject.Checked;
            _settings.TerminalFolder = f.TerminalFolder.Text.Trim() is { Length: > 0 } tf ? tf : null;
            if (f.QuotaNotify.Checked != _settings.QuotaNotifications) _quota = new(); // switching on starts from a fresh, silent baseline
            _settings.QuotaNotifications = f.QuotaNotify.Checked;
            _settings.AutoPrime = f.AutoPrime.Checked;
            _settings.AgentIssues = (int)f.AgentIssues.Value;
            _settings.AgentOpenPrs = (int)f.AgentOpenPrs.Value;
            _settings.AgentDraftPrs = f.DraftPrs.Checked;
            _settings.AgentUltracode = f.Ultracode.Checked;
            _settings.AskApproval = f.AskApproval.Checked;
            _settings.AutoLine = f.AutoLine.Checked;
            // Once, like auto-switch's policy: the owner agrees that agents caught mid-run start over.
            if (f.MoveWorkflows.Checked && !_settings.MoveWorkflowsAccepted
                && MessageBox.Show(DockSettings.MoveWorkflowsConsent + "\n\nMove running workflows?", "Patch Goblins — move running workflows",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                _settings.MoveWorkflowsAccepted = true;
            _settings.MoveWorkflows = f.MoveWorkflows.Checked && _settings.MoveWorkflowsAccepted;
            ApplyAgentSettings();
            _settings.Save();
            _timer.Interval = TimerMs;
            var autoSwitch = f.AutoSwitch.Checked;
            _dash.CloseSettings(); // every control is read: the flyout goes back now, the engine is told after
            if (autoSwitch != _settings.AutoSwitch) SetAutoSwitch(autoSwitch); // asks for the owner's yes first when switching on
            if (threshold != _engineThreshold)
            {
                await _engine.CommandAsync("config", "set", "autoswitch.threshold", threshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await ReadEngineThresholdAsync(); // agents follow the engine's threshold
            }
            var buffer = (int)f.SwitchBuffer.Value;
            if (buffer != _engineSwitchBuffer)
            {
                await _engine.CommandAsync("config", "set", "autoswitch.switchBufferSeconds", buffer.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await ReadEngineThresholdAsync();
            }
        }
        catch (Exception e) { _dash.CloseSettings(); Error("Could not save settings", e); }
    }

    static string? PickFolder(string description, string? start = null)
    {
        using var d = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        if (Directory.Exists(start)) d.InitialDirectory = start;
        return d.ShowDialog() == DialogResult.OK ? d.SelectedPath : null;
    }

    static void Error(string title, Exception e) =>
        MessageBox.Show(Engine.Redact(e.Message), "Patch Goblins — " + title, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _watch.Stop();
        _limitWatch.Dispose();
        _link?.Dispose();
        _agents?.Dispose(); // workers keep running; the next start re-attaches them, or stops them without their terminal
        _tray.Visible = false;
        _tray.Dispose();
        _dash.Dispose();
        base.ExitThreadCore();
    }
}

static class Prompt
{
    public static string? Show(string title, string text, string value)
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(340, 110), Font = new Font("Segoe UI", 9f) };
        var label = new Label { Text = text, Location = new Point(12, 10), Size = new Size(316, 32) };
        var box = new TextBox { Text = value, Location = new Point(12, 44), Width = 316 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(172, 76) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(253, 76) };
        f.Controls.AddRange([label, box, ok, cancel]);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
    }
}
