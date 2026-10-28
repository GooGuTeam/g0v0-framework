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
    "osx-x64": ("libg0v0-image-codecs.dylib", b"\xcf\xfa\xed\xfe"),
    "osx-arm64": ("libg0v0-image-codecs.dylib", b"\xcf\xfa\xed\xfe"),
}
with zipfile.ZipFile(package) as archive:
    for rid, (name, magic) in libraries.items():
        data = archive.read(f"runtimes/{rid}/native/{name}")
        if hashlib.sha256(data).digest() != hashlib.sha256((artifacts / rid / "image-codecs" / name).read_bytes()).digest():
            raise SystemExit(f"Packaged library differs from native artifact: {rid}/{name}")
        if not data.startswith(magic):
            raise SystemExit(f"Unexpected binary format: {rid}/{name}")
    for licence in ("LICENCE-Wuffs-MIT", "LICENCE-stb", "LICENCE-Simd", "LICENCE-cpu_features"):
        archive.getinfo(f"licences/{licence}")
print(f"Verified image codec assets: {package.name}")
