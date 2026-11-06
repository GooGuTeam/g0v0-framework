# Empty texture upload queue optimisation

## Scope

This change follows the text submission optimisation. Normal texture binding checks for pending uploads on every glyph, even when the font atlas is unchanged. The empty dequeue attempt previously acquired the texture's queue lock each time.

A subsequent [global upload-queue optimisation](texture-upload-queue-optimisation.md) replaces linear membership scans during scheduling. Its client output is `Debug-render-upload-queue`; this document and build record describe the earlier local empty-queue batch.

Changed implementation files:

- `osu.Framework/Graphics/OpenGL/Textures/GLTexture.cs`
- `osu.Framework/Graphics/Veldrid/Textures/VeldridTexture.cs`

Both retain the existing queue and introduce one private `pendingUploadCount` integer. Empty dequeue attempts read it with `Volatile.Read()` and return without acquiring the lock. Non-empty attempts still acquire the original lock and recheck the actual queue before dequeueing.

This applies to ordinary GL and Veldrid textures, including their video subclasses. Deferred ordinary textures also use `VeldridTexture`; its separate framebuffer texture implementation is unchanged. The optimisation is shared by text, sprites and other normal texture users. Dummy textures are unchanged.

## Publication and concurrency

- `SetData()` enqueues under the original queue lock, then publishes the queue's count with `Volatile.Write()` before global scheduling.
- Successful dequeue publishes the remaining count under the same lock.
- Actual queue inspection and mutation remain locked. The fast path never reads `Queue.Count` without synchronisation.
- A positive read may become stale before acquiring the lock, for example if `FlushUploads()` removed the work. The existing empty check inside the lock handles that case.
- A zero read may overlap a concurrent enqueue. That upload remains queued and is considered on a subsequent call; the empty result does not mean that producers cannot enqueue immediately afterwards.
- `Upload()` checks again on every call. There is no frame-level latch, so later binds in the same frame can observe newly published atlas uploads.
- `FlushUploads()` and delayed disposal drain through the same helper and therefore update the published count. As before, they do not prevent a concurrent producer from enqueueing after they observe an empty queue.

The count describes queued items, excluding an item already dequeued and being processed. It is not a GPU completion signal. The existing locked `UploadComplete` getter remains unchanged, including its queue-empty semantics. No GPU fences or completion checks are replaced by this field.

## Preserved paths

Availability checks and draw-thread validation remain in their original positions in `Upload()`. A dequeued item still goes through the original `DoUpload()`, disposal and mipmap paths. Empty upload objects used to allocate or resize framebuffer storage are real queue entries and still execute.

Global queue membership, frame budgets, bypass-queue behaviour, upload ordering, renderer bindings and resource retirement are unchanged. No new lock ordering is introduced. The unused `IsQueuedForUpload` properties are not repurposed as scheduling flags.

The GL uploaded-region bookkeeping and disabled regional mipmap prototype are unchanged. The earlier Veldrid boolean bookkeeping optimisation remains in place. No shaders, glyph geometry, text order or texture coordinates were changed in this batch.

## Expected effect and limitations

For an already-drained texture, an upload dequeue attempt replaces monitor entry/exit with one published-count read and branch. This removes an unnecessary lock from repeated stable glyph/atlas binding. Actual uploads incur a count publication after enqueue and dequeue and retain their locks; this is not a lock-free upload queue.

No reduction in GPU vertex count, texture upload bytes or draw calls is claimed. Actual timing, concurrency behaviour under load and cross-backend rendering have not been measured or exercised at runtime.

## Client build

Built from `../g0v0` against the existing local source dependencies:

```text
dotnet build osu.Desktop/osu.Desktop.csproj -c Debug -warnaserror -p:G0V0SourceBuild=true
```

Result: **0 warnings, 0 errors**, 39.53 seconds.


Only source inspection and the client build were performed. No tests, application launches, probes, benchmarks or additional analysis tools were run. Existing slider, text and whole-quad optimisations remain in place; no client implementation or project-reference files were changed for this batch.
