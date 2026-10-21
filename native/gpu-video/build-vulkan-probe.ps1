param(
    [Parameter(Mandatory=$true)][string]$Zig,
    [Parameter(Mandatory=$true)][string]$VulkanInclude
)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force $out | Out-Null
& $Zig c++ -std=c++17 -O2 -Wall -Wextra -Werror "-I$VulkanInclude" "$PSScriptRoot/test_vulkan_interop.cpp" -o "$out/test_vulkan_interop.exe" -ld3d11 -ldxgi
if ($LASTEXITCODE -ne 0) { throw 'Vulkan interop probe compilation failed' }
& "$out/test_vulkan_interop.exe"
if ($LASTEXITCODE -ne 0) { throw 'Vulkan interop probe failed or no supported adapter was found' }
