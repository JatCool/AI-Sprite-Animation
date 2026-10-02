# Changelog

All notable changes to this package are documented here. The project follows [Semantic Versioning](https://semver.org/):
`MAJOR` = breaking changes (settings/workflow format, API), `MINOR` = backwards-compatible features, `PATCH` = fixes.

## 1.1.0

- **Rig method rebuilt around a real rig.** A `SpriteRigAsset` (`<Sprite>_Rig.asset`) stores 15 joints, the ground line and a per-pixel part map with 14 parts:
  head, hair, body, near/far arm (upper, lower+hand), weapon, near/far leg (upper, lower, foot). Overlapping legs supported.
- **Bone hierarchy** (forward kinematics): rotating an upper arm carries the forearm and weapon; hair swings with lag.
- **Sprite Rig Editor** (Tools > AI Sprite Animation > Sprite Rig Editor): drag joints and the ground line, paint/fill parts, auto-assign, live animation preview, undo.
- **New procedural animations**: walk/run cycles with alternating legs and counter-swinging arms; attack with anticipation, swing, follow-through and recovery; breathing idle.
  Poses are provided through `IRigPoseProvider` (extension point for a future "AI Pose + Rig" mode).
- **Grounding**: the lowest foot pixel lands on the ground line every frame; only intentional hops leave it; translations are pixel-snapped.
- Vacated torso spots (where an arm swung away) are filled with the surrounding body colour.
- Animation Method in the window: *Rig (Recommended for Pixel Art)* / *AI Redraw (Experimental)*. Generating without a rig asks to open the Rig Editor or auto-create one (batch mode auto-creates).
- Removed the old per-preset `Rig Swing`/`Rig Move Arm` and the global body/arm-gain settings (replaced by the rig asset and `Rig Intensity`).

## 1.0.0

First release.

- **Rig mode (default)**: the source sprite's own pixels are cut into parts (head, body, two legs, optional weapon arm) and moved by a procedural skeleton.
  Pixel-exact identity, the sprite's own alpha, no AI model needed, about 1 s per animation. Left-facing sprites supported (exact mirror).
- **AIRedraw mode**: ComfyUI + AnimateDiff v3 + IPAdapter Plus (tight character reference) + Tile ControlNet + OpenPose ControlNet driven by the same skeleton.
  Frames are clipped to the rig's silhouette of the source sprite. Exact frame counts via a dropped tail buffer. Bundled workflow `AnimateDiffSprite` v3.
- `Documentation~/benchmarks.md`: benchmark of IPAdapter strengths, FreeNoise, FreeInit and context windows against the rig on the same sprite and seed.
- Padded working canvas (default 2x the sprite); frames cropped to the union of used pixels with an identical pivot derived from the source pivot; no clipped limbs.
- Pixel-art post-processing: majority-vote downsampling into the source palette, nearest-neighbour only, hard alpha.
- ComfyUI process manager: detects a running ComfyUI (never stops it), otherwise starts the configured one and stops exactly the process tree it started
  (graceful Ctrl+C, then force-kill) on success, failure, timeout, cancel, editor quit and reload; PID/owner file for crash recovery.
- Sprite import with PPU / filter / pivot from the source sprite or from project settings; AnimationClip (loop per preset);
  Animator Controller integration that never overwrites hand-made states.
- Editor window with mode selector, presets, ComfyUI settings, Diagnostics (versions, nodes, models, workflow), Test Connection and Test Full Pipeline. `BatchRunner` for CLI/CI.
- Requires Unity 6000.6 (tested on 6000.6.0f1).
