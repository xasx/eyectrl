# Language-model release: the fused Qwen2.5-1.5B on ONNX Runtime 1.24.4

- **Word suggestions from the 1.5B model work now.** EyeCtrl's 1.5B model is a fused
  re-export of Qwen2.5-1.5B-Instruct (`model_q4f16_fused.onnx`, sha256 `61e17fef…`), hosted
  as a single file, and ONNX Runtime moves 1.22.1 → **1.24.4**. On a PC that already had the
  fused re-export installed (2026-09-07), suggestions were off because that file did not
  match the manifest's entry (the older published original), so the load-time integrity
  check refused it at every start; the manifest now names the fused file. Separately, ONNX
  Runtime 1.23+ cannot load the ORIGINAL file on the CPU path (its graph optimizer fails on
  it), which is why the runtime bump needed the new artifact. Installs still holding the old
  file show the Models page's re-download (about 1.6 GB, on a dwell, never automatic at
  startup); the owner's PC already has the fused file, so it loads with no download. The
  re-export pipeline — the optimum export, the offline fusion and the quantization — is
  committed under `tools/model-export/qwen2.5-1.5b/`.
