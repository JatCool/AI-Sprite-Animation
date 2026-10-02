"""Regenerates package/Workflows/AnimateDiffSprite.json's pose-slot section for a given slot count.

The bundled ComfyUI API workflow has MAX_FRAMES LoadImage "pose slots" chained with ImageBatch; Unity fills the slots and an
ImageFromBatch node keeps only the first __FRAME_COUNT__ of them. Run: python tools/build_workflow.py 24
"""
import json, sys, os

slots = int(sys.argv[1]) if len(sys.argv) > 1 else 24
path = os.path.join(os.path.dirname(__file__), "..", "package", "Workflows", "AnimateDiffSprite.json")
wf = json.load(open(path))

# drop old pose loaders / batch nodes
for k in list(wf):
    n = int(k)
    if 100 <= n < 300:
        del wf[k]

for i in range(slots):
    wf[str(100 + i)] = {"class_type": "LoadImage", "inputs": {"image": f"__POSE_{i:02d}__"}, "_meta": {"title": f"Pose {i}"}}
prev = "100"
for i in range(1, slots):
    nid = str(200 + i)
    wf[nid] = {"class_type": "ImageBatch", "inputs": {"image1": [prev, 0], "image2": [str(100 + i), 0]}, "_meta": {"title": f"Pose batch {i}"}}
    prev = nid
wf["45"]["inputs"]["image"] = [prev, 0]
out = {k: wf[k] for k in sorted(wf, key=int)}
json.dump(out, open(path, "w"), indent=1)
print(len(out), "nodes,", slots, "pose slots")
