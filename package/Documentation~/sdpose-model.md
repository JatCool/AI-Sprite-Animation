# SDPose model (required for AI Pose + Rig)

The model is **not** part of this package or repository. It is downloaded once into your ComfyUI install (the window has an *Install / Download Model* button that does exactly what is documented here; nothing is downloaded silently).

| | |
|---|---|
| Model | **SDPose-Wholebody**, ComfyUI repackaging `sdpose_wholebody_fp16.safetensors` (all-in-one checkpoint: SD 2.x U-Net with the pose heatmap head, text encoder, VAE) |
| What it is | A pose *estimator*: image in, 133 whole-body keypoints out (the first 18 are OpenPose body-18 keypoints, which is what the package uses). It does not generate motion by itself: AI Pose + Rig feeds it frames of a generated video of a generic walking/running/... person |
| Source | <https://huggingface.co/Comfy-Org/SDPose> (file `checkpoints/sdpose_wholebody_fp16.safetensors`); direct URL `https://huggingface.co/Comfy-Org/SDPose/resolve/main/checkpoints/sdpose_wholebody_fp16.safetensors` |
| Original work | `teemosliang/SDPose-Wholebody` (<https://huggingface.co/teemosliang/SDPose-Wholebody>), paper arXiv:2503.07740 |
| Size | **1,916,645,792 bytes** (1.92 GB) |
| SHA-256 | `63d01f9a7494560693b24767f4469d59c9d3266b31ff0a253e74d1e611442721` (the installer verifies it) |
| Location | `<ComfyUI>/models/checkpoints/sdpose_wholebody_fp16.safetensors` (portable build: `C:\AI\ComfyUI\ComfyUI\models\checkpoints\`) |
| License | MIT (model card of `teemosliang/SDPose-Wholebody` and of `Comfy-Org/SDPose`). The ComfyUI model index lists Apache-2.0; both are permissive. Trained on COCO-2017, U-Net weights from Stable Diffusion v2 (CreativeML Open RAIL++-M applies to those base weights) |
| Not needed | `rt_detr_v4-x-hgnet_*.safetensors` (person detector for multi-person images; the package renders a single person, so the whole image is used) |
| Node | ComfyUI core node `SDPoseKeypointExtractor` (ComfyUI 0.38 has it built in; no custom node pack) |
| Credentials | None. The file is public; no token is stored or sent |

Manual install instead of the button: download the file from the URL above into `models/checkpoints`, then restart ComfyUI (or let the package start it).
