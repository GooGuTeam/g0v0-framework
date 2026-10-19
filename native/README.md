# Recording native integration

The recording libraries target **Windows x64**. Linux/macOS offline export uses FFmpeg. Windows ARM64/x86 native recording is not supplied.

## Build-only entry point

```powershell
./native/build-recording.ps1 -DownloadToolchain
# Or use an existing toolchain, without downloads:
./native/build-recording.ps1 -Zig C:/tools/zig/zig.exe -VulkanHeaders C:/tools/Vulkan-Headers/include
```

Zig 0.14.1 and Vulkan-Headers 1.4.309 are pinned in `recording-toolchain.json`; downloads are checked against SHA-256 before extraction. Downloading is opt-in. The compiler explicitly targets `x86_64-windows-gnu` with optimisation and warnings-as-errors. Neither probes nor tests are built or executed.

Outputs are `native/artifacts/win-x64/g0v0-gpu-video.dll`, `g0v0-wasapi-capture.dll` and `SHA256SUMS`. Compilation uses a temporary staging directory and copies DLLs only after both libraries compile. Toolchains and outputs are ignored by Git. Use `-OutputDirectory` to change the destination or `-DeployDirectory` to explicitly copy and hash-check DLLs into an existing, unlocked client output directory.

## MSBuild

`osu.Framework.csproj` imports `native/RecordingNative.targets`:

- `-p:BuildRecordingNative=true` invokes the same build-only script.
- Supply `RecordingZig` and `RecordingVulkanHeaders`, or explicitly set `RecordingNativeDownloadToolchain=true`.
- `RecordingNativeDirectory` can point to a downloaded artifact directory.
- Built libraries propagate through project-reference output copying for Windows desktop x64. Other targets do not receive these files as root-level content.
- `-p:PackRecordingNative=true` requires both DLLs and packs `runtimes/win-x64/native/` plus a `buildTransitive` target for RID-less Windows desktop consumers. RID-specific consumers use standard NuGet runtime assets.

Do not publish packages using temporary local framework/Veldrid references. The presentation API changes still require an approved Veldrid package before the framework's package-only build can pass.

## CI/CD

`build-recording-native.yml` is a reusable build-only workflow on Windows 2022. It caches the pinned toolchain, compiles both DLLs, inspects an isolated NuGet packaging artifact and uploads DLLs with checksums. It is called by framework CI and release. Existing managed test jobs are unchanged; no native probe job is added.

The release job waits for native compilation, downloads and verifies hashes on Linux, packs native assets into the framework package and verifies package entries/hashes/PE architecture before the existing publication steps. The inspection script never loads native code. No publication has been triggered locally.

Local validation covers compilation, MSBuild copying and static package inspection. Hosted workflow execution, code signing, FSE hardware behaviour and supported-device coverage remain unverified. Native binaries are unsigned.
