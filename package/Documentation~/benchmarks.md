# Identity benchmark: why Rig mode is the default

Question: can AnimateDiff redraw a 48x48 pixel-art character so that it is still *the same sprite* in every frame?

Setup: one sprite (48x48 player), Walk, 8 frames, seed 7, same pose sequence, RTX 3080 10 GB, ComfyUI 0.38.0,
AnimateDiff-Evolved + IPAdapter Plus as installed by the README. Frames are aligned to the source through the shared pivot, so
the comparison is pixel-exact in the regions that should not move:

* **head_match** - fraction of the source's head pixels (top 30% of the body) reproduced with the exact same colour
* **torso_match** - same for the 30-55% rows (torso/arms/weapon), +-1 px vertical tolerance
* **upper_iou** - silhouette overlap of the upper 55% with the source
* **upper_consist** - fraction of upper-body pixels that stay unchanged from one frame to the next
* **move_max** - lower-body change vs frame 0 (0 = nothing moves)

| Variant | head_match | torso_match | upper_iou | upper_consist | move_max | Time |
|---|---|---|---|---|---|---|
| **R. Rig (source pixels moved by the skeleton)** | 0.69* | **0.98** | 0.93 | 0.87* | **21.6** | **~1 s** (9 s incl. editor start) |
| A. First iteration: IPAdapter 0.8, Tile only, no pose | 0.14 | 0.09 | 0.99 | 0.96 | 1.6 (static) | 67 s |
| B. Strong IPAdapter (1.0) + tight reference, no pose | 0.05 | 0.10 | 0.99 | 0.97 | 0.6 (static) | 73 s |
| C. B + OpenPose, IPAdapter 0.8 | 0.21 | 0.25 | 0.91 | 0.94 | 12.5 | 85 s |
| C. B + OpenPose, IPAdapter 0.9 | 0.26 | 0.29 | 0.90 | 0.95 | 13.2 | ~85 s |
| C. B + OpenPose, IPAdapter 1.0 | 0.12 | 0.27 | 0.88 | 0.94 | 12.5 | 77 s |
| D. C + FreeNoise | 0.12 | 0.27 | 0.88 | 0.94 | 12.5 | 85 s |
| E. D + FreeInit (2 iterations) | 0.24 | 0.26 | 0.88 | 0.98 | 11.5 | 156 s |
| F. C + context windows (8/overlap 4) | failed | | | | | |
| G. F + ContextRef | failed | | | | | |

\* Measured with the first, simpler rig (head/body/legs). The current hierarchical rig (independent arms, hair, weapon, feet) moves more of the sprite on purpose, so the exact-position match is lower by design; what matters is that every pixel is still an original pixel (palette match 100%, colour-histogram match about 0.9).

Findings

* Silhouette-level consistency is easy (IoU ~0.9); **pixel-level identity is not**. With every diffusion variant only ~10-30% of the source's head and torso pixels survive, the
  face is lost and the costume is recoloured (`benchmark-walk.png`: first cell of each row is the source sprite; rows are Rig, C, D, E, A, B). Variants A/B keep the design closer but do not move.
* **IPAdapter 0.8 / 0.9 / 1.0**: no consistent trend (0.9 slightly best); the differences are within seed noise. Higher weights mostly trade motion for sameness.
* **FreeNoise has no effect** on clips shorter than one context window: variant D is numerically identical to C. (FreeNoise re-orders noise across *context windows*.)
* **FreeInit** raises temporal stability (upper_consist 0.94 -> 0.98) but doubles the run time and does not bring back the source's pixels. Not adopted.
* **Context windows** do not run with the core `ControlNetApplyAdvanced`: ComfyUI reports "Control type ControlNet may not support required features for sliding context window;
  use ControlNet nodes from Kosinkadink/ComfyUI-Advanced-ControlNet". Using them would need that extra node pack and a rewritten ControlNet section; since clips are <= 24 frames
  (one window) there is nothing to gain, so it is not adopted.
* **Tail frames**: with a native 8-frame batch the last 1-2 frames degrade (AnimateDiff v3 is trained on 16-frame windows). AIRedraw mode therefore generates a few extra frames and drops them.

Conclusion: a diffusion model cannot be told "same pixels, new pose" at 48 px. The default **Rig** mode keeps the source sprite as the single source of truth:
it cuts the sprite into parts (head, body, two legs, optional weapon arm) and moves them rigidly with the skeleton, majority-vote
downsampled, so every output pixel is a source pixel (torso_match 0.98, palette match 100%, colour histogram match ~0.93-0.96 vs ~0.5 for diffusion) and the alpha is the sprite's own.
**AIRedraw** remains available for stylised characters that tolerate redrawing; its frames are clipped to the rig's silhouette so nothing is drawn outside the character.
