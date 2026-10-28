"""follow_logins under a concurrent switch, and after a post-refresh write that timed out."""

from __future__ import annotations

import json
import threading
import time
from unittest.mock import patch

from claude_swap import oauth
from claude_swap.exceptions import ClaudeCodeLockTimeout
from claude_swap.session import is_session_stale
from tests.test_autoswitch import EngineHarness

HOUR = 3_600_000


def _rig(temp_home, exp):
    """Account 1 live with a live window; account 2 seeded with a later-expiring login. Synthetic tokens only."""
    h = EngineHarness(temp_home)
    h.seed(1, "a@example.com", expires_at=exp)
    h.seed(2, "b@example.com", expires_at=int(time.time() * 1000) + 5 * HOUR)
    h.make_live("a@example.com", 1)
    creds = temp_home / ".claude" / ".credentials.json"
    creds.write_text(json.dumps({"claudeAiOauth": {"accessToken": "at-a", "refreshToken": "rt-a", "expiresAt": exp}}))
    sdir = h.switcher._session_dir("1", "a@example.com")
    sdir.mkdir(parents=True)
    prof = sdir / ".credentials.json"
    prof.write_text(json.dumps({"claudeAiOauth": {"accessToken": "at-p", "refreshToken": "rt-p", "expiresAt": exp + 60_000}}))
    return h, creds, prof


def _rt(path):
    return json.loads(path.read_text())["claudeAiOauth"]["refreshToken"]


def test_a_switch_between_read_and_write_never_copies_the_other_accounts_login(temp_home):
    now = int(time.time() * 1000)
    h, creds, prof = _rig(temp_home, now + 3 * HOUR)
    real_sync = h.switcher._follow_sync
    switcher_thread = threading.Thread(target=lambda: h.switcher.switch_to("2", json_output=True))

    def switch_lands_mid_follow(*a, **kw):
        # The tray's ungated `cswap switch 2` runs right after follow_logins resolved account 1.
        if not switcher_thread.is_alive() and switcher_thread.ident is None:
            switcher_thread.start()
            switcher_thread.join(timeout=1.0)
        return real_sync(*a, **kw)

    with patch.object(
        h.switcher, "_live_session_pids", side_effect=lambda n, e: [4242] if n == "1" else []
    ), patch.object(h.switcher, "_follow_sync", side_effect=switch_lands_mid_follow):
        h.switcher.follow_logins()
        switcher_thread.join(timeout=30)
    assert not switcher_thread.is_alive() and h.active_number() == 2  # the switch did land
    assert _rt(prof) in ("rt-a", "rt-p")  # never account 2's login in account 1's window
    assert "rt-2" not in h.switcher._store._read_account_credentials("1", "a@example.com")


def test_a_timed_out_post_refresh_write_is_healed_by_the_next_tick(temp_home):
    now = int(time.time() * 1000)
    h, creds, prof = _rig(temp_home, now + 10 * 60_000)  # inside the refresh window
    fresh = json.dumps({"claudeAiOauth": {"accessToken": "at-f", "refreshToken": "rt-f", "expiresAt": now + 8 * HOUR}})
    real_sync = h.switcher._follow_sync
    calls = []

    def second_sync_times_out(*a, **kw):
        calls.append(1)
        if len(calls) == 2:
            raise ClaudeCodeLockTimeout("held")
        return real_sync(*a, **kw)

    with patch.object(h.switcher, "_live_session_pids", return_value=[4242]), patch(
        "claude_swap.oauth.try_refresh_oauth_credentials", return_value=oauth.RefreshOutcome(fresh, None)
    ) as refresh:
        with patch.object(h.switcher, "_follow_sync", side_effect=second_sync_times_out):
            assert h.switcher.follow_logins() == {"status": "busy"}
        assert _rt(prof) == "rt-p"  # the used generation: only the backup holds rt-f
        assert h.switcher.follow_logins()["status"] == "synced"
        assert refresh.call_count == 1  # healed from the backup, no second grant consumed
    assert _rt(creds) == "rt-f" and _rt(prof) == "rt-f"
    assert not is_session_stale(prof.parent)
    # A later tick never writes the older copies back over the backup's generation.
    with patch.object(h.switcher, "_live_session_pids", return_value=[4242]):
        assert h.switcher.follow_logins() == {"status": "synced"}
    assert "rt-f" in h.switcher._store._read_account_credentials("1", "a@example.com")


def test_a_switch_during_the_refresh_grant_never_writes_into_the_new_accounts_login(temp_home):
    now = int(time.time() * 1000)
    h, creds, prof = _rig(temp_home, now + 10 * 60_000)  # inside the refresh window
    fresh = json.dumps({"claudeAiOauth": {"accessToken": "at-f", "refreshToken": "rt-f", "expiresAt": now + 8 * HOUR}})

    def switch_lands_during_grant(num, email, newest):
        # cswap's lock is released for the network refresh: the tray's `cswap switch 2` lands here.
        h.switcher.switch_to("2", json_output=True)
        return oauth.RefreshOutcome(fresh, None)

    with patch.object(
        h.switcher, "_live_session_pids", side_effect=lambda n, e: [4242] if n == "1" else []
    ), patch.object(h.switcher, "consume_backup_grant", side_effect=switch_lands_during_grant):
        assert h.switcher.follow_logins() == {"status": "alone"}
    assert h.active_number() == 2  # the switch did land
    assert _rt(creds) != "rt-f"  # account 1's new generation never lands in account 2's default login
