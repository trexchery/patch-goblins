"""Provider dormant credentials: DPAPI on Windows, strict Keychain on macOS.

macOS reference files contain no credential material. Small Keychain chunks
always fit the existing security wrapper's stdin path, including large JWTs.
"""
from __future__ import annotations

import base64
import hashlib
import json
from pathlib import Path
import re
import sys
import uuid

from claude_swap import dpapi, macos_keychain
from claude_swap.fsutil import read_text_with_retry


PREFIX = "keychain-provider-v1:"
CHUNK_SIZE = 900


class ProviderCredentialsError(ValueError):
    """A secret-free credential-storage error, safe for CLI diagnostics."""


def _service(path: Path) -> str:
    digest = hashlib.sha256(str(path.resolve()).encode("utf-8")).hexdigest()
    return "PatchGoblins.Provider." + digest


def _reference(path: Path, encoded: str) -> dict | None:
    if not encoded.startswith(PREFIX):
        return None
    try:
        ref = json.loads(encoded[len(PREFIX):])
        if (not isinstance(ref, dict) or ref.get("service") != _service(path)
            or not isinstance(ref.get("generation"), str)
            or not re.fullmatch(r"[0-9a-f]{32}", ref["generation"])
            or type(ref.get("chunks")) is not int or not 1 <= ref["chunks"] <= 4096):
            raise ValueError
        return ref
    except (ValueError, TypeError, KeyError):
        raise ProviderCredentialsError("Provider Keychain reference is invalid; credentials were not read") from None


def _keys(ref: dict):
    return [f'{ref["service"]}.{ref["generation"]}.{i}' for i in range(ref["chunks"])]


def _read(ref: dict) -> str:
    account = macos_keychain.keychain_account_name()
    try:
        values = [macos_keychain.get_password(key, account) for key in _keys(ref)]
        if any(value is None for value in values):
            raise ProviderCredentialsError("Provider credentials are missing from Keychain; sign in again")
        return base64.b64decode("".join(values), validate=True).decode("utf-8")
    except macos_keychain.KEYCHAIN_ERRORS:
        raise ProviderCredentialsError("Provider Keychain is unavailable; unlock it and retry") from None
    except (ValueError, UnicodeError):
        raise ProviderCredentialsError("Provider credentials in Keychain are unreadable; sign in again") from None


def _erase(ref: dict) -> None:
    account = macos_keychain.keychain_account_name()
    try:
        for key in _keys(ref):
            macos_keychain.delete_password(key, account)
    except macos_keychain.KEYCHAIN_ERRORS:
        raise ProviderCredentialsError("Provider Keychain cleanup failed; unlock it and retry") from None


def write(path: Path, text: str, atomic_write) -> None:
    if sys.platform != "darwin":
        atomic_write(path, dpapi.seal(text))
        return
    try:
        old = _reference(path, read_text_with_retry(path))
    except FileNotFoundError:
        old = None
    payload = base64.b64encode(text.encode("utf-8")).decode("ascii")
    chunks = [payload[i:i + CHUNK_SIZE] for i in range(0, len(payload), CHUNK_SIZE)] or [""]
    if len(chunks) > 4096:
        raise ProviderCredentialsError("Provider credential exceeds the supported Keychain size")
    ref = {"service": _service(path), "generation": uuid.uuid4().hex, "chunks": len(chunks)}
    account = macos_keychain.keychain_account_name()
    if any(ord(char) < 32 or ord(char) == 127 for char in account):
        raise ProviderCredentialsError("Provider Keychain account metadata is invalid; no credentials were written")
    for key, chunk in zip(_keys(ref), chunks):
        command = (f"add-generic-password -U -a {macos_keychain._quote(account)} "
            f"-s {macos_keychain._quote(key)} -X {chunk.encode('utf-8').hex()}\n")
        if len(command.encode("utf-8")) > macos_keychain.SECURITY_STDIN_LINE_LIMIT:
            raise ProviderCredentialsError("Provider Keychain metadata exceeds the safe stdin limit; no credentials were written")
    try:
        for key, chunk in zip(_keys(ref), chunks):
            macos_keychain.set_password(key, account, chunk)
            if macos_keychain.get_password(key, account) != chunk:
                raise ProviderCredentialsError("Provider Keychain write verification failed; previous credentials were retained")
        if _read(ref) != text:
            raise ProviderCredentialsError("Provider Keychain write verification failed; previous credentials were retained")
        atomic_write(path, PREFIX + json.dumps(ref, separators=(",", ":")))
    except BaseException as error:
        try:
            _erase(ref)
        except ProviderCredentialsError:
            pass
        if isinstance(error, macos_keychain.KEYCHAIN_ERRORS):
            raise ProviderCredentialsError("Provider credential write failed; previous credentials were retained") from None
        raise
    # The new generation is verified and its nonsecret reference is durable.
    # A cleanup failure must not roll back or delete the valid new generation.
    if old:
        try:
            _erase(old)
        except ProviderCredentialsError:
            pass


def read(path: Path, atomic_write) -> str:
    encoded = read_text_with_retry(path)
    ref = _reference(path, encoded)
    if ref is not None:
        if sys.platform != "darwin":
            raise ProviderCredentialsError("These provider credentials require the original macOS Keychain")
        return _read(ref)
    text = dpapi.unseal(encoded)
    if sys.platform == "darwin":
        # No legacy fallback: return only after Keychain verification and atomic
        # replacement of the legacy file with a secret-free reference succeed.
        write(path, text, atomic_write)
    return text


def delete(path: Path) -> None:
    if sys.platform == "darwin":
        try:
            ref = _reference(path, read_text_with_retry(path))
        except FileNotFoundError:
            return
        if ref is not None:
            _erase(ref)
    path.unlink(missing_ok=True)
