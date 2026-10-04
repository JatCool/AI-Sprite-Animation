# Licenses: the code vs. the models

**The code of this repository** (the Unity package, its workflows, documentation, the development tools and the Claude Code skill template) is open source under the
**MIT License** ([`LICENSE`](../LICENSE); the package folder carries the same text in [`package/LICENSE.md`](../package/LICENSE.md) for Unity Package Manager).
You may use, copy, modify, merge, publish, distribute, sublicense and sell it, also commercially; keep the copyright and license notice.

**The MIT License does not cover models, ComfyUI, node packs or other third-party software.** This repository contains no model weights. The default **Rig** method
needs none of them. Only the optional *AI Pose + Rig* and *AI Redraw* methods use the models below, which you install separately into your own ComfyUI.
Each model has its own license that governs the model and, depending on its terms, how you may use what it produces. Check the license of every model you use,
especially for commercial projects. Licenses can change; the model page is the reference.

## Models (installed separately into ComfyUI, never committed)

License strings as shown by the Hugging Face model pages at the time of writing.

| Model | Used by | License | Source |
|---|---|---|---|
| SDPose-Wholebody (`sdpose_wholebody_fp16.safetensors`, 1.92 GB) | AI Pose + Rig (pose estimation; produces no images) | MIT. Its U-Net derives from Stable Diffusion 2.x weights (CreativeML Open RAIL++-M applies to those base weights) | [Comfy-Org/SDPose](https://huggingface.co/Comfy-Org/SDPose); details in [`sdpose-model.md`](../package/Documentation~/sdpose-model.md) |
| Stable Diffusion 1.5 (`v1-5-pruned-emaonly.safetensors`) | AI Pose + Rig (motion video), AI Redraw | CreativeML Open RAIL-M (use-based restrictions) | [stable-diffusion-v1-5](https://huggingface.co/stable-diffusion-v1-5/stable-diffusion-v1-5) |
| AnimateDiff motion module v3 (`v3_sd15_mm.ckpt`) | AI Pose + Rig, AI Redraw | Apache-2.0 | [guoyww/animatediff](https://huggingface.co/guoyww/animatediff) |
| IP-Adapter Plus SD1.5 and CLIP-ViT-H image encoder | AI Redraw only | Apache-2.0 | [h94/IP-Adapter](https://huggingface.co/h94/IP-Adapter) |
| ControlNet v1.1 tile and openpose | AI Redraw only | OpenRAIL (use-based restrictions) | [lllyasviel/ControlNet-v1-1](https://huggingface.co/lllyasviel/ControlNet-v1-1) |

## Software the optional methods talk to

| Software | License | How it is used |
|---|---|---|
| [ComfyUI](https://github.com/Comfy-Org/ComfyUI) | GPL-3.0 | a separate program that the package starts and talks to over its HTTP API; it is not distributed with, or linked into, this package |
| [ComfyUI-AnimateDiff-Evolved](https://github.com/Kosinkadink/ComfyUI-AnimateDiff-Evolved) | Apache-2.0 | ComfyUI node pack you install yourself |
| [comfyui-ipadapter](https://github.com/comfyorg/comfyui-ipadapter) (fork of [ComfyUI_IPAdapter_plus](https://github.com/cubiq/ComfyUI_IPAdapter_plus)) | GPL-3.0 | ComfyUI node pack you install yourself (AI Redraw only) |

## Your sprites and generated animations

The default Rig method moves **your own pixels**; the output is your art, rearranged, and carries your art's license. The sample sprite shown in the documentation is a
sprite from the author's own game, included only to illustrate results. For the AI methods, the *pose data* is produced by the models above; the pixels are still yours.
Whether AI Redraw output (which does redraw pixels) can be used commercially depends on the licenses of the models you used.

## Contributions

Contributions are accepted under the same MIT License; see [`CONTRIBUTING.md`](../CONTRIBUTING.md).
