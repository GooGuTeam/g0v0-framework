param(
    [Parameter(Mandatory=$true)][string]$Zig,
    [Parameter(Mandatory=$true)][string]$SdlSdk,
    [Parameter(Mandatory=$true)][string]$MixerSdk,
    [string]$Output = "$PSScriptRoot/build"
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Output | Out-Null
& $Zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -shared `
    "$PSScriptRoot/audio_bridge.c" -I "$SdlSdk/include" -I "$MixerSdk/include" `
    "$SdlSdk/lib/x64/SDL3.lib" "$MixerSdk/lib/x64/SDL3_mixer.lib" `
    -o "$Output/g0_audio.dll"
if ($LASTEXITCODE -ne 0) { throw 'Native audio bridge compilation failed' }
Copy-Item "$SdlSdk/lib/x64/SDL3.dll", "$MixerSdk/lib/x64/SDL3_mixer.dll" $Output
# Do not copy optional codec DLLs blindly; review each codec's license first.
