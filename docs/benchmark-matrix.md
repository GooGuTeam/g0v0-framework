# Benchmark matrix

A benchmark runner built on the visual tests. Each case is an existing `TestScene`, set up through its own steps and sliders, drawn on a real GPU into an offscreen framebuffer. Cases run under configurations built from factors (switches such as per-vertex masking), and a results window shows cases against configurations, with captures, frame times and draw counters for each cell.

Code: `osu.Framework/Testing/Benchmarking` (runner, statistics, results window) and `osu.Framework/Platform/OffscreenGameHost.cs`. The framework's matrix is `osu.Framework.Tests/FrameworkBenchmarkMatrix.cs`.

## Running

From `osu.Framework.Tests` (a Release build is better for timing):

```bash
osu.Framework.Tests.exe --benchmark-matrix                                  # results window; Run starts a matrix
osu.Framework.Tests.exe --benchmark-matrix --run                            # no window; prints a Markdown table
osu.Framework.Tests.exe --benchmark-matrix --capture-ui=matrix.png          # renders the results window offscreen to a PNG
```

Options: `--results=dir` (default `%LOCALAPPDATA%/g0v0-benchmarks/<matrix>`), `--design=factorial`, `--factor=<name>` and `--case=<name>` (repeatable), `--rounds=n`, `--warmup=n`, `--frames=n`.

The framework currently needs the local Veldrid source (`TextureViewArray` and the bindless APIs are not in the published `g0v0.Veldrid`), so build with `-p:G0V0SourceVeldrid=true` as for other framework builds.

In the window, the chips next to "Vary" choose the factors for the next run, and the design button switches between one factor at a time (baseline, each factor changed alone, all changed together) and a full factorial design. While a run is in progress the window drops to 15 frames per second, and partial results fill in as each process finishes.

## Defining a matrix

```csharp
new BenchmarkCase("Rounded masking", typeof(TestSceneMaskingPerformance))
{
    Sliders = new Dictionary<string, double> { ["count"] = 2000, ["corner radius"] = 8 },
},
new BenchmarkCase("Self-overlapping path", typeof(TestSceneDrawablePath))
{
    Test = nameof(TestSceneDrawablePath.TestSelfOverlappingPath),
},
new BenchmarkFactor("Per-vertex masking")
{
    EnabledByDefault = true,
    DisabledSettings = new Dictionary<string, string> { ["PerVertexMasking"] = "false" },
},
```

- A case names a scene and how to set it up: slider values by description, step buttons by text (toggle steps switch on), a `[Test]` method to run as the test browser does (set-up steps, then its steps), or all steps.
- A factor sets environment variables and framework settings (written to the process's `framework.ini`) for each state. The baseline is every factor in its default (shipped) state. Each configuration runs in its own process, so anything read at startup can be a factor.
- Another program uses the same machinery by calling `BenchmarkProgram.TryRun(args, matrix, () => new TheGame())` first thing in `Main`; the game provides the resources its scenes need.

## How a run measures

Each process uses an `OffscreenGameHost`: a windowless D3D11 device on Windows (Metal on macOS, Vulkan on Linux, or Vulkan anywhere with `OfflineVulkanRenderer = true`) whose default framebuffer has a depth attachment like a swapchain's, so the normal front-to-back pass runs. `GameHost` now draws without a window when a host provides the backbuffer size, and applies renderer settings before window creation.

- Frames run on one thread, as fast as possible. Scene time advances 1/60 s per frame, so a scene looks the same at a given frame whatever each frame costs.
- Each frame is timed as update (scene update and draw node generation), draw (CPU submission) and GPU wait. The wait reads back one pixel of the backbuffer: Veldrid's `WaitForIdle` returns at once on D3D11, so an earlier version measured no GPU time at all.
- Per frame it also records draw calls, vertices, texture and shader binds, framebuffer redraws, vertex buffer binds, uniform uploads and allocation, and per case the garbage collections.
- After its warm-up frames each case is held still (scene time frozen, frames still drawn) until 1 s has passed since it started, 3 s for a process's first case. Without this, the first third of a case ran up to 4× slower than the last as tiered compilation caught up; with it, the thirds agree.
- Every case advances scene time by the same number of frames in every configuration. `RNG` is reseeded before each case, so scenes built from random values are identical.
- Each process raises its priority, opts out of Windows power throttling (EcoQoS, applied to processes without a window) and keeps to the fastest cores of a hybrid CPU. On the Core Ultra 7 255HX test machine, the spread of one configuration's medians across processes fell from 14–33% to 0–10% with the core pinning; priority and EcoQoS alone made no difference.
- A priming round runs every configuration once before measuring. It fills the shader caches, which otherwise made each configuration's first process slower, and captures each case at the end of its warm-up. Captures are compared with the baseline's: pixels differing by more than 8/255 in any channel, and the largest difference.

## Statistics

The table shows each cell's median frame time: the median over rounds of each round's median. Machines drift during a matrix. On the test laptop every process slowed by 20–40% a few minutes in, in update, draw and GPU time alike, which looks like the CPU leaving its boost window. Changes are therefore compared round by round:

- Rounds alternate direction (A…E, then E…A), so a steady drift cancels over each pair of rounds.
- A configuration's change is the geometric mean of its per-round ratios to the same round's baseline. It is significant when the 95% t interval of the log ratios excludes zero, with at least three rounds, and the change is at least 1%. With three rounds the interval is wide (t = 4.3), so only clear effects are flagged; the default is four rounds.

In the window, significant cells are tinted (green faster, red slower) and other changes are prefixed with ≈. The detail panel shows the interval, the per-round range, percentiles, the update/draw/GPU split, counters, allocation and the pixel comparison.

## Results directory

`matrix.json` (all runs and pixel comparisons, read by the window), `report.md` (the Markdown table), `runs/` (one JSON per process, rewritten after each case), `captures/<configuration>/<case>.png`, `logs/` (each process's output), `history/` (a copy of each finished matrix) and `host/` (the processes' game storage, including the shader cache, kept between matrices).

## First results

RTX 5060 Laptop GPU with D3D11, 1920×1080, 240 frames after 90 warm-up, four rounds, five of the eight cases. Median frame time in ms; bold changes are significant.

| Case | Defaults | −Per-vertex masking | −Texture slots | +Direct paths | All 3 changed |
| --- | ---: | ---: | ---: | ---: | ---: |
| Boxes | 0.81 | 0.78 −3.9% | 0.81 +1.2% | 0.79 −1.8% | 0.82 +2.1% |
| Rounded masking | 1.39 | 3.30 **+128.1%** | 1.41 +0.4% | 1.40 −2.0% | 3.42 **+141.6%** |
| Glow edge effects | 3.65 | 3.72 +4.0% | 3.27 −9.4% | 3.85 +9.5% | 3.46 −3.8% |
| Sprite text | 1.44 | 1.48 +3.0% | 1.45 +0.8% | 1.58 +12.4% | 1.53 +6.4% |
| Self-overlapping path | 0.15 | 0.14 −8.6% | 0.15 −3.7% | 0.27 **+78.9%** | 0.23 **+54.8%** |

- Without per-vertex masking, 2,000 rounded containers take 2,025 draw calls instead of 25, and draw submission time rises from 0.59 to 2.14 ms.
- Direct paths draw the self-overlapping path at 0.27 ms against 0.15 ms: the mesh is rebuilt on the CPU every frame (draw time 0.02 → 0.08 ms), as the slider research found. Its capture differs from the framebuffer path in 0.004% of pixels, by at most 45/255, at antialiased edges.
- No configuration changed the captures of the other cases by a single pixel.
- Texture slots show no significant effect on this GPU at these scenes' sizes.

## Not covered

- Only D3D11 on one Windows laptop was run. The Vulkan, shared atlas and single-channel font factors, the factorial design, and macOS/Linux were not run. OpenGL has no offscreen device here.
- The results window was checked by rendering it offscreen (`--capture-ui`); it was not opened as a window, and its Run button was not used (the console runner shares the same code).
- Cases below about 0.3 ms are at the noise floor of a per-frame GPU wait.
- The client's gameplay scenes are not wired in yet; that needs `BenchmarkProgram.TryRun` in the client's test program and a client matrix.
