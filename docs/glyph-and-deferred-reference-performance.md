# Glyph extraction and deferred resource references

## Changes

`GlyphStore` caches the normal and ASCII-HD resource-name prefixes when constructed. `Get()` and `GetAsync()` no longer construct those strings for each lookup.

Normal-resolution glyph extraction copies alpha directly over the readable rows and columns. The zero-initialised output keeps pixels outside a trimmed sprite sheet transparent. The existing low-resolution coverage average is retained. Successful glyph extraction increments `LoadedGlyphCount` once; the previous working-tree implementation incremented it twice.

Primitive upload events use `ResourceAllocator.ReferenceAdjacent()` to reuse the last resource ID when the next reference has the same object identity, including null. A run of 10,000 references to one batch uses one resource-table slot. A different intervening resource creates another slot, and `NewFrame()` clears the table as before. Ordinary `Reference()` calls retain the original append path. IDs remain object handles and events remain separate, in their original order. This reduces resource-table writes and growth; it does not combine primitive events or GPU draws.

## Regression coverage

- Glyph namespace matching, rectangular trimmed pages, transparent output padding, low-resolution alpha averaging and one increment per extraction.
- Adjacent and non-adjacent resource identities, null, frame reset and objects whose equality/hash implementations throw.
- Existing masking batching and texture-upload ownership tests are included in the focused suite.

## Measurement

`BenchmarkGlyphExtraction` compares the previous generic extraction loop with the actual new `LoadCharacter()` path using a fixed synthetic page and an embedded Roboto glyph. Font loading happens outside the measurement. Both methods allocate and dispose the same alpha-upload result. Normal and low-resolution modes are measured separately.

`BenchmarkResourceReference` compares the previous append-only implementation with the actual allocator for 10,000 references, both to one repeated object and to alternating objects. The lists retain capacity after warmup, so steady-state allocation figures do not measure the reduction in retained resource-table capacity. These are isolated CPU microbenchmarks, not application FPS measurements.

Initial Windows Release microbenchmark, .NET 10.0.12 / Intel Core Ultra 7 255HX, BenchmarkDotNet 0.15.8 in-process, three warmups and six measured iterations:

| Glyph extraction | Previous generic loop | Actual new path | Ratio |
| --- | ---: | ---: | ---: |
| Normal resolution | 10.073 us | 2.407 us | 0.24 |
| Low resolution | 6.812 us | 7.190 us | 1.06 |

The normal-resolution measurement is about 76% lower. Low-resolution confidence intervals overlap; no improvement is claimed there. Allocation remains 4.23 KB and 1.16 KB respectively, since the coverage output still needs its own storage. Namespace-prefix allocation removal is separate and was not measured by this extraction benchmark.

The first allocator prototype applied identity checks to every `Reference()` call. It reduced a 10,000-reference repeated-object run from 16.924 us to 5.349 us, but increased the alternating-object run from 17.301 us to 23.091 us. The final implementation therefore limits this check to the primitive-event path.

Final allocator run, with the same benchmark configuration (times cover 10,000 references):

| Workload | Previous append baseline | Ordinary `Reference()` control | Primitive `ReferenceAdjacent()` |
| --- | ---: | ---: | ---: |
| Repeated same object | 22.931 us | 19.397 us | 5.684 us |
| Alternating objects | 24.355 us | 22.754 us | Not used for ordinary events |

The primitive path takes about 75% less time than the old append baseline, or 71% less than the actual ordinary-reference control in this run. The ordinary alternating path retains its original implementation and shows no measured regression. Differences between the two append implementations, and absolute times across runs, include benchmark/JIT/clock variation and are not claimed as improvements to unchanged code. All allocator cases report zero steady-state allocations after warmup; the reduced resource-table slot count is established by the regression test instead.

Reports: `artifacts/benchmarks-perf-luna/results/*BenchmarkGlyphExtraction-report-github.md` and `artifacts/benchmarks-perf-luna-final/results/*BenchmarkResourceReference-report-github.md`. Build/test logs use the `*-perf-luna-final.log` names; the Release build is `build-perf-luna-release.log`.

## Validation commands and limits

The focused Windows Debug suite passed **35 tests**, including the revised opt-in/ordinary-reference mixed cases. Both the full desktop Debug build and the Release benchmark project built with **zero warnings and errors**. The builds use the same existing local source-dependency override:

```powershell
dotnet build -c Debug -warnaserror osu-framework.Desktop.slnf --no-restore -m:1 -nr:false -p:G0V0SourceVeldrid=true
dotnet test osu.Framework.Tests/bin/Debug/net10.0/osu.Framework.Tests.dll --no-build --filter "FullyQualifiedName~FontStoreTest|FullyQualifiedName~ResourceAllocatorTest|FullyQualifiedName~MaskingBatchingTest|FullyQualifiedName~ArrayPoolTextureUploadTest|FullyQualifiedName~AlphaTextureUploadTest"
dotnet build osu.Framework.Benchmarks/osu.Framework.Benchmarks.csproj -c Release -warnaserror --no-restore -m:1 -nr:false -p:G0V0SourceVeldrid=true
dotnet osu.Framework.Benchmarks/bin/Release/net10.0/osu.Framework.Benchmarks.dll --filter "*BenchmarkResourceReference*" "*BenchmarkGlyphExtraction*" --inProcess --job Short --warmupCount 3 --iterationCount 6 --launchCount 1 --artifacts artifacts/benchmarks-perf-luna
dotnet osu.Framework.Benchmarks/bin/Release/net10.0/osu.Framework.Benchmarks.dll --filter "*BenchmarkResourceReference*" --inProcess --job Short --warmupCount 3 --iterationCount 6 --launchCount 1 --artifacts artifacts/benchmarks-perf-luna-final
```

Final scoped InspectCode produced no findings in the new APIs, tests and benchmark code. Findings remain in the pre-existing HD-font selection and low-resolution loop, and the pinned tool continues logging .NET toolchain compatibility exceptions. This is not a clean full-repository analysis result. CodeFileSanity retains the existing `tools/Sdl3AudioSmoke/Program.cs` filename/type mismatch apart from expected fork licence-header noise.

No Linux run was available: this Windows host has no installed WSL environment. No new GPU pixel comparison or end-to-end client frame-time measurement was run for this batch.

## Follow-up found during review

An independent Luna review identified an existing exact-fit boundary discrepancy in `Deferred/Allocation/VertexManager.cs`: writes switch buffers at `>= 2 MiB`, while the draw capacity calculation permits an exact fit. An 8-byte point vertex can expose this at 262,144 points. This needs a separate boundary regression and paired write/draw fix before extending deferred events to contain multiple primitives. This batch leaves primitive event sizes unchanged.
