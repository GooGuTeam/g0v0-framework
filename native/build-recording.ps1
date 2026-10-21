# Copyright (c) GooGuTeam. Licensed under the MIT Licence.
# See the LICENCE file in the repository root for full licence text.
[CmdletBinding()]
param(
    [string]$Zig,
    [string]$VulkanHeaders,
    [switch]$DownloadToolchain,
    [string]$ToolchainDirectory,
    [string]$OutputDirectory,
    [string]$DeployDirectory
)

$ErrorActionPreference = 'Stop'
if (-not $ToolchainDirectory) { $ToolchainDirectory = Join-Path $PSScriptRoot '.toolchain' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'artifacts/win-x64' }
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
if (-not [Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT') {
    throw 'Recording libraries currently target Windows x64 only.'
}
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'recording-toolchain.json') -Raw | ConvertFrom-Json
if ($DownloadToolchain) {
    New-Item -ItemType Directory -Force $ToolchainDirectory | Out-Null
    foreach ($entry in @($manifest.zig, $manifest.vulkanHeaders)) {
        $archive = Join-Path $ToolchainDirectory ($entry.directory + '.zip')
        if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $entry.sha256) {
            Invoke-WebRequest -Uri $entry.url -OutFile $archive
        }
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Toolchain checksum mismatch: $archive"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $ToolchainDirectory $entry.directory))) {
            Expand-Archive -LiteralPath $archive -DestinationPath $ToolchainDirectory -Force
        }
    }
    if (-not $Zig) { $Zig = Join-Path $ToolchainDirectory ($manifest.zig.directory + '/zig.exe') }
    if (-not $VulkanHeaders) { $VulkanHeaders = Join-Path $ToolchainDirectory ($manifest.vulkanHeaders.directory + '/include') }
}
if (-not $Zig -or -not $VulkanHeaders) {
    throw 'Supply Zig and VulkanHeaders, or explicitly enable DownloadToolchain.'
}
$Zig = (Resolve-Path -LiteralPath $Zig).Path
$VulkanHeaders = (Resolve-Path -LiteralPath $VulkanHeaders).Path
if (-not (Test-Path -LiteralPath (Join-Path $VulkanHeaders 'vulkan/vulkan.h'))) {
    throw 'VulkanHeaders must contain vulkan/vulkan.h.'
}
$zigVersion = & $Zig version
if ($LASTEXITCODE -ne 0 -or $zigVersion.Trim() -ne $manifest.zig.version) {
    throw "Expected Zig $($manifest.zig.version)."
}

$video = Join-Path $PSScriptRoot 'gpu-video'
$audio = Join-Path $PSScriptRoot 'wasapi-capture'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$stage = Join-Path $OutputDirectory ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $stage | Out-Null
try {
    # Build libraries only; probes are deliberately excluded.
    & $Zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -Wall -Wextra -Werror -shared `
        (Join-Path $video 'vulkan_recording.cpp') -I $VulkanHeaders `
        -o (Join-Path $stage 'g0v0-gpu-video.dll') `
        -ld3d11 -ldxgi -lmfplat -lmfreadwrite -lmfuuid -lole32 -levr
    if ($LASTEXITCODE -ne 0) { throw 'GPU recording library compilation failed.' }

    & $Zig cc -target x86_64-windows-gnu -std=c11 -O2 -Wall -Wextra -Werror -shared `
        (Join-Path $audio 'capture.c') -o (Join-Path $stage 'g0v0-wasapi-capture.dll')
    if ($LASTEXITCODE -ne 0) { throw 'WASAPI capture library compilation failed.' }

    foreach ($name in @('g0v0-gpu-video.dll', 'g0v0-wasapi-capture.dll')) {
        Copy-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $OutputDirectory $name) -Force
    }
    $hashes = foreach ($name in @('g0v0-gpu-video.dll', 'g0v0-wasapi-capture.dll')) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name"
    }
    [IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS'), $hashes, [Text.UTF8Encoding]::new($false))
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
Write-Output 'Built Windows x64 recording libraries; no probes or tests were run.'

# Never guess which client output directory is in use.
if ($DeployDirectory) {
    $destination = (Resolve-Path -LiteralPath $DeployDirectory).Path
    if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
        throw 'DeployDirectory must be an existing client output directory.'
    }
    foreach ($name in @('g0v0-gpu-video.dll', 'g0v0-wasapi-capture.dll')) {
        $library = Join-Path $OutputDirectory $name
        $target = Join-Path $destination $name
        try {
            Copy-Item -LiteralPath $library -Destination $target -Force
        } catch {
            throw "Could not deploy $target. Choose an unlocked output directory. $($_.Exception.Message)"
        }
        if ((Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
            throw "Deployed library hash mismatch: $target"
        }
        Write-Output "Deployed and verified: $target"
    }
}
