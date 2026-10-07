# Making document parsing adapt to the memory it has
**Date:** 2026-10-07
**Status:** Reviewed
**Built on:** no prior corpus material (issue #676)

## Executive summary
Connapse's PDF parsing fails with `parse_out_of_memory` on the default Docker deployment because each sandboxed parser host loads the PP-DocLayout v3 model and runs it per page.

Measured on a 5-page text PDF, the cost is:
- about **390 MB of transient native memory per page** for inference activations at 800×800;
- about **165 MB** of resident weights;
- a peak of about 650 MB, against about 210 MB without layout.

Session options barely move this:
- Disabling graph optimisation saves about 30 MB at peak and removes a 90 MB spike during loading.
- Turning prepacking off and changing the thread count change nothing.

No surveyed product adapts to memory: Docling, Unstructured, Marker, MinerU, PaddleX, Tika, Azure and LlamaParse all publish fixed minimums of 2–16 GB.

**Recommended fix, in three parts:**
1. Pick the richest parsing mode that fits the memory actually available, and fall back to a cheaper mode instead of failing.
2. Size the pool from the container's real limit and the measured need of each mode.
3. Later, move layout, table and OCR inference into one shared sandboxed inference process.

Shrinking the activations needs a lower input size or partial quantisation, and both must pass the extract eval.

## Where the memory goes (measured)
This section reports a probe on Windows that matches the parser host: ONNX Runtime 1.29, the same `SessionOptions` as `PdfLayout`, 1 intra-op thread, and `JobBenefits2024.pdf` (5 text pages).

| Stage | Private MB | Peak working set MB |
|---|---|---|
| Idle host | 10 | 29 |
| Page rendered (PDFium + bitmap + tensor) | +16 native, +8 managed | 55 |
| Layout session loaded | 222 (load spike 310) | 351 |
| Layout inference, per page | **612–614** | **647–650** |
| SLANet+ loaded (tables) | about +30 | — |

- The managed heap never exceeds 40 MB; over 90% of the memory is native ONNX Runtime memory.
- That makes `DOTNET_GCHeapHardLimit` irrelevant to this failure. Only the process watchdog sees it.

| Variant | Inference peak (MB) | Effect |
|---|---|---|
| Arena and memory pattern off (current) | 612 | baseline |
| Graph optimisation `DISABLE_ALL` | 583 | −30 MB peak, −90 MB load spike, about 15% slower |
| `session.disable_prepacking` | 611 | no change |
| 4 intra-op threads | 612 | same memory, **3–5× faster** |
| Arena on | 751, retained | worse |
| Arena + memory pattern | 1,282, retained | much worse |

**Caveat:** this was measured on Windows. On Linux, glibc may keep freed activation memory resident between pages; `MALLOC_ARENA_MAX` or trimming should be measured inside the container.

## How production document parsers handle memory
None of the systems surveyed adapts its pipeline to available memory. All publish fixed minimums:

| System | Published minimum |
|---|---|
| docling-serve | 2.3–3 GB |
| MinerU | 2 GB basic, 8 GB standard |
| Azure Layout container | 16 GB |
| LlamaParse | 2–16 GB per worker |

The patterns that recur:
1. **Models in one shared process, thin workers:** Marker (one inference server shared by its conversion workers), PaddleX on Triton, LlamaParse (separate OCR service), and Docling (threads sharing models).
2. **Strategy chosen by need:** Unstructured's `auto` uses the model-free path for text PDFs. Its fallback triggers on a missing dependency, not on memory.
3. **Admission by free memory:** unstructured-api returns 503 below `UNSTRUCTURED_MEMORY_FREE_MINIMUM_MB`.
4. **Bounded batches, pages and render resolution.**
5. **Restart a crashed child:** Tika's `spawnChild`. This contains the failure but still fails the document.

Sources:
- Unstructured `strategies.py`;
- the unstructured-api README;
- docling-serve `configuration.md` and issue #234;
- the Marker README;
- the MinerU README;
- the PaddleX serving docs;
- the Tika 2.x wiki;
- Azure Document Intelligence container requirements;
- LlamaParse self-hosting tuning.

## Levers for the model itself
This section covers what can shrink the layout model, with sources and their tiers.

- **Smaller PP-DocLayout models.** M is 22.6 MB and S is 4.8 MB, but they lose 15–20 mAP points against L, a clear regression ([PaddleX layout docs](https://paddlepaddle.github.io/PaddleX/latest/en/module_usage/tutorials/ocr_modules/layout_detection.html), primary).
- **Quantisation.**
  - Naive int8 breaks RT-DETR-style detectors (onnxruntime #19437, secondary).
  - Backbone-only QDQ per-channel int8, with the decoder and heads kept fp32, retained about 0.99 recall and precision on a similar layout model (docling-layout-heron-int8, community).
  - It saves weights, and probably some activation memory. It must pass olmOCR-bench (extract-v1).
- **fp16 on x86 CPU.** No benefit: kernels fall back to fp32 and weights are upcast.
- **Input resolution.** 640×640 instead of 800×800 cuts activations to about 64%. The model was trained at 800, so small regions are at risk; this is an extract-eval question.
- **Sharing weights across processes.** Aligned external data or `.ort` files, memory-mapped with prepacking off, would save at most about 165 MB per extra host. Activations dominate and can't be shared, so this helps only with several hosts and is less valuable than a shared inference process.

## Architecture options
Options ranked for Connapse, which uses sandboxed multi-process parsing, must need zero configuration, and should never fail a document when a cheaper path exists.

1. **Mode ladder by available memory, with a fallback.**
   - The ladder: full (layout + tables + OCR as needed), then layout without tables, then Raw text extraction (plus OCR only for pages with no text layer).
   - **Before parsing:** pick the richest rung whose measured need fits the host's budget.
   - **After an OOM:** treat it (watchdog kill, `OutOfMemoryException`) as a signal to retry once, one rung down.
   - **Recording:** store the mode used and why in document metadata, show a warning in the UI, and log the budget once.
   - **Effort:** low. **Value:** removes the failure on every deployment size.
2. **Pool sized from the real limit.**
   - Read cgroup v2 `memory.max` and `memory.current`, falling back to `MemAvailable` or `GlobalMemoryStatusEx`.
   - Today's formula undercounts: half of GC's available memory, which is itself about 75% of the container, is about 37%.
   - Give each host what its mode needs (about 700 MB for Layout, measured). When the budget fits one host, run one host, never several below the need.
   - **Effort:** low.
3. **One shared sandboxed inference process.**
   - It owns layout, table and OCR models, behind one queue at batch size 1. Parser hosts render pages and send only fixed-size tensors.
   - Models and activations are paid once, and parser hosts stay near 200 MB.
   - The sandbox keeps read-only model files and no network; the only residual risk is ONNX kernels running on crafted pixels.
   - Precedents: Marker, ChromeOS ML Service, LlamaParse.
   - **Effort:** medium (protocol, supervisor, fallback when it's down).
4. **Activation reduction** (640 input, backbone int8): only with extract-eval evidence.
5. **Bigger fixed defaults** (status quo plus a larger compose limit): what every surveyed product does. It doesn't meet the zero-configuration goal alone, but it is reasonable as a better default alongside parts 1 and 2.

**A floor no design removes.** Layout inference needs about 650 MB at peak in some process. A 1 GB container also has to hold the web app (about 300 MB) and a parser, so it cannot run layout at all. There, the right behaviour is Raw mode with a visible note, not a failure.

## Recommendation
- **Now (#676):**
  - parts 1 and 2: mode ladder with OOM fallback, cgroup-based budget, per-mode host sizing, recorded in metadata;
  - `DISABLE_ALL` graph optimisation for the layout session (removes the 90 MB load spike);
  - 4 intra-op threads where cores allow (3–5× faster at the same memory);
  - raise the compose `web` limit to 2–3 GB so the default deployment runs layout.
- **Next:** part 3, the shared inference process.
- **Then:** measure 640-input and backbone-int8 layout on extract-v1, and adopt only what holds accuracy.

## Conflicts and uncertainties
- **Linux heap retention** after inference is unmeasured. Measure resident memory inside the container before fixing the per-mode needs.
- **mmap savings across processes** under cgroups are unverified (sources differ on how shared file pages are charged).
- **The int8 evidence** comes from a community model card for a related model, not PP-DocLayout v3.
- **The web app's own memory** (assumed about 300 MB) needs measuring at idle and under load.

## Gaps
- No published product adapts its pipeline to memory, so there is no direct precedent for the ladder.
- No published activation profile for PP-DocLayout v3.
- Table and OCR inference peaks weren't measured; only SLANet+'s load cost (about 30 MB) was.
