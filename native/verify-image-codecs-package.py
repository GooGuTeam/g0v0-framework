#!/usr/bin/env python3
# Copyright (c) GooGuTeam. Licensed under the MIT Licence.
# See the LICENCE file in the repository root for full licence text.

"""Check that a framework package carries the image codec library for every platform, unchanged, with its licences."""

import hashlib
from pathlib import Path
import sys
import zipfile

package = Path(sys.argv[1])
artifacts = Path(sys.argv[2])
libraries = {
    "win-x64": ("g0v0-image-codecs.dll", b"MZ"),
    "win-arm64": ("g0v0-image-codecs.dll", b"MZ"),
    "linux-x64": ("libg0v0-image-codecs.so", b"\x7fELF"),
    "linux-arm64": ("libg0v0-image-codecs.so", b"\x7fELF"),
    "android-arm64": ("libg0v0-image-codecs.so", b"\x7fELF"),
    "osx-x64": ("libg0v0-image-codecs.dylib", b"\xcf\xfa\xed\xfe"),
    "osx-arm64": ("libg0v0-image-codecs.dylib", b"\xcf\xfa\xed\xfe"),
}
xcframework = "g0v0-image-codecs.xcframework"


def check_same(entry, source):
    if hashlib.sha256(archive.read(entry)).digest() != hashlib.sha256(source.read_bytes()).digest():
        raise SystemExit(f"Packaged file differs from native artifact: {entry}")


with zipfile.ZipFile(package) as archive:
    for rid, (name, magic) in libraries.items():
        entry = f"runtimes/{rid}/native/{name}"
        check_same(entry, artifacts / rid / "image-codecs" / name)
        if not archive.read(entry).startswith(magic):
            raise SystemExit(f"Unexpected binary format: {entry}")

    # Every file of the xcframework, including one framework binary per slice (device and simulator).
    source = artifacts / "ios" / "image-codecs" / xcframework
    binaries = 0
    for path in source.rglob("*"):
        if path.is_file():
            entry = f"runtimes/ios/native/{xcframework}/{path.relative_to(source).as_posix()}"
            check_same(entry, path)
            if path.parent.name == "g0v0-image-codecs.framework" and path.name == "g0v0-image-codecs":
                if not archive.read(entry).startswith(b"\xcf\xfa\xed\xfe"):
                    raise SystemExit(f"Unexpected binary format: {entry}")
                binaries += 1
    if binaries != 2:
        raise SystemExit(f"Expected device and simulator slices in {xcframework}, found {binaries}.")

    for licence in ("LICENCE-Wuffs-MIT", "LICENCE-stb", "LICENCE-Simd", "LICENCE-cpu_features"):
        archive.getinfo(f"licences/{licence}")
print(f"Verified image codec assets: {package.name}")
