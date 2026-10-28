# Rendering CPU optimisations

This batch retains the existing slider meshes, range handling and replay-seeking fix. It does not change slider shaders, offscreen rendering, draw order or GPU synchronisation.

## Text geometry reuse

Files:
- `osu.Framework/Graphics/Sprites/SpriteText.cs`
- `osu.Framework/Graphics/Sprites/SpriteText.DrawNode.cs`

Character layout regeneration increments a version, including regeneration to an empty layout. Each draw node retains its own screen-space glyph list, keyed by the layout version, screen transform and inflation amount.

The lazy character layout is resolved before comparing the version and obtaining the screen transform, since layout can change the drawable's size. Colour-only changes can reuse glyph geometry. Text, layout, transform and inflation changes regenerate it. Shadow parameters and drawable colour continue to update independently, and character/shadow ordering is unchanged.

This avoids repeated per-character coordinate calculations during fades or colour changes. It does not eliminate per-frame vertex submission or rendering, and moving/scaling text still requires geometry updates.

A subsequent [text submission optimisation](text-submission-optimisation.md) adds cached shadow geometry and pixel areas, applied-state shadow colour, shared glyph-pair texture preparation and per-draw vertex colour/depth initialisation. Its client output is `Debug-render-text-submit`; the build below records this earlier batch.

## Active shader binding lists

File: `osu.Framework/Graphics/Veldrid/Pipelines/GraphicsPipeline.cs`

The existing resource-layout invalidation now also rebuilds compact lists of texture units and uniform names used by the current shader. Draws iterate these lists instead of repeatedly checking every attachment accumulated earlier in the frame against the shader.

The lists cache layout mappings only. Each draw still fetches the latest attached resources, calls `GetResourceSet()`, resolves current uniform offsets and submits resource bindings. Replacing a buffer or texture in an occupied slot therefore needs no layout rebuild, while pending uniform uploads and Deferred range changes remain effective.

Shader changes, newly occupied slots and frame resets invalidate the mappings. Disposal and frame resets clear the cached lists. This applies to ordinary Veldrid rendering and the Deferred event processor's graphics pipeline.

## Upload bookkeeping and disposal

- `osu.Framework/Graphics/Veldrid/Textures/VeldridTexture.cs`: replace the unused uploaded-region list with a local boolean. The custom region-based mipmap prototype remains disabled. Upload order, disposal, queue locking and whole-texture mipmap generation are unchanged.
- `osu.Framework/Graphics/Containers/BufferedContainer.DrawNode.cs`: dispose the grayscale parameter buffer alongside the blur parameter buffer. Backend-managed delayed disposal remains in use.

## Build and limitations

The desktop client was built against the local framework, resources and Veldrid projects:

```text
dotnet build osu.Desktop/osu.Desktop.csproj -c Debug -warnaserror -p:G0V0SourceBuild=true
```

Result: **0 warnings, 0 errors**, 25.67 seconds.

No tests, application launches, shader probes or benchmarks were run. The expected benefit is reduced CPU-side redundant work; no frame-time improvement or runtime visual equivalence has been measured. The disposal change addresses resource lifetime rather than claiming an immediate FPS improvement.
