# Changelog

All notable changes to this package are documented here. The project follows [Semantic Versioning](https://semver.org/):
`MAJOR` = breaking changes (settings/workflow format, API), `MINOR` = backwards-compatible features, `PATCH` = fixes.

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
