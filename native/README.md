# Native libraries

## Image codecs

`image-codecs/` builds `g0v0-image-codecs`, which decodes and resizes images for `osu.Framework.Imaging` (Wuffs, stb_image_write, Simd and cpu_features, all as submodules under `image-codecs/third_party`). Initialise them once:

```bash
git submodule update --init --recursive
```

`ImageCodecs.targets` copies the library for the target platform from `artifacts/<rid>/image-codecs/` into the output. When the target is the build machine's own platform (RID-less builds, or a RID matching the host), the library is compiled with CMake whenever it is missing or older than its sources, so a C++ toolchain and CMake 3.24+ are required. `-p:BuildImageCodecs=false` requires a prebuilt library instead; `-p:ImageCodecsDirectory=<dir>` points at one.

Other RIDs need a prebuilt library. Android arm64 is cross-compiled with the NDK:

```bash
cmake -S native/image-codecs -B native/artifacts/obj/image-codecs-android-arm64 -G Ninja   -DCMAKE_TOOLCHAIN_FILE=$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake   -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-24 -DCMAKE_BUILD_TYPE=Release
cmake --build native/artifacts/obj/image-codecs-android-arm64
cmake --install native/artifacts/obj/image-codecs-android-arm64 --prefix native/artifacts/android-arm64/image-codecs
```

The library links the C++ runtime statically (no Visual C++ redistributable or system libstdc++ needed).

Android builds are linked for 16 KB pages, which Android 16 requires.

iOS loads the library as an embedded framework. CI builds `g0v0-image-codecs.framework` for devices and Apple silicon simulators (both arm64) and combines them into `artifacts/ios/image-codecs/g0v0-image-codecs.xcframework`. `osu.Framework.iOS` resolves it at `@rpath/g0v0-image-codecs.framework`, and source iOS builds embed it when it is present there.

The `g0v0.Framework` package carries the library under `runtimes/<rid>/native/` for win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64 and android-arm64, and as `runtimes/ios/native/g0v0-image-codecs.xcframework`, with the third-party licences under `licences/`. The Android library also stops NuGet from falling back to the glibc linux-arm64 one, which the Android RID graph would otherwise select. `.github/workflows/build-image-codecs.yml` builds each platform on its own runner; the release workflow downloads them to `artifacts/<rid>/image-codecs/` (`artifacts/ios/image-codecs/` for iOS) and packs with `-p:PackImageCodecs=true -p:BuildImageCodecs=false`, then checks the package with `verify-image-codecs-package.py`. Packing without `PackImageCodecs` fails, so a package never ships without the decoder.

Releases also publish `g0v0.Framework.Android` and `g0v0.Framework.iOS` at the same version. They are packed on Windows and on the Xcode runner, since each needs its platform workload. Other Android ABIs and Intel simulators have no image codec library.

## Recording


The recording libraries target **Windows x64**. Linux/macOS offline export uses FFmpeg. Windows ARM64/x86 native recording is not supplied.

### Build-only entry point

```powershell
./native/build-recording.ps1 -DownloadToolchain
# Or use an existing toolchain, without downloads:
./native/build-recording.ps1 -Zig C:/tools/zig/zig.exe -VulkanHeaders C:/tools/Vulkan-Headers/include
```

Zig 0.14.1 and Vulkan-Headers 1.4.309 are pinned in `recording-toolchain.json`; downloads are checked against SHA-256 before extraction. Downloading is opt-in. The compiler explicitly targets `x86_64-windows-gnu` with optimisation and warnings-as-errors. Neither probes nor tests are built or executed.

Outputs are `native/artifacts/win-x64/g0v0-gpu-video.dll`, `g0v0-wasapi-capture.dll` and `SHA256SUMS`. Compilation uses a temporary staging directory and copies DLLs only after both libraries compile. Toolchains and outputs are ignored by Git. Use `-OutputDirectory` to change the destination or `-DeployDirectory` to explicitly copy and hash-check DLLs into an existing, unlocked client output directory.

### MSBuild

`osu.Framework.csproj` imports `native/RecordingNative.targets`:

- `-p:BuildRecordingNative=true` invokes the same build-only script.
- Supply `RecordingZig` and `RecordingVulkanHeaders`, or explicitly set `RecordingNativeDownloadToolchain=true`.
- `RecordingNativeDirectory` can point to a downloaded artifact directory.
- Built libraries propagate through project-reference output copying for Windows desktop x64. Other targets do not receive these files as root-level content.
- `-p:PackRecordingNative=true` requires both DLLs and packs `runtimes/win-x64/native/` plus a `buildTransitive` target for RID-less Windows desktop consumers. RID-specific consumers use standard NuGet runtime assets.

Do not publish packages using temporary local framework/Veldrid references. The presentation API changes still require an approved Veldrid package before the framework's package-only build can pass.

### CI/CD

`build-recording-native.yml` is a reusable build-only workflow on Windows 2022. It caches the pinned toolchain, compiles both DLLs, inspects an isolated NuGet packaging artifact and uploads DLLs with checksums. It is called by framework CI and release. Existing managed test jobs are unchanged; no native probe job is added.

The release job waits for native compilation, downloads and verifies hashes on Linux, packs native assets into the framework package and verifies package entries/hashes/PE architecture before the existing publication steps. The inspection script never loads native code. No publication has been triggered locally.

Local validation covers compilation, MSBuild copying and static package inspection. Hosted workflow execution, code signing, FSE hardware behaviour and supported-device coverage remain unverified. Native binaries are unsigned.
