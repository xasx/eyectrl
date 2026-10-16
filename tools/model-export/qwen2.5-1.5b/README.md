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

All paths below are relative to a scratch directory (the recorded session used
`~/qwen-reexport/`, which is also the hard-coded default `out/` in the scripts — they contain
no machine-specific absolute paths).

**1. Export** (optimum 1.27 + torch 2.7.1 CPU) — writes the fp32 model, opset 14:

```bash
venv/Scripts/optimum-cli export onnx --model Qwen/Qwen2.5-1.5B-Instruct \
    --task text-generation-with-past --opset 14 out
```

→ `out/model.onnx` + `out/model.onnx_data` (7,108,352,000 bytes). optimum validates the
export against PyTorch (recorded max deviation ≈ 1e-4 on the logits and present.* outputs).

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
tensor-valued attribute and Cast target becomes fp16; tied `embed_tokens`/`lm_head` weights
are deduplicated by value; the graph is re-toposorted and the ONNX checker gates the result.
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

(The ONNX checker cannot validate `com.microsoft` contrib ops at opset 14 — ONNX Runtime
itself was the gate for this artifact, and it loads on 1.21 / 1.22.1 / 1.24.4 on CPU and
DirectML.)

## Published artifact

The file is published on Hugging Face per
[the release plan](../../../docs/plans/2026-09-07-dependency-update-strategy.md) (one file,
`onnx/model_q4f16_fused.onnx`, under a `resolve/<commit>/` URL so the manifest's
immutable-revision pin holds), and `src/EyeCtrl.App/Resources/models.json` names it with the
hash above.
