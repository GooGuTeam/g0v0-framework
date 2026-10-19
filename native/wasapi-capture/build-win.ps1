param([Parameter(Mandatory=$true)][string]$Zig)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force $out | Out-Null
& $Zig cc -std=c11 -O2 -Wall -Wextra -Werror -shared "$PSScriptRoot/capture.c" -o "$out/g0v0-wasapi-capture.dll"
if ($LASTEXITCODE -ne 0) { throw 'Native bridge compilation failed' }
& $Zig cc -std=c11 -O2 -Wall -Wextra -Werror "$PSScriptRoot/test_capture.c" -o "$out/test_capture.exe"
if ($LASTEXITCODE -ne 0) { throw 'Native test compilation failed' }
& "$out/test_capture.exe"
if ($LASTEXITCODE -ne 0) { throw 'Native capture tests failed' }
