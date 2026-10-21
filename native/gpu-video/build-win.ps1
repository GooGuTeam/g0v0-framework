param([Parameter(Mandatory=$true)][string]$Zig)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force $out | Out-Null
& $Zig c++ -DG0_VIDEO_DIAGNOSTICS -std=c++17 -O2 -Wall -Wextra -Werror -municode "$PSScriptRoot/test_encoder.cpp" -o "$out/test_encoder.exe" -ld3d11 -lmfplat -lmfreadwrite -lmfuuid -lole32 -levr
if ($LASTEXITCODE -ne 0) { throw 'Encoder probe compilation failed' }
& $Zig c++ -std=c++17 -O2 -Wall -Wextra -Werror -municode "$PSScriptRoot/verify.cpp" -o "$out/verify.exe" -lmfplat -lmfreadwrite -lmfuuid -lole32
if ($LASTEXITCODE -ne 0) { throw 'Decoder probe compilation failed' }
& "$out/test_encoder.exe" "$out/probe.mp4"
if ($LASTEXITCODE -ne 0) { throw 'GPU encoder probe failed' }
& "$out/verify.exe" "$out/probe.mp4"
if ($LASTEXITCODE -ne 0) { throw 'Encoded file validation failed' }
