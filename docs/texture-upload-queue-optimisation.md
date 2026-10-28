# Global texture upload queue optimisation

## Scope

`osu.Framework/Graphics/Rendering/Renderer.cs` now uses a renderer-owned FIFO `Queue<INativeTexture>` and a reference-identity `HashSet<INativeTexture>` under the same short lock. This replaces the linear `ConcurrentQueue.Contains(texture)` scan followed by a separate enqueue.

The earlier per-texture empty-upload fast path is retained. No texture backend, glyph layout, shader, draw order or GPU synchronisation code changes in this batch. GL, ordinary Veldrid and Deferred share this base renderer scheduling path. Separate renderer instances have separate queues and membership sets.

## Scheduling invariant

The membership set tracks physical entries waiting in the FIFO, excluding a texture already dequeued for processing. It is not an upload-completion or GPU-in-flight set.

- Before renderer initialisation, enqueue requests are still ignored without inserting membership.
- Enqueue adds to the membership set and appends to the FIFO while holding the same lock. Duplicate requests for the same object do not append another entry.
- Dequeue removes the FIFO entry and its membership while holding that lock.
- `Upload()` runs only after the dequeue helper returns and releases the lock.
- Texture uploads triggered directly by binding do not remove membership. An existing global entry remains valid, can service newly queued local data, or can later become a harmless no-op.
- Local `FlushUploads()` and delayed texture disposal likewise leave physical global entries alone. The normal dequeue path eventually removes their membership. No separate membership-clear path can leave a FIFO entry untracked.

Reference equality deliberately identifies the actual native texture object; no virtual texture equality or hashing code executes under the queue lock. Collection backing storage can grow with peak queued texture count, and the set adds CPU bookkeeping storage proportional to distinct queued objects.

## Lock ordering and arrival races

A texture's `SetData()` holds its local upload-queue lock while requesting global scheduling. The global lock therefore must never be held while calling `Upload()`, `FlushUploads()`, disposal or other texture operations. The implementation confines the global lock to collection operations and the queue-count snapshot.

Removing membership before upload permits a subsequent local empty-to-nonempty transition to schedule the same texture again. If the current upload consumes that new work, the later global entry is an ordinary no-op. If data arrives after the upload drain ends, the subsequent entry still provides a scheduling opportunity. There is no completion-time removal that could erase a newly queued entry.

If new data arrives while the texture is still physically queued, duplicate suppression retains that entry rather than moving the texture to the back. Accepted entries remain FIFO. The operation eliminates the old check/enqueue race without changing per-texture upload order.

## Budgets and statistics

`BeginFrame()` obtains one locked queue-length snapshot for both the queued statistic and the existing `Math.Clamp(count / 2, 1, MaxTexturesUploadedPerFrame)` target. Previously those were separate concurrent count reads; concurrent arrivals can consequently be accounted for at a slightly different instant. The target formula is unchanged.

Actual `Upload()` calls remain outside the queue lock. The existing dequeued/performed counters, successful-upload limit, pixel-budget arithmetic and break order are unchanged. An entry that performs no upload still increments only the dequeued counter and does not consume the successful-upload budget. Direct binding still checks uploads normally, independently of the global frame budget.

No queue entry is removed merely because its local pending count is zero, its upload was performed directly, or its texture was disposed. Membership is removed only alongside physical dequeue. The unused concrete `IsQueuedForUpload` properties remain unused.

## Expected effect and limitations

Duplicate lookup changes from a linear queue scan to expected constant-time hash membership. Queue and set growth can still allocate or resize, and producers now serialise on a short global lock. This targets scheduling during loading and atlas expansion, not GPU texture bandwidth or settled text rendering. The previous empty local queue optimisation addresses the repeated stable glyph-binding path separately.

No runtime speedup, contention improvement, visual equivalence or concurrency stress result has been measured. Compile success alone establishes none of those properties.

## Client build

Built from `../g0v0` against the existing local source dependencies:

```text
dotnet build osu.Desktop/osu.Desktop.csproj -c Debug -warnaserror -p:G0V0SourceBuild=true
```

Result: **0 warnings, 0 errors**, 20.93 seconds.


Only source inspection and client compilation were performed. No tests, application launches, shader probes, benchmarks or additional analysis tools were run. Earlier static slider meshes, replay-seeking synchronisation, text caching, whole-quad submission and empty-upload optimisation remain in place. No client implementation or project-reference files were changed for this batch.
