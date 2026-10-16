# Language-model release: the fused Qwen2.5-1.5B on ONNX Runtime 1.24.4

- **Word suggestions from the 1.5B model work now.** EyeCtrl's 1.5B model is a fused
  re-export of Qwen2.5-1.5B-Instruct (`model_q4f16_fused.onnx`, sha256 `61e17fef…`), hosted
  as a single file, and ONNX Runtime moves 1.22.1 → **1.24.4**. The previously published file
  could not load on 1.23+ — the runtime's graph optimizer failed on it — so the integrity
  check disabled suggestions on every start. Installs still holding the old file show the
  Models page's re-download (about 1.6 GB, on a dwell, never automatic at startup); the
  owner's PC already has the fused file, so it loads with no download. The re-export pipeline
  — the optimum export, the offline fusion and the quantization — is committed under
  `tools/model-export/qwen2.5-1.5b/`.
