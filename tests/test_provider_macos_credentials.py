"""Synthetic provider credentials; every Keychain operation is in memory."""
import base64
import json
from pathlib import Path
import shlex
from types import SimpleNamespace

import pytest

from claude_swap import dpapi, macos_keychain as kc
from claude_swap.providers import _credentials as credentials, codex, grok


SECRET = "SYNTHETIC-PROVIDER-SECRET"
ORIGINAL_SET_PASSWORD = kc.set_password


@pytest.fixture
def darwin(monkeypatch, block_real_keychain):
    monkeypatch.setattr(credentials, "sys", SimpleNamespace(platform="darwin"))
    return block_real_keychain


def atomic(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(text, encoding="utf-8")
    temporary.replace(path)


def test_large_provider_payload_stays_in_keychain_not_reference_files(tmp_path, darwin):
    path = tmp_path / "creds" / "1.enc"
    payload = json.dumps({"access_token": SECRET * 1500, "refresh_token": SECRET})
    credentials.write(path, payload, atomic)
    raw = path.read_text()
    assert raw.startswith(credentials.PREFIX)
    assert SECRET not in raw
    assert "access_token" not in raw
    assert all(len(value) <= credentials.CHUNK_SIZE for value in darwin.data.values())
    assert credentials.read(path, atomic) == payload
    assert credentials._service(path) == credentials._service(path.parent / ".." / "creds" / "1.enc")


def test_large_provider_values_always_use_security_stdin(tmp_path, darwin, monkeypatch):
    store = {}
    calls = []
    # Exercise the real wrapper writer with an in-memory security subprocess.
    def run(argv, **kwargs):
        assert argv == [kc._SECURITY, "-i"]
        command = kwargs["input"]
        assert len(command.encode()) <= kc.SECURITY_STDIN_LINE_LIMIT
        tokens = shlex.split(command)
        service = tokens[tokens.index("-s") + 1]
        account = tokens[tokens.index("-a") + 1]
        value = bytes.fromhex(tokens[tokens.index("-X") + 1]).decode()
        calls.append(argv)
        store[service, account] = value
        return SimpleNamespace(returncode=0, stdout="", stderr="")
    monkeypatch.setattr(kc.subprocess, "run", run)
    monkeypatch.setattr(kc, "set_password", ORIGINAL_SET_PASSWORD)
    monkeypatch.setattr(kc, "get_password", lambda service, account: store.get((service, account)))
    credentials.write(tmp_path / "1.enc", SECRET * 1000, atomic)
    assert len(calls) > 1
    assert credentials.read(tmp_path / "1.enc", atomic) == SECRET * 1000


def test_long_keychain_username_fails_before_wrapper_argv_fallback(tmp_path, darwin, monkeypatch):
    monkeypatch.setattr(kc, "keychain_account_name", lambda: "user" * 2000)
    with pytest.raises(credentials.ProviderCredentialsError, match="safe stdin limit"):
        credentials.write(tmp_path / "1.enc", SECRET, atomic)
    assert not (tmp_path / "1.enc").exists()
    assert not darwin.data


def test_keychain_username_cannot_inject_an_interactive_security_command(tmp_path, darwin, monkeypatch):
    monkeypatch.setattr(kc, "keychain_account_name", lambda: "synthetic\nextra-command")
    with pytest.raises(credentials.ProviderCredentialsError, match="metadata is invalid"):
        credentials.write(tmp_path / "1.enc", SECRET, atomic)
    assert not (tmp_path / "1.enc").exists()
    assert not darwin.data


def test_migration_removes_legacy_file_material_only_after_verified_write(tmp_path, darwin):
    path = tmp_path / "1.enc"
    legacy = base64.b64encode(SECRET.encode()).decode()
    path.write_text(legacy)
    assert credentials.read(path, atomic) == SECRET
    assert path.read_text().startswith(credentials.PREFIX)
    assert legacy not in path.read_text()


def test_keychain_write_failure_retains_legacy_and_never_returns_credentials(tmp_path, darwin, monkeypatch):
    path = tmp_path / "1.enc"
    legacy = base64.b64encode(SECRET.encode()).decode()
    path.write_text(legacy)
    def denied(*args):
        raise kc.KeychainError("denied " + SECRET)
    monkeypatch.setattr(kc, "set_password", denied)
    with pytest.raises(credentials.ProviderCredentialsError) as error:
        credentials.read(path, atomic)
    assert SECRET not in str(error.value)
    assert path.read_text() == legacy


def test_read_missing_or_locked_keychain_never_falls_back_to_disk(tmp_path, darwin, monkeypatch):
    path = tmp_path / "1.enc"
    credentials.write(path, SECRET, atomic)
    raw = path.read_text()
    darwin.data.clear()
    with pytest.raises(credentials.ProviderCredentialsError):
        credentials.read(path, atomic)
    assert path.read_text() == raw
    def denied(*args):
        raise kc.KeychainError("locked " + SECRET)
    monkeypatch.setattr(kc, "get_password", denied)
    with pytest.raises(credentials.ProviderCredentialsError) as error:
        credentials.read(path, atomic)
    assert SECRET not in str(error.value)


def test_verification_or_reference_write_failure_preserves_previous_generation(tmp_path, darwin, monkeypatch):
    path = tmp_path / "1.enc"
    credentials.write(path, SECRET, atomic)
    previous = path.read_text()
    def broken_writer(*args):
        raise OSError("synthetic disk failure")
    with pytest.raises(credentials.ProviderCredentialsError, match="previous credentials were retained"):
        credentials.write(path, SECRET + "new", broken_writer)
    assert path.read_text() == previous
    assert credentials.read(path, atomic) == SECRET
    assert len(darwin.data) == 1


def test_keychain_roundtrip_mismatch_preserves_previous_reference(tmp_path, darwin, monkeypatch):
    path = tmp_path / "1.enc"
    credentials.write(path, SECRET, atomic)
    previous = path.read_text()
    old_keys = set(darwin.data)
    original_get = kc.get_password
    monkeypatch.setattr(kc, "get_password", lambda service, account:
        original_get(service, account) if (service, account) in old_keys else "synthetic mismatch")
    with pytest.raises(credentials.ProviderCredentialsError, match="verification failed"):
        credentials.write(path, SECRET + "new", atomic)
    assert path.read_text() == previous
    monkeypatch.setattr(kc, "get_password", original_get)
    assert credentials.read(path, atomic) == SECRET
    assert set(darwin.data) == old_keys


def test_replacement_and_account_removal_delete_old_keychain_generations(tmp_path, darwin):
    path = tmp_path / "1.enc"
    credentials.write(path, SECRET, atomic)
    old_keys = set(darwin.data)
    credentials.write(path, SECRET + "new", atomic)
    assert not old_keys.intersection(darwin.data)
    credentials.delete(path)
    assert not path.exists()
    assert not darwin.data


def test_delete_failure_keeps_reference_for_retry(tmp_path, darwin, monkeypatch):
    path = tmp_path / "1.enc"
    credentials.write(path, SECRET, atomic)
    def denied(*args):
        raise kc.KeychainError("denied " + SECRET)
    monkeypatch.setattr(kc, "delete_password", denied)
    with pytest.raises(credentials.ProviderCredentialsError) as error:
        credentials.delete(path)
    assert path.exists()
    assert SECRET not in str(error.value)


def test_reference_cannot_redirect_reads_to_another_credentials_path(tmp_path, darwin):
    path = tmp_path / "1.enc"
    credentials.write(path, SECRET, atomic)
    other = tmp_path / "2.enc"
    other.write_text(path.read_text())
    with pytest.raises(credentials.ProviderCredentialsError):
        credentials.read(other, atomic)


@pytest.mark.parametrize("platform", ["win32", "linux"])
def test_existing_non_macos_sealing_uses_same_dpapi_contract(tmp_path, monkeypatch, platform):
    monkeypatch.setattr(credentials, "sys", SimpleNamespace(platform=platform))
    monkeypatch.setattr(dpapi, "seal", lambda text: "original-format:" + text)
    monkeypatch.setattr(dpapi, "unseal", lambda encoded: encoded.removeprefix("original-format:"))
    path = tmp_path / "1.enc"
    credentials.write(path, SECRET, atomic)
    assert path.read_text() == "original-format:" + SECRET
    assert credentials.read(path, atomic) == SECRET


@pytest.mark.parametrize("provider", [codex, grok])
def test_provider_remove_keeps_roster_when_keychain_cleanup_fails(tmp_path, darwin, monkeypatch, provider):
    monkeypatch.setattr(provider, "sys", SimpleNamespace(platform="darwin"))
    monkeypatch.setattr(provider, "store_dir", lambda: tmp_path / "store")
    provider._save_roster([{"number": 1, "email": "synthetic@example.invalid"}])
    if provider is codex:
        provider._seal_auth(1, {"access_token": SECRET})
    else:
        provider._seal(1, "synthetic", {"key": SECRET})
    def denied(*args):
        raise kc.KeychainError("denied")
    monkeypatch.setattr(kc, "delete_password", denied)
    with pytest.raises(credentials.ProviderCredentialsError):
        provider.cmd_remove(1)
    assert provider._roster()[0]["number"] == 1
    assert provider._cred_path(1).exists()
