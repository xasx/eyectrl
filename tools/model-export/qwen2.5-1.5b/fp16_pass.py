"""Deterministic fp16 pass + tie-dedupe for the fresh Qwen2.5-1.5B export.

Design copied from the original onnx-community q4f16 artifact:
  - graph IO stays fp32/int64 (past KV in fp32, ids int64)
  - boundary casts inserted right after fp32 inputs / before fp32 outputs
    (named InsertedPrecisionFreeCast_*, matching the original)
  - EVERYTHING internal is fp16: initializers, Constant attrs, Cast targets
  - tied embed/lm_head deduplicated (single fp16 initializer, like the original)
  - lm_head excluded from 4-bit quantization (original: 196 MatMulNBits = 28 layers x 7)
"""
import sys, os, hashlib
import numpy as np
import onnx
from onnx import numpy_helper, TensorProto, helper

OUT = os.path.expanduser("~/qwen-reexport/out")
SRC = sys.argv[1] if len(sys.argv) > 1 else os.path.join(OUT, "model_si.onnx")
FP16 = sys.argv[2] if len(sys.argv) > 2 else os.path.join(OUT, "model_fp16b.onnx")

F, F16 = TensorProto.FLOAT, TensorProto.FLOAT16
FLOAT_DTYPES = {np.dtype(np.float32)}

m = onnx.load(SRC)
onnx.external_data_helper.convert_model_from_external_data(m)
g = m.graph
assert not [n for n in g.node if n.op_type in ("If", "Loop", "Scan")], "subgraphs not handled"
print(f"loaded: {len(g.node)} nodes, {len(g.initializer)} initializers", flush=True)

# ---- 1. dedupe tied embeddings -------------------------------------------------
inits = {i.name: i for i in g.initializer}
emb = "model.embed_tokens.weight"
lm = "lm_head.weight"
if emb in inits and lm in inits:
    a, b = numpy_helper.to_array(inits[emb]), numpy_helper.to_array(inits[lm])
    if a.shape == b.shape and a.dtype == b.dtype:
        h1 = hashlib.sha256(a.tobytes()).hexdigest()
        h2 = hashlib.sha256(b.tobytes()).hexdigest()
        if h1 == h2:
            # point every lm_head.weight consumer at the embed initializer, drop the copy
            for n in g.node:
                n.input[:] = [emb if x == lm else x for x in n.input]
            g.initializer.remove(inits[lm])
            print(f"deduped tied weights: {lm} -> {emb} (sha256 {h1[:12]})", flush=True)
        else:
            print(f"weights differ ({h1[:12]} vs {h2[:12]}) - keeping both", flush=True)

# ---- 2. convert float initializers to fp16 -------------------------------------
new_inits = []
for t in g.initializer:
    arr = numpy_helper.to_array(t)
    if arr.dtype in FLOAT_DTYPES:
        new_inits.append(numpy_helper.from_array(arr.astype(np.float16), t.name))
    else:
        new_inits.append(t)
del g.initializer[:]
g.initializer.extend(new_inits)
print("initializers converted", flush=True)

# ---- 3. tensor-valued node attributes + Cast targets ---------------------------
for n in g.node:
    for a in n.attribute:
        if a.type == onnx.AttributeProto.TENSOR:
            t = helper.get_attribute_value(a)
            if t.data_type == F:
                arr = numpy_helper.to_array(t)
                a.t.CopyFrom(numpy_helper.from_array(arr.astype(np.float16), t.name))
    if n.op_type == "Cast":
        to = [a for a in n.attribute if a.name == "to"]
        if to and to[0].i == F:
            to[0].i = F16
print("tensor attributes + casts converted", flush=True)

# ---- 4. boundary casts around fp32 graph IO ------------------------------------
fp32_in = [i for i in g.input if i.type.tensor_type.elem_type == F]
fp32_out = [o for o in g.output if o.type.tensor_type.elem_type == F]
cast_nodes = []
for gin in fp32_in:
    new_edge = f"InsertedPrecisionFreeCast_{gin.name}"
    for n in g.node:
        n.input[:] = [new_edge if x == gin.name else x for x in n.input]
    cast_nodes.append(helper.make_node("Cast", [gin.name], [new_edge],
                                        to=F16, name=f"InsertedPrecisionFreeCast_{gin.name}"))
for gout in fp32_out:
    tmp = f"{gout.name}__internal_fp16"
    producer = next(n for n in g.node if gout.name in n.output)
    producer.output[:] = [tmp if x == gout.name else x for x in producer.output]
    # present.*/logits edges are ALSO consumed inside the graph (attention shape
    # logic reads present.*) - those consumers must see the fp16 internal edge,
    # not the fp32 boundary cast output.
    for n in g.node:
        if n is producer:
            continue
        n.input[:] = [tmp if x == gout.name else x for x in n.input]
    cast_nodes.append(helper.make_node("Cast", [tmp], [gout.name],
                                        to=F, name=f"InsertedPrecisionCastOut_{gout.name}"))
g.node.extend(cast_nodes)
print(f"boundary casts: {len(fp32_in)} in, {len(fp32_out)} out", flush=True)

# ---- 4b. re-toposort (boundary casts must precede their consumers) --------------
from collections import defaultdict, deque
srcs = {i.name for i in g.input} | {t.name for t in g.initializer}
nodes = list(g.node)
consumers = defaultdict(list)
indeg = [0] * len(nodes)
for idx, n in enumerate(nodes):
    deps = [x for x in n.input if x and x not in srcs]
    for x in deps:
        consumers[x].append(idx)
    indeg[idx] = len(deps)
q = deque(i for i, d in enumerate(indeg) if d == 0)
order = []
while q:
    idx = q.popleft()
    order.append(idx)
    for o in nodes[idx].output:
        for c in consumers.get(o, ()):
            indeg[c] -= 1
            if indeg[c] == 0:
                q.append(c)
assert len(order) == len(nodes), f"topo sort incomplete: {len(order)}/{len(nodes)}"
del g.node[:]
g.node.extend(nodes[i] for i in order)
print(f"toposorted {len(order)} nodes", flush=True)

# ---- 5. verify: no FLOAT initializer/attr/cast-target left ---------------------
leftovers = [t.name for t in g.initializer if t.data_type == F]
leftovers += [n.name or str(i) for n in g.node if n.op_type == "Cast"
              and not (n.name or "").startswith("InsertedPrecisionCastOut_")
              and any(a.name == "to" and a.i == F for a in n.attribute)]
assert not leftovers, f"unconverted float leftovers: {leftovers[:5]}"
del g.value_info[:]
print("verification: no float leftovers", flush=True)

onnx.save_model(m, FP16, save_as_external_data=True,
                all_tensors_to_one_file=True, location="model_fp16b.onnx_data",
                size_threshold=0)
print("saved", FP16, flush=True)

# ---- 6. checker gate (path form, external-data aware) ---------------------------
onnx.checker.check_model(FP16, full_check=True)
print("CHECKER OK", flush=True)
