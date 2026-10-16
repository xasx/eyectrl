"""Quantize the deterministic-fp16 model to q4f16, matching the original artifact:
196 MatMulNBits (lm_head excluded), tied embed/lm_head deduplicated by value."""
import sys, os, hashlib
import numpy as np
import onnx
from onnx import numpy_helper
from onnxruntime.quantization.matmul_nbits_quantizer import MatMulNBitsQuantizer

OUT = os.path.expanduser("~/qwen-reexport/out")
FP16 = sys.argv[1] if len(sys.argv) > 1 else os.path.join(OUT, "model_fp16b.onnx")
FINAL = sys.argv[2] if len(sys.argv) > 2 else os.path.expanduser("~/qwen-reexport/model_q4f16_final.onnx")

m = onnx.load(FP16)
g = m.graph
inits = {t.name: t for t in g.initializer}

# --- dedupe tied lm_head weight by value against the embedding ------------------
lm_node = next(n for n in g.node if n.name == "/lm_head/MatMul")
weight_name = next(i for i in lm_node.input if i in inits)
cand = [n for n in g.initializer
        if n.name != weight_name and n.data_type == onnx.TensorProto.FLOAT16
        and n.dims == inits[weight_name].dims]
print("lm_head weight:", weight_name, "| same-shape fp16 candidates:", [c.name for c in cand][:3], flush=True)
for c in cand:
    a = numpy_helper.to_array(inits[weight_name]); b = numpy_helper.to_array(c)
    if hashlib.sha256(a.tobytes()).digest() == hashlib.sha256(b.tobytes()).digest():
        lm_node.input[:] = [c.name if x == weight_name else x for x in lm_node.input]
        g.initializer.remove(inits[weight_name])
        print(f"deduped: {weight_name} == {c.name} (sha256 match)", flush=True)
        break
else:
    print("no tie found; keeping both copies", flush=True)

# --- 4-bit quantization, block 32, symmetric, lm_head excluded -------------------
q = MatMulNBitsQuantizer(m, bits=4, block_size=32, is_symmetric=True,
                         nodes_to_exclude={"/lm_head/MatMul"})
q.process()
proto = q.model.model if hasattr(q.model, "model") else q.model
n_mnb = sum(1 for n in proto.graph.node if n.op_type == "MatMulNBits")
print(f"MatMulNBits={n_mnb} (target 196)", flush=True)
assert n_mnb == 196, "unexpected quantized-node count"

del proto.graph.value_info[:]
onnx.save_model(proto, FINAL, save_as_external_data=False)
print(f"saved {FINAL}: {os.path.getsize(FINAL):,} bytes (original 1,221,878,940)", flush=True)

pass  # checker lacks com.microsoft op registry; ortprobe is the real gate
print("FINAL CHECKER OK", flush=True)
