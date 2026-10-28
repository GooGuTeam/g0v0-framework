# Texture upload ownership and alpha fallback

## Changes

- `ArrayPoolTextureUpload` exposes exactly the requested pixel count, including through the byte-oriented `ITextureUpload` interface. Pool capacity must not make an undersized upload appear valid or expose unrelated trailing pixels.
- Negative dimensions and integer multiplication overflow are rejected before renting an array. Zero-sized uploads remain supported.
- Disposal atomically detaches the rented array and returns it once, including with concurrent disposal. Subsequent data access throws. Callers must still finish using previously acquired spans before disposing; this does not make concurrent reading and disposal safe.
- Performance graph loops acquire the pixel span once per loop instead of once per pixel. No runtime speedup is claimed without measurements.
- Implemented the missing `AlphaTextureUpload.ToRgbaUpload()` already referenced by the texture, atlas, OpenGL and Veldrid paths. Conversion expands coverage into white RGB plus the original alpha, matching the alpha shader, and preserves dimensions, bounds and mip level. Alpha inputs now use the image dimension limits and expose only their logical pixel range.

## Validation (Windows, 2026-10-25)

Desktop Debug build with warnings as errors: **0 warnings, 0 errors**. The working tree requires the existing local Veldrid source dependency configuration:

```powershell
dotnet build -c Debug -warnaserror osu-framework.Desktop.slnf -m:1 -nr:false -p:NuGetAudit=false -p:G0V0SourceVeldrid=true
```

NuGetAudit was disabled only for this local command because the vulnerability service was unreachable. No project configuration was changed. After restore, the successful final build used `--no-restore` with the same source dependency configuration.

Focused tests: **21 passed, 0 failed, 0 skipped**, including the new upload ownership and alpha conversion fixtures and existing Veldrid row-stride tests:

```powershell
dotnet test osu.Framework.Tests/bin/Debug/net10.0/osu.Framework.Tests.dll --no-build --filter "FullyQualifiedName~ArrayPoolTextureUploadTest|FullyQualifiedName~AlphaTextureUploadTest|FullyQualifiedName~VeldridTextureUploadTest"
```

The sandboxed test runner stalled at startup; the same tests passed outside the sandbox.

CodeFileSanity reported the repository's expected licence-header noise and an existing filename/type mismatch in `tools/Sdl3AudioSmoke/Program.cs`. Scoped InspectCode completed with exit code 0 and an empty final issue report after fixing its initial findings. Its log still contains toolchain compatibility exceptions (`Unknown tools version: 18.0`), so this is not evidence of a clean full-solution inspection. CodeFileSanity is not recorded as passing. No Linux run, GPU visual test or performance benchmark was performed.
