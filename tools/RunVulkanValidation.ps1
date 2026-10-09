param(
    [ValidateSet('veldrid', 'deferred')][string]$Renderer = 'veldrid',
    [string]$LayerPath = "$env:LOCALAPPDATA\g0v0-tools\vulkan\1.4.363.0\Bin",
    [string]$TestFilter = 'TestSceneScissor,TestSceneStencil,TestSceneShaderStorageBufferObject,TestSceneMasking,TestSceneBlending,TestSceneComplexBlending,TestSceneDynamicDepth,TestSceneBufferedContainer,TestSceneCachedBufferedContainer,TestSceneFrontToBackBufferedContainer,TestSceneTexturePremultiplication,TestSceneTextureCropping,TestSceneTexturedTriangle,TestSceneDrawNodeDisposal,TestSceneGammaCorrection',
    [int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\osu.Framework.Tests\bin\Debug\net10.0\osu.Framework.Tests.exe'
if (!(Test-Path "$LayerPath\VkLayer_khronos_validation.json")) { throw "Validation layer not found: $LayerPath" }
if (!(Test-Path $exe)) { throw 'Build osu.Framework.Tests first.' }
$names = @('VK_LAYER_PATH','VK_INSTANCE_LAYERS','VK_LAYER_SETTINGS_PATH','VK_LOADER_DEBUG','OSU_GRAPHICS_SURFACE','OSU_GRAPHICS_RENDERER','VELDRID_VK_FSE')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$dir = Join-Path $env:TEMP ("g0v0-vulkan-validation\{0}-{1}" -f $Renderer,(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $dir | Out-Null
try {
    $env:VK_LAYER_PATH = $LayerPath
    $env:VK_INSTANCE_LAYERS = 'VK_LAYER_KHRONOS_validation'
    $env:VK_LAYER_SETTINGS_PATH = $PSScriptRoot
    $env:VK_LOADER_DEBUG = 'error,warn,layer'
    $env:OSU_GRAPHICS_SURFACE = 'Vulkan'
    $env:OSU_GRAPHICS_RENDERER = $Renderer
    $env:VELDRID_VK_FSE = '0'
    $p = Start-Process $exe -ArgumentList '--benchmark',"--test-filter=$TestFilter" -RedirectStandardOutput "$dir\stdout.log" -RedirectStandardError "$dir\stderr.log" -PassThru
    # Keep the process handle open so ExitCode remains available after process termination.
    $processHandle = $p.Handle
    "PID=$($p.Id) Logs=$dir"
    $reason = 'exited'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (!$p.HasExited) {
        Start-Sleep -Seconds 2
        if ((Get-Date) -gt $deadline) { $reason = 'timeout'; break }
        if (Select-String "$dir\stdout.log" -Pattern 'triggered an error' -Quiet) { $reason = 'step failure'; break }
        if ((Get-Item "$dir\stdout.log").Length -gt 50MB) { $reason = 'log size limit'; break }
        $p.Refresh()
    }
    if (!$p.HasExited) {
        $null = $p.CloseMainWindow()
        if (!$p.WaitForExit(10000)) { $p.Kill() }
    }
    $p.WaitForExit()
    $exitCode = $p.ExitCode
    if ($null -eq $exitCode) { $exitCode = 'unavailable' }
    "Reason=$reason ExitCode=$exitCode" | Set-Content "$dir\status.txt"
    Get-Content "$dir\status.txt"
    # Completion alone does not imply validation success. Inspect VUID/SYNC-HAZARD messages in both logs.
}
finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
