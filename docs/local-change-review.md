# Local change review (2026-10-28)

Review of the framework work since v2026.1019.1-g0v0, with the fixes made during the review and the issues still open. Client findings are in the client's [docs/local-change-review.md](../../g0v0/docs/local-change-review.md).

## Building

- Builds restore the published `g0v0.Veldrid` packages. `-p:G0V0SourceVeldrid=true` (or `-p:G0V0SourceBuild=true`, which the client also understands) references a sibling `g0v0-veldrid` checkout instead; see `Directory.Build.targets`. Packing is rejected while it is set.
- With Veldrid.SPIRV as a project reference, its native library is copied under `runtimes/<rid>/native/` and is missing from `deps.json`. `build/SpirvBesideApplication.targets` copies the build machine's library beside executables on build and publish (Windows, Linux and the universal macOS library).
- Descriptor arrays (`TextureViewArray`) and the bindless APIs were released in `g0v0.Veldrid` 4.9.0-g0v0.4, which the framework now references, so it builds from packages again.
- With `-warnaserror` the framework and client build without warnings. The code-style build (`-p:EnforceCodeStyleInBuild=true`) still reports IDE0073 on every file, because `.editorconfig`'s `file_header_template` holds only the GooGuTeam header while files use the three header forms of the fork. This predates the local work.

## Native image codecs

- `native/ImageCodecs.targets` compiles `g0v0-image-codecs` with CMake when the library for the build machine's platform is missing or older than its sources, including RID-less Linux and macOS builds, which previously had no library. The submodules under `native/image-codecs/third_party` must be initialised. Other RIDs, including Android arm64, need a prebuilt library (see [native/README.md](../native/README.md)).
- **Packaging (was a release blocker):** the image migration removed ImageSharp and StbiSharp, so a framework package without the native library cannot decode any image. Release CI now builds the library on each desktop platform (`build-image-codecs.yml`) and packs it under `runtimes/<rid>/native/` with its licences; packing without it fails. The C++ runtime is linked statically, since the Windows build previously needed the Visual C++ redistributable. The ARM64 (Windows, Linux) and macOS builds have only been compiled in CI, not run. Simd calls the x86-only `__cpuid` on Windows ARM64; `msvc-arm64-cpuid.h` stubs it there, leaving only the logged CPU model empty.
- `NativeImageCodec` checks the library's ABI in its static constructor. A missing library therefore fails every image load in the process, not only the first.
- **Fixed:** `Image.Load(Stream)` copied every input into an unsized `MemoryStream`, regrowing it for large files. It now decodes a `MemoryStream`'s buffer in place and sizes the buffer up front for seekable streams. Each decode still copies the native pixels into a new managed array.

## WASAPI native callback

`WasapiNativeBridge` writes a small machine-code thunk at runtime that calls `BASS_ChannelGetData` without entering the CLR. Decoding the branch offsets showed:

| Architecture | Problem | Status |
| --- | --- | --- |
| x64 | None; offsets, stack alignment and shadow space are correct | — |
| ARM64 | Both `cbz` branches (null context, mixer handle 0) jumped to the call at offset 32 instead of the early return at 56, calling BASS with invalid arguments and then restoring a stack frame that was never pushed. This crashes the audio thread whenever the handle is 0, for example before the mixer exists or during device re-initialisation. | Fixed (`cbz` immediates 14 and 12) |
| x86 | Both `jz` branches targeted offset 38, inside `ret 12`. The bytes there happened to decode as `or al, 0` followed by the zero path. | Fixed (target 40) |

Only x64 has been exercised; the ARM64 and x86 thunks still need testing on hardware.

## Rendering

Reviewed without finding faults:

- Masking and scissor state is applied lazily before geometry (`ApplyPendingMaskingState`). Every submission path applies it: vertex batches, direct draws and static meshes. `Clear` disables the scissor explicitly, so clears are not affected by a pending scissor.
- The texture upload queue deduplicates textures. A texture is queued again whenever its own upload queue goes from empty to non-empty, and `Upload` drains it completely, so no data is left behind.
- `TripleBuffer` readers now wait on an event instead of spinning; the event is reset before the availability check, so a write finishing in between still wakes the reader.
- Shared texture atlases are off by default (`SharedTextureAtlases`), and atlas allocation is locked.
- Expensive operations (shader initialisation) now run for up to 2 ms per frame instead of one per frame.

Not yet reviewed in detail: the deferred renderer changes, bindless/descriptor-array texture tables, region mipmaps and the font atlas packers. Their GPU and unit tests pass.

## Warnings fixed during the review

Constant names were changed to the naming rules (`MixerAudioCapture.SAMPLE_RATE`/`CHANNELS`, `StaticQuadMesh.CHUNK_VERTICES`, `GlyphStore.HIGH_DEFINITION_PREFIX`, `NativeImageCodec.MAXIMUM_BYTES`), together with unused usings, redundant casts and explicit types. `FfmpegOfflineVideoRecording` now passes its cancellation token when filling its buffer pool, and `VeldridShaderPart.WithPassthroughInput` no longer documents its parameters twice.
