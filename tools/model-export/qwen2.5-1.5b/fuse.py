import os
from collections import Counter
from onnxruntime.transformers import optimizer

OUT = os.path.expanduser("~/qwen-reexport/out")
om = optimizer.optimize_model(
    os.path.join(OUT, "model_si.onnx"), model_type="gpt2",
    num_heads=12, hidden_size=1536, verbose=False)
fused = os.path.join(OUT, "model_fused.onnx")
om.save_model_to_file(fused, use_external_data_format=True)
m = om.model
c = Counter(n.op_type for n in m.graph.node)
interesting = {k: v for k, v in c.items() if "LayerNorm" in k or "Attention" in k or "Gelu" in k or "Add" in k}
print("op counts:", interesting, "| total nodes:", sum(c.values()))
