"""Build with `bash macos/build-macos.sh`; no alias/system Python dependency."""

from pathlib import Path
from setuptools import setup


ROOT = Path(__file__).resolve().parent.parent
setup(
    name="Patch Goblins",
    version="0.7.0",
    app=[str(ROOT / "macos" / "main.py")],
    options={
        "py2app": {
            "arch": "arm64",
            "argv_emulation": False,
            "no_chdir": True,
            "packages": ["claude_swap", "textual", "rumps", "truststore"],
            "includes": ["AppKit", "Foundation", "objc"],
            "excludes": ["tkinter", "pytest", "setuptools", "pip"],
            "extra_scripts": [str(ROOT / "macos" / "cswap.py")],
            "iconfile": str(ROOT / "build" / "macos" / "goblin.icns"),
            "resources": [
                str(ROOT / "assets" / "goblin.png"),
                str(ROOT / "LICENSE"),
            ],
            "plist": {
                "CFBundleName": "Patch Goblins",
                "CFBundleDisplayName": "Patch Goblins",
                "CFBundleIdentifier": "com.patchgoblins.app",
                "CFBundleShortVersionString": "0.7.0",
                "LSUIElement": True,
                "LSMinimumSystemVersion": "12.0",
                "NSHighResolutionCapable": True,
                "NSHumanReadableCopyright": "Patch Goblins contributors; claude-swap © Onur Cetinkol, MIT",
            },
        }
    },
)
