"""Enroll an account from an isolated Claude Code login (Patch Goblins).

``cswap add`` captures whatever the *default* profile is logged in as, so adding
a second account normally means logging the default profile out of the first.
Enrollment instead runs the official ``claude auth login`` against a throwaway
``CLAUDE_CONFIG_DIR`` and hands that login to the regular import path:

    CLAUDE_CONFIG_DIR=<tmp> claude auth login
    cswap enroll <tmp>

The default login, running sessions and ``activeAccountNumber`` are never
touched. An existing slot for the same account is overwritten in place (a
re-login is how a dead refresh token is repaired). The caller owns ``<tmp>``
and must delete it afterwards: it holds a live credential.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import TYPE_CHECKING

from claude_swap.exceptions import TransferError
from claude_swap.models import get_timestamp
from claude_swap.paths import _pick_global_config
from claude_swap.session import read_config_dir_credentials
from claude_swap.transfer import (
    FORMAT_VERSION,
    _parse_payload,
    _slim_config,
    _slim_credentials,
    import_accounts,
)

if TYPE_CHECKING:
    from claude_swap.switcher import ClaudeAccountSwitcher


def build_envelope(config_dir: Path, slot: int) -> dict:
    """One-account export envelope from a Claude Code config directory."""
    credentials = read_config_dir_credentials(str(config_dir), strict_keychain=True)
    if not credentials:
        raise TransferError(
            f"no login found in {config_dir} (did `claude auth login` finish?)"
        )
    config_path = _pick_global_config(config_dir / ".config.json", config_dir / ".claude.json")
    if not config_path.is_file():
        raise TransferError(f"no Claude config found in {config_dir}")

    creds = _slim_credentials(
        _parse_payload(credentials, "enrolled credentials")
    )
    if not isinstance(creds.get("claudeAiOauth"), dict):
        raise TransferError("enrolled credentials carry no Claude OAuth login")
    config = _slim_config(
        _parse_payload(config_path.read_text(encoding="utf-8"), "enrolled config"),
        "enrolled config",
    )
    oauth = config["oauthAccount"]
    email = oauth.get("emailAddress")
    if not isinstance(email, str) or not email:
        raise TransferError("enrolled login has no email address")
    return {
        "version": FORMAT_VERSION,
        "exportedAt": get_timestamp(),
        "encrypted": False,
        "activeAccountNumber": None,
        "accounts": [
            {
                "number": slot,
                "email": email,
                "uuid": oauth.get("accountUuid", "") or "",
                "organizationUuid": oauth.get("organizationUuid", "") or "",
                "organizationName": oauth.get("organizationName", "") or "",
                "added": get_timestamp(),
                "credentials": creds,
                "config": config,
            }
        ],
    }


def enroll(switcher: ClaudeAccountSwitcher, config_dir: str) -> str:
    """Import the login held in ``config_dir``; returns the enrolled email."""
    root = Path(config_dir).expanduser().resolve()
    data = switcher._get_sequence_data_migrated() or {}
    used = [int(n) for n in data.get("accounts", {})]
    envelope = build_envelope(root, slot=max(used, default=0) + 1)
    import_accounts(switcher, "<enroll>", force=True, text=json.dumps(envelope))
    return envelope["accounts"][0]["email"]
