# Language-model release: the fused Qwen2.5-1.5B on ONNX Runtime 1.24.4

- **Word suggestions from the 1.5B model work now.** EyeCtrl's 1.5B model is a fused
  re-export of Qwen2.5-1.5B-Instruct (`model_q4f16_fused.onnx`, sha256 `61e17fef…`), hosted
  as a single file, and ONNX Runtime moves 1.22.1 → **1.24.4**. On a PC that already had the
  fused re-export installed (2026-09-07), suggestions were off because that file did not
  match the manifest's entry (the older published original), so the load-time integrity
  check refused it at every start; the manifest now names the fused file. Separately, ONNX
  Runtime 1.23+ cannot load the ORIGINAL file on the CPU path (its graph optimizer fails on
  it), which is why the runtime bump needed the new artifact. Installs still holding the old
  file show the Models page's re-download (1.67 GB, on a dwell, never automatic at startup);
  the owner's PC already has the fused file, so it loads with no download. **Updating an
  existing install: replace the whole publish folder** — `onnxruntime.dll` changes with
  1.24.4 (`DirectML.dll` does not), and an exe-only update would pair the new managed code
  with the old native runtime. The re-export pipeline — the optimum export, the offline
  fusion and the quantization — is committed under `tools/model-export/qwen2.5-1.5b/`.
- **The 0.5B option is removed.** Its model file cannot load on ONNX Runtime 1.24.4's CPU path
  (the same `SimplifiedLayerNormFusion` bug), and the retired option no longer resolves to its
  file at all, so an install that selected 0.5B gets no model suggestions — on the graphics card
  too — until another model is chosen or downloaded on the Models page. If 0.5B was its only
  model, that page says "No models downloaded"; the files stay on disk and can be deleted by
  hand: `%APPDATA%\EyeCtrl\models\qwen2.5-0.5b.onnx` and `qwen2.5-0.5b.tokenizer.json`.
- **The day log now records which provider ran the model.** The predictor's own lines —
  `Using DirectML (GPU) for {Model}`, the silent CPU fallback warning, the
  `SmallLMPredictor initialized with …` line and any load failure — now reach
  `%APPDATA%\EyeCtrl\logs\eyectrl-<date>.log`; before, the predictor was built without a
  logger and all of them were dropped. It also unmutes two lines the note did not name:
  `No tokenizer provided for {Model}; LM suggestions disabled` (Warning, at each keyboard
  stack build while the selected model is not downloaded, beside the existing "Tokenizer
  file not found" line) and `LM inference failed` (Error, with its exception).
