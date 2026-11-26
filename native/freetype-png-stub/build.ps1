# Copyright (c) GooGuTeam. Licensed under the MIT Licence.
# See the LICENCE file in the repository root for full licence text.

# Builds freetype-png-stub.xcframework (static, ios-arm64 and ios-arm64-simulator, matching FreeTypeSharp's own slices)
# into osu.Framework.iOS/native/. The stubs need no SDK headers, so the pinned Zig compiles them on any host:
#   ./native/freetype-png-stub/build.ps1 -DownloadToolchain
[CmdletBinding()]
param(
    [string]$Zig,
    [switch]$DownloadToolchain
)

$ErrorActionPreference = 'Stop'
$native = Split-Path $PSScriptRoot -Parent
if (-not $Zig) {
    $manifest = Get-Content -LiteralPath (Join-Path $native 'recording-toolchain.json') -Raw | ConvertFrom-Json
    $toolchain = Join-Path $native '.toolchain'
    $Zig = Join-Path $toolchain ($manifest.zig.directory + '/zig.exe')
    if (-not (Test-Path -LiteralPath $Zig)) {
        if (-not $DownloadToolchain) { throw 'Supply Zig, or run with -DownloadToolchain.' }
        # build-recording.ps1 downloads and verifies the pinned toolchain.
        & (Join-Path $native 'build-recording.ps1') -DownloadToolchain -OutputDirectory (Join-Path $toolchain 'unused')
    }
}

$source = Join-Path $PSScriptRoot 'png_stubs.c'
$output = Join-Path $native '../osu.Framework.iOS/native/freetype-png-stub.xcframework'
$obj = Join-Path $native 'artifacts/obj/freetype-png-stub'
if (Test-Path -LiteralPath $output) { Remove-Item -Recurse -Force -LiteralPath $output }
New-Item -ItemType Directory -Force $obj | Out-Null

foreach ($slice in @(@{ Id = 'ios-arm64'; Target = 'aarch64-ios.15.0' }, @{ Id = 'ios-arm64-simulator'; Target = 'aarch64-ios.15.0-simulator' })) {
    $o = Join-Path $obj "$($slice.Id).o"
    & $Zig cc -target $slice.Target -ffreestanding -fno-stack-protector -O2 -Wall -Wextra -Werror -c $source -o $o
    if ($LASTEXITCODE -ne 0) { throw "Compiling $($slice.Id) failed." }
    $dir = Join-Path $output $slice.Id
    New-Item -ItemType Directory -Force $dir | Out-Null
    & $Zig ar --format=darwin rcs (Join-Path $dir 'libfreetype-png-stub.a') $o
    if ($LASTEXITCODE -ne 0) { throw "Archiving $($slice.Id) failed." }
}

$plist = @'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>AvailableLibraries</key>
	<array>
		<dict>
			<key>LibraryIdentifier</key>
			<string>ios-arm64</string>
			<key>LibraryPath</key>
			<string>libfreetype-png-stub.a</string>
			<key>SupportedArchitectures</key>
			<array>
				<string>arm64</string>
			</array>
			<key>SupportedPlatform</key>
			<string>ios</string>
		</dict>
		<dict>
			<key>LibraryIdentifier</key>
			<string>ios-arm64-simulator</string>
			<key>LibraryPath</key>
			<string>libfreetype-png-stub.a</string>
			<key>SupportedArchitectures</key>
			<array>
				<string>arm64</string>
			</array>
			<key>SupportedPlatform</key>
			<string>ios</string>
			<key>SupportedPlatformVariant</key>
			<string>simulator</string>
		</dict>
	</array>
	<key>CFBundlePackageType</key>
	<string>XFWK</string>
	<key>XCFrameworkFormatVersion</key>
	<string>1.0</string>
</dict>
</plist>
'@
[System.IO.File]::WriteAllText((Join-Path $output 'Info.plist'), $plist.Replace("`r`n", "`n") + "`n", (New-Object System.Text.UTF8Encoding $false))
Write-Host "Built $((Resolve-Path -LiteralPath $output).Path)"
