# Descriptor reuse experiment (not retained)

Tried preserving descriptor bindings when switching between pipelines whose resource layout objects were identical at every set (no push constants). Both selected validation runs stopped after seven scenes at DrawNodeDisposal's `all drawable references lost` assertion timeout.

The experiment was reverted with targeted edits. Existing pipeline cache and earlier fixes remain intact. Framework.Tests was rebuilt after reverting; the game binary was not rebuilt with the experimental descriptor code.

Control runs without the change reproduced the same failure and four validation diagnostics:

- SYNC-HAZARD-WRITE-AFTER-PRESENT
- UNASSIGNED-non-acquired-swapchain-image-used
- VUID-vkQueueSubmit-pSignalSemaphores-00067
- VUID-VkPresentInfoKHR-pImageIndices-01430

Therefore causality cannot be attributed to descriptor reuse. Current results expose unresolved lifecycle/presentation issues (or environmental/test interactions requiring isolation); previous successful runs do not establish universal correctness. No new performance improvement is delivered in this pass.

Logs under `%TEMP%\g0v0-vulkan-validation\`:

- Experiment: veldrid-20261009-000452, deferred-20261009-000527
- Reverted baseline: veldrid-20261009-000727, deferred-20261009-000802

Each run completed seven scenes before failure; exit code 0 resulted from the launcher closing the failed test and is not a pass. Next step is to isolate DrawNodeDisposal and swapchain acquisition during window/renderer lifecycle changes, then resume optimisation after a stable baseline.
