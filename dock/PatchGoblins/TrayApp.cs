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
    readonly HashSet<string> _told = [], _reported = []; // reported session limits and limits handed to the engine
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
        AutoPrimeTick();
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

    /// Reports stopped sessions without interrupting them. A terminal may contain unsaved shell
    /// state or other tabs, and a replacement window does not prove the session actually resumed.
    async Task ResumeStoppedAsync(Snapshot snap)
    {
        await _movePass.WaitAsync();
        try
        {
            (_held, var shown) = (null, _notices);
            await MovePassAsync(_snap ?? snap);
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
        if (_engineThreshold == null) await ReadEngineThresholdAsync();
        if (await FreePinnedAsync(snap)) { _retry = true; return; }
        _retry = false;
        foreach (var s in stopped.Where(s => s.AtLimit))
        {
            var key = $"{s.SessionId}@{s.Limit?.ResetsAt ?? s.Limit?.At}";
            if (!_told.Add(key)) continue;
            var folder = Path.GetFileName(s.Cwd.TrimEnd('\\', '/'));
            var guidance = s.Status == "agents"
                ? "Reopen it from the Agents menu when you are ready."
                : "When it is safe, reopen or resume the session on an account with room.";
            Notify("Terminal stopped at a limit", $"{folder} has reached a usage limit. {guidance}", ToolTipIcon.Warning);
        }
    }

    /// An account held by any interactive session stays pinned. Only known headless agent
    /// runs may yield it; an unrecognized holder is always left alone.
    async Task<bool> FreePinnedAsync(Snapshot snap)
    {
        var (threshold, hysteresis) = (Threshold, _engineHysteresis ?? 10);
        var due = ResumeStopped.FreeTargets(snap.Accounts, LiveOrLaunching(), _limits.RefusedUntil, DateTimeOffset.UtcNow, threshold, hysteresis);
        if (due.Count == 0) _heldNotice.Clear();
        foreach (var n in due)
            if (await FreePinnedAsync(snap, n)) return true;
        return false;
    }

    async Task<bool> FreePinnedAsync(Snapshot snap, int n)
    {
        void Held(string text) { if (!_heldNotice.Contains(n)) _held = (n, text); }
        if (ResumeStopped.ProfileDir(n) is not { } dir) return false;
        if (_freed.TryGetValue(n, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(30))
        {
            Held($"#{n} was freed recently and is in use again. It will not be freed repeatedly.");
            return false;
        }
        var account = snap.Accounts.First(a => a.Number == n);
        bool agents;
        try
        {
            var windows = await Task.Run(() => ResumeStopped.Scan([(dir, n)], DateTime.UtcNow, includeBusy: true, agentLimits: _agentLimits));
            var (records, runs) = await Task.Run(() => ResumeStopped.LiveCounts(dir));
            agents = ResumeStopped.AgentsOnly(windows, records, runs, _agents?.RunsOn(account) ?? 0);
            if (windows.Count > 0 || !agents)
            {
                var holders = ResumeStopped.Holders(windows, records, runs);
                if (holders.Count > 0 || windows.Count > 0)
                    Held($"#{n} is in use by a terminal or another process. Close or move that session yourself when it is safe to free the account.");
                return false;
            }
        }
        catch (Exception e) { Program.Log(e); return false; }

        if (!ResumeStopped.DefaultProfileClear(ResumeStopped.DefaultDir))
        {
            Held("The default login is in use or its sessions cannot be inspected. Agents keep working until it is safe to switch.");
            return false;
        }
        _freed[n] = DateTime.UtcNow;
        try
        {
            await _agents!.YieldAsync(account, TimeSpan.FromMinutes(30));
            var free = false;
            for (var i = 0; i < 60 && !(free = await Task.Run(() => ResumeStopped.LiveProfiles([(dir, n)]).Count == 0)); i++) await Task.Delay(500);
            if (!free || !_settings.AutoSwitch || _limits.Switching || _engineSwitches > 0) return true;
            var sw = await _engine.SwitchNowAsync(n);
            if (sw is { Switched: true })
            {
                _lastBlockedNotice = null;
                Notify($"Agents freed #{n} for the default login", $"{Format.SwitchNotice(sw.From, sw.To)}. Agents continue their work on another account.");
                Switched(n);
            }
        }
        catch (Exception e) { Program.Log(e); }
        return true;
    }

    /// The tray just moved the default login to `n`: the listed state says so at once, and no list started before it replaces it.
    void Switched(int n)
    {
        _switches++;
        if (_snap is { } s) _snap = s with { ActiveAccountNumber = n, Accounts = [.. s.Accounts.Select(a => a with { Active = a.Number == n })] };
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

    DateTimeOffset _lastAutoPrime;

    /// With Auto-Prime on, every tick starts fresh 5-hour windows early (overnight too), so they also reset early.
    /// The ledger stops a window being primed twice; this only paces the attempts.
    void AutoPrimeTick()
    {
        if (!_settings.AutoPrime || _snap == null || _priming || DateTimeOffset.Now - _lastAutoPrime < TimeSpan.FromMinutes(30)) return;
        // The default login near its line: a prime would make the account it switches to (a fresh one) a live profile meanwhile.
        if (_snap.Accounts.FirstOrDefault(a => a.Active) is { } login && ResumeStopped.Peak(login) >= Threshold - (_engineHysteresis ?? 10)) return;
        if (Prime.Ready(_snap.Accounts, DateTimeOffset.Now) is not { Count: > 0 } ready) return;
        _lastAutoPrime = DateTimeOffset.Now;
        _ = PrimeAsync(ready);
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
                "• Automatic switches wait while a default-login session is open. Your terminals and workflows " +
                "are never closed or reopened automatically. Manual account switches can change the login used by an open session.\n" +
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
