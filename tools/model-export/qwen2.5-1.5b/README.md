# Qwen2.5-1.5B re-export: the fused q4f16 model

The pipeline behind the shipped 1.5B model: **`model_q4f16_fused.onnx`, 1,671,599,992 bytes,
sha256 `61e17fef2f6df1fd112dc42c69777298271c8b5992de21d35a43b4bfef99d688`**.

## Why

ONNX Runtime 1.23+ cannot load the previously published `model_q4f16.onnx`: its graph
optimizer's `SimplifiedLayerNormFusion` pass fails on the decomposed RMSNorm pattern
(`GetIndexFromName ... input_layernorm/Constant_output_0 ... Mul_1/SimplifiedLayerNormFusion/`).
Fusing those patterns **offline** — here, with `onnxruntime.transformers.optimizer` from the
export venv — removes the decomposed shapes the runtime pass chokes on. The result loads on
ONNX Runtime 1.21 / 1.22.1 / 1.24.4 on both CPU and DirectML, which is what let
`Microsoft.ML.OnnxRuntime.DirectML` move to 1.24.4.

## Environment

A Python venv with `requirements.txt` (pins from the export venv's `pip freeze`, 2026-09-07).
torch is the **CPU wheel**:

```bash
python -m venv venv
venv/Scripts/pip install torch==2.7.1 --index-url https://download.pytorch.org/whl/cpu
venv/Scripts/pip install -r requirements.txt
```

optimum stays on the 1.x line (1.27): optimum 2.x is a stub, and newer torch breaks the 1.x
exporter's symbolic hooks (the plan's decision — see
`docs/plans/2026-09-07-dependency-update-strategy.md`).

## Pipeline

**Run every step with `~/qwen-reexport/` as the working directory.** `fuse.py` takes no
arguments and hard-codes that path (`fuse.py:5`); `fp16_pass.py` and `quantize.py` default
there too. The scripts are byte-identical to the recorded run and contain no
machine-specific absolute paths.

**1. Export** (optimum 1.27 + torch 2.7.1 CPU) — writes the fp32 model, opset 14:

```bash
venv/Scripts/optimum-cli export onnx --model Qwen/Qwen2.5-1.5B-Instruct \
    --task text-generation-with-past --opset 14 out
```

→ `out/model.onnx` + `out/model.onnx_data` (7,108,352,000 bytes). optimum's PyTorch
validation **warned rather than passed** — the recorded log: `The ONNX export succeeded
with the warning: The maximum absolute difference … is not within the set tolerance 1e-05`,
with `logits: max diff = 0.0010037` (1.0e-3) and the worst `present.*` at
`present.27.value: 8.4e-4`. That is normal fp32 drift for a 28-layer decoder, not a defect.

**2. Shape-inference shell** — the fusion step's input. This is the phase-1 step of the same
session's `make_q4f16.py`, which is deliberately NOT committed (its converter path was the
dead end that `fp16_pass.py` replaced); reproduced here so the pipeline stays runnable:

```python
import onnx
m = onnx.load("out/model.onnx", load_external_data=False)   # graph shell, weights stay on disk
m = onnx.shape_inference.infer_shapes(m)
onnx.save_model(m, "out/model_si.onnx")                     # ~1.8 MB shell; its initializers
                                                            # still point at out/model.onnx_data
```

**3. Fuse** — `fuse.py` (no arguments):

```bash
venv/Scripts/python fuse.py
```

Reads `out/model_si.onnx` (weights resolved from `out/model.onnx_data`), runs
`onnxruntime.transformers.optimizer.optimize_model` with `model_type="gpt2"`,
`num_heads=12`, `hidden_size=1536`, and writes `out/model_fused.onnx` +
`out/model_fused.onnx.data` (7,108,352,000 bytes). The graph's 6,964 nodes become 3,225:
the decomposed RMSNorm patterns become `SimplifiedLayerNormalization` /
`SkipSimplifiedLayerNormalization` contrib ops.

**4. fp16 pass** — `fp16_pass.py <src> <dst>`; the recorded run:

```bash
venv/Scripts/python fp16_pass.py out/model_fused.onnx out/model_fused_fp16.onnx
```

Graph IO stays fp32/int64 (past KV fp32, ids int64) with `InsertedPrecisionFreeCast_*`
boundary casts, exactly like the original artifact's design; every internal initializer,
tensor-valued attribute and Cast target becomes fp16; the graph is re-toposorted.

**On the fused input the checker then RAISES — expected, and the saved file is the one the
pipeline uses.** Measured: `ValidationError: No Op registered for
SimplifiedLayerNormalization with domain_version of 14`, because the fused graph's 57 norms
are `SimplifiedLayerNormalization` (**default domain**) and `SkipSimplifiedLayerNormalization`
(`com.microsoft`), which the checker's opset-14 registry does not know. The script saves
BEFORE it checks (`fp16_pass.py:134` saves, `:140` checks), so a rebuilder sees a traceback
after a successful save — that saved output is exactly the file step 5 consumes. ONNX Runtime
is the real gate for this artifact. (Only the unfused run's log survives, `fp16.log`, and it
ends `CHECKER OK` because the unfused graph still carries the decomposed patterns.)

**No tied weight was deduplicated for this export, and its root cause is the environment.**
Both this pass and `quantize.py` dedupe only when the two candidates are **byte-identical
initializers** (fp16_pass checks `model.embed_tokens.weight` against an initializer literally
named `lm_head.weight`; quantize matches by value against same-shape fp16 candidates).
The model IS tied (`out/config.json`: `"tie_word_embeddings": true`), but optimum's own
deduplication was **skipped**: the export log says `Weight deduplication check in the ONNX
export requires accelerate`, and `accelerate` is absent from this venv. Separately — a property
of tracing `nn.Linear`, not of the missing package — optimum exports `lm_head`
**pre-transposed under a different initializer name**, and neither custom pass found a tie —
the recorded quantize log says it exactly:
`lm_head weight: onnx::MatMul_9283 | same-shape fp16 candidates: []` →
`no tie found; keeping both copies`. Both copies ship, which is why this artifact is 1.67 GB
against the original's 1.22 GB (≈ one extra 467 MB fp16 copy).

**`accelerate` is deliberately absent.** With it installed, optimum may deduplicate the tied
`lm_head` and the exported bytes change — a rebuild with `accelerate` no longer matches the
expected sha256 below.

The external data is always written beside the output under the **hard-coded name
`model_fp16b.onnx_data`** (≈ 3.55 GB for the fused model) — and successive runs in one
directory accumulate into that file (on the recorded box it reached 17.7 GB after the
pre-fusion runs). Use a clean `out/` per full pipeline run.

**5. Quantize** — `quantize.py <fp16 src> <final single file>`; the recorded run:

```bash
venv/Scripts/python quantize.py out/model_fused_fp16.onnx model_q4f16_fused.onnx
sha256sum model_q4f16_fused.onnx
```

4-bit `MatMulNBits` (bits=4, block_size=32, symmetric) with **`/lm_head/MatMul` excluded**;
the script asserts exactly **196** quantized nodes (28 layers × 7 projections) and saves a
**single-file** model (no external data). Expected result:

| | |
| --- | --- |
| bytes | 1,671,599,992 |
| sha256 | `61e17fef2f6df1fd112dc42c69777298271c8b5992de21d35a43b4bfef99d688` |

(The ONNX checker cannot validate the fused norms' ops at opset 14 — the failing op is the
default-domain `SimplifiedLayerNormalization`, next to `com.microsoft`'s
`SkipSimplifiedLayerNormalization`; ONNX Runtime is the real gate for this artifact, and it
loads on 1.21 / 1.22.1 / 1.24.4 on CPU and DirectML.)

## Published artifact

Published on Hugging Face:

- **Repo:** [`xasx/Qwen2.5-1.5B-Instruct-ONNX-fused`](https://huggingface.co/xasx/Qwen2.5-1.5B-Instruct-ONNX-fused)
- **Commit:** `493920436cb18f4a154480089a3ba27ac9dfd6c0`
- **Path:** `onnx/model_q4f16_fused.onnx` (the `resolve/<commit>/` URL form below)
- The repo also carries the model card (derivation from `Qwen/Qwen2.5-1.5B-Instruct`, the
  pipeline, the sha256 and the runtime matrix) and the upstream **LICENSE (Apache-2.0)**.

`src/EyeCtrl.App/Resources/models.json` names it with the hash above:
`https://huggingface.co/xasx/Qwen2.5-1.5B-Instruct-ONNX-fused/resolve/493920436cb18f4a154480089a3ba27ac9dfd6c0/onnx/model_q4f16_fused.onnx`.
The immutable `resolve/<commit>/` pin is what keeps the manifest's revision-pinning gate
(`ShippedModelManifestTests`) satisfied. Background:
[the release plan](../../../docs/plans/2026-09-07-dependency-update-strategy.md).
