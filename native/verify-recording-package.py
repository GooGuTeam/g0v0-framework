#!/usr/bin/env python3
# Copyright (c) GooGuTeam. Licensed under the MIT Licence.
# See the LICENCE file in the repository root for full licence text.

"""Inspect NuGet contents without loading native code or executing a probe."""

import hashlib
from pathlib import Path
import sys
import zipfile

package = Path(sys.argv[1])
source = Path(sys.argv[2])
with zipfile.ZipFile(package) as archive:
    for name in ("g0v0-gpu-video.dll", "g0v0-wasapi-capture.dll"):
        data = archive.read(f"runtimes/win-x64/native/{name}")
        if hashlib.sha256(data).digest() != hashlib.sha256((source / name).read_bytes()).digest():
            raise SystemExit(f"Packaged binary differs from native artifact: {name}")
        if data[:2] != b"MZ":
            raise SystemExit(f"Invalid PE signature: {name}")
        offset = int.from_bytes(data[0x3C:0x40], "little")
        if data[offset:offset + 6] != b"PE\0\0\x64\x86":
            raise SystemExit(f"Expected Windows x64 PE image: {name}")
    archive.getinfo("buildTransitive/g0v0.Framework.targets")
print(f"Verified native assets: {package.name}")
