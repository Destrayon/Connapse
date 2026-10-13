# Layout-model memory levers, measured on Linux (#681)

Neither lever shrinks a layout parse enough to matter: a 640 input breaks detection, and a backbone-only int8 model saves about 33 MB. The useful result is the Linux baseline, which is higher than the Windows figure #677 was sized on. That points to the shared inference process (#680).

## Method
- **The probe.**
  - It runs the real parser host (`ParserProcessPool` with `PdfParser`, `PdfTextMode=Layout`) in the `dotnet/sdk:10.0` image on WSL2's Linux kernel.
  - Each file gets a fresh host.
  - Peak resident memory is the host's `VmHWM` from `/proc`, which is what the watchdog compares against (`WorkingSet64`).
  - Results are the mean of 3 runs unless stated.
- **The PDFs.**
  - JobBenefits2024: 5 text pages, from the #676 report.
  - fr-2023-28800: Federal Register, multi-column.
  - One olmOCR-bench table page.
  - jfk-104-10130-10215: a scan, so OCR then layout.
  - census-p60-279: a long, table-heavy report.
- **Detection agreement.** A variant's regions are compared to the shipped fp32 model's on the same rendered page. A region matches if it has the same label and IoU > 0.7, using the 0.5 score threshold.

## Linux baseline

| PDF | Peak MB (range) | Parse time |
|---|---|---|
| JobBenefits2024 | 856 (842–880) | 11 s |
| fr-2023-28800 | 831 (784–883) | 9 s |
| olmOCR table page | 904 (887–920) | 6 s |
| jfk scan | 919 (907–940) | 17 s |
| census-p60-279 | 1,043 (1,032–1,058) | 132 s |

- **Higher than Windows.** These are 180–400 MB above the ~650 MB measured on Windows in the #676 research. Resident memory on Linux also counts the runtime and the native libraries mapped into the process.
- **The model is most of it.** Without the layout model the same host peaks at 123–410 MB; the high end is the OCR'd scan.
- **The sizing gap.** `ParserProcessPool.LayoutHostMb` is 900, and at the 3 GB compose default each host gets 1,024 MB. Table-heavy PDFs like census-p60 exceed that. They run out of memory and are re-read without the layout model (#677), so they index, but with less structure.
- **Allocator and graph settings.**
  - `MALLOC_ARENA_MAX=2` made no consistent difference: −45 to +71 MB in single runs.
  - Graph optimization off saved about 50 MB, but made census 24% slower (single run).

## 640×640 input: rejected
- **How it was built.** The ONNX export bakes 800 in throughout:
  - the input shape and declared intermediate shapes;
  - the integer feature-map sizes for strides 4, 8, 16 and 32 (200, 100, 50, 25), and the anchor counts (10,000 + 2,500 + 625 = 13,125);
  - the 25×25 sine/cosine positional embedding;
  - the anchor validity mask;
  - the 200×200 mask-head grid.

  The rebuild regenerates every one. The positional-embedding and anchor-mask formulas reproduce the 800 originals exactly. An 800 → 800 rebuild gives identical detections, so the conversion is sound.
- **What it detects.** At 640 the model misses 39 of 103 text regions, 15 of 32 paragraph titles, and all 4 footers.
- **Why.** The 800 model on the same pages, downscaled to 640 and back, misses only 3 of 103. So the loss isn't image detail: the model doesn't transfer to a smaller grid without retraining.

## Backbone-only int8: rejected
- **The model.** The 82 backbone convolutions (before the encoder's input projection) are quantized to int8: QDQ, per-channel weights, uint8 activations, min-max calibration on 32 pages that are not in the test set. The encoder, decoder and heads stay fp32. The file goes from 130 MB to 89 MB.
- **Detections.** It's close to fp32: on 36 held-out pages it misses 7 of 558 regions and adds 14, with headers, footers and tables unchanged. It's about 7% faster.
- **Memory.** Linux peak fell 33 MB on average: 856→794, 904→815, 919→884 and 1,043→1,008, but 831→890 on fr-2023. Activation memory sits in the fp32 transformer, so quantizing the backbone mostly saves weights.
- **Verdict.** Below the 100 MB bar the issue set, so it isn't worth an extract-eval run or the extra model.

## What follows
- **#680.** Shrinking the model doesn't change how many layout parses fit. A shared inference process holds one copy of the models and pays the activation peak once, and it removes the per-host 900–1,060 MB need altogether.
- **Probe gotcha.** When repeating this probe, put candidate models under the app's own folder. The parser sandbox (Landlock) refuses reads elsewhere, and the layout pass then silently falls back to content order. A run that reports a parse warning is not a layout measurement.
