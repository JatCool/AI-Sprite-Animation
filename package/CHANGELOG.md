# Changelog

All notable changes to this package are documented here. The project follows [Semantic Versioning](https://semver.org/):
`MAJOR` = breaking changes (settings/workflow format, API), `MINOR` = backwards-compatible features, `PATCH` = fixes.

## 1.6.0

- **CrouchWalk preset** (Rig only): the approved Crouch posture (hips near knee height, torso leaning 32 degrees with `Head = -Body` so the face is never re-sampled, arms hanging in front of the knees, sword low) with the legs taking turns: the foot that moves forward lifts (knee folds more), the other stays flat and pushes; 8 frames @ 12 FPS, loop, in place (the game moves the character). Both thighs stay beyond 65 degrees, so the renderer fills the pelvis cells exactly as in Crouch. The hips bob about 4 px per step because the rig pins the lowest foot to the ground line.
  * `RigAnimator` kind `crouchwalk` (a formula like Walk/Run, so any frame count loops), default preset `CrouchWalk` (added to `AddedAfterFirstRelease`), right-click entry *AI > Generate Animation > CrouchWalk*. AI Pose + Rig and AI Redraw refuse the preset with a clear message.
  * `tools/posetests`: CrouchWalk checks (finite poses, in place, `Head + Body == 0`, thighs beyond 65 degrees, the legs swing and alternate, the loop closes, source colours only / alpha 0-255 / no border contact, feet on the ground row within +-1 px, hips stay at least 4 px below standing, no floating pieces, no frame-to-frame jump compared with Walk's about 570 px per step).

- **Crouch preset** (Rig only): a goose-step crouch for slipping through narrow gaps or ducking under something flying at the character: quick dip -> both feet planted and flat, knees tucked forward, the hips at about knee height (not on the heels, not sitting), the torso leaning in about 32 degrees, head forward and low, arms hanging in front of the knees with the sword held low; the last frame is held (6 frames @ 12 FPS, one-shot). No hop and no root movement: the renderer pins the lowest foot to the ground line, so the folded legs lower the hips (about 8 px of 48 on the player, the head also moves forward and down with the lean, so the figure is compact). Meant for games that shrink the collider while the player crouches.
  * **Renderer** (`SpriteRig`): a thigh swung beyond 65 degrees (a deep crouch) leaves the pelvis/skirt area behind the hip empty, which showed as a notch under the leaning torso. In that case the vacated thigh cells are now filled the same way vacated arm cells always were (nearest body colours within 3 cells, `FillVacatedBody`). Below 65 degrees nothing changes, so Walk, Run, Attack and the Jump crouch (58 degrees) render exactly as before.
  * `RigAnimator` kind `crouch` (key times sit on the 6-frame grid), default preset `Crouch` (added to `AddedAfterFirstRelease`), right-click entry *AI > Generate Animation > Crouch*. AI Pose + Rig and AI Redraw refuse the preset with a clear message.
  * `tools/posetests`: Crouch checks (finite poses within +-150 deg, no hop or drift, source colours only / alpha 0-255 / no border contact, feet on the ground row within +-1 px, hips end at least 5 px lower and stay down, the held pose does not pop, torso leans forward a little, **no separate pieces: the silhouette stays one 4-connected piece**).
  * Lessons learned (versions 2-3): (1) the player's cloak tail was mapped to the torso (`Body`), so a forward lean swung it away from the skirt and thighs and left a visible gap under it; a deep knee bend vacates the space behind the thigh in the same way. The player's rig now maps the tail pixels (x 17-19, y 32-38 of `player_east.png`) to `LegNearUpper`, so the cloth follows the thigh. (2) Rotating the head, even by a few degrees, re-samples the face pixels with nearest-neighbour and makes the face look wrong: Crouch leans the torso but sets `Head` to exactly `-Body`, so the head's net rotation is 0 and the face is pixel-exact while it still travels with the lean; the arms (children of the torso) are given as world angle minus the lean (the test enforces `Head + Body == 0`). (3) Check poses for detached pieces and distorted faces by looking at the frames at 9x, not just for clipping.
  * `tools/riglab`: the joints file accepts `remap <FromPart> <ToPart> x0 y0 x1 y1` (pixels of FromPart inside the box become ToPart), to try a rig refinement without the editor; `example_player_joints.txt` carries the tail remap.

- **Sit preset** (Rig only): small dip -> squat -> the character drops onto the ground with the knees up, torso upright and forearms resting on the knees; the last frame is held (8 frames @ 12 FPS, one-shot). No hop and no root movement: the renderer pins the lowest foot to the ground line, so thighs rising forward over shins that hang down lower the hips to the ground (about 8 px of 48 on the player). Version 1 of the pose was a deep squat and looked bad; this is version 2. The game keeps the sit for as long as it wants (a one-shot clip holds its last frame) and returns to Idle to stand up.
  * `RigAnimator` kind `sit` (key times sit on the 8-frame grid), default preset `Sit` (added to `AddedAfterFirstRelease`, so existing settings assets receive it), right-click entry *AI > Generate Animation > Sit*. AI Pose + Rig and AI Redraw refuse the preset with a clear message.
  * `tools/posetests`: Sit checks (finite poses within +-155 deg, no hop or drift, source colours only / alpha 0-255 / no border contact, feet on the ground row within the renderer's +-1 px rounding, hips end at least 6 px lower and keep lowering, the last two frames are close so the held pose does not pop).

## 1.5.0

- **Rotate preset**: the character turns from its side view to face the viewer, **stays in front for 1 second**, then turns to the needed side (18 frames @ 12 FPS, one-shot, ending on the opposite side: the mirrored side view, so a right-facing character ends facing left; the game mirrors the sprite for the other direction).
  Rig method only and pixel-exact: the frames use only the character's own **side sprite** (rig-rendered) and its own **front-view sprite**; the in-between frames are the side/front view squashed horizontally (nearest-neighbour, about 55% width at the half-way point), so no new colour and no partial alpha can appear, the pivot and the feet line never change.
  `TurnThroughFront` (pure math) builds the sequence; `FrontSprite` finds the front sprite: the setting *Front Sprite Path* (Rotate section of the settings), else `<base>_front.png` / `_south.png` / `_down.png` (or `<name>_front.png`) next to the side sprite (for `player_east.png`: `player_front.png`; same canvas size and ground line).
  **The package never invents a front view**: without front art the animation stops with a clear message that names the file to add. AI Pose + Rig and AI Redraw refuse the preset with a clear message.
  * New preset fields `turnThroughFront`, `frontHoldSeconds` (1), `turnSquashFrames` (1); the frame count follows from the hold time (`2 + 2 x squash frames + hold seconds x FPS + 2`). Default preset `Rotate`, right-click entry *AI > Generate Animation > Rotate*.
    Existing settings assets get the `Rotate` preset added (and a Rotate preset from the first 1.5.0 draft, a squash spin without the front view, is upgraded) when they are loaded; other presets are never touched (`AddedAfterFirstRelease` in `AIAnimationSettings`: add the names of future animation types there).
  * Rig output of 1.1.0-1.4.0 is unchanged (byte-identical frames and clips).
  * `tools/posetests`: Rotate checks (18 frames, side first, 12 identical front frames, mirrored last side, own palette only, turning frames narrower, feet on one ground row, longer hold, placement of the front sprite) with the front sprite as fixture `player_front_48.raw`.
- **Jump preset** (Rig only): wind-up -> deep crouch -> launch -> rise -> tucked apex (two frames) -> fall -> landing crouch -> standing again (10 frames @ 12 FPS, one-shot, first and last frame identical). The clip carries **no travel height** (a game moves the character itself): the pose changes, and `hop` (at most 1 px) only keeps the hips at standing height while the legs tuck. The sword is counter-rotated so it keeps pointing forward while the arms go up.
  * `RigAnimator` kind `jump` (key times sit on the 10-frame grid), default preset `Jump` (added to `AddedAfterFirstRelease`, so existing settings assets receive it), right-click entry *AI > Generate Animation > Jump*. AI Pose + Rig and AI Redraw refuse the preset with a clear message (no jump skeleton or motion prompt exists for them).
  * `tools/posetests`: Jump checks (finite poses within +-120 deg, no travel height, same first and last pose, knee lifted and arms up in the air, source colours only / alpha 0-255 / no border contact, feet never below the ground row, both crouches sink the hips by at least 2 px, hips stay near standing height in the air, last frame pixel-identical to the first).

## 1.4.0

- **Constraint stage for AI poses.** Between "AI pose extraction" and "saved pose asset" the poses now go through validation *and* a constraint stage (`PoseConstraints`) that only prevents obviously broken poses; the AI still controls the motion
  and the Rig method's own procedural animations pass the stage unchanged (unit-tested). Same architecture as before: AnimateDiff -> SDPose -> keypoints -> `OpenPoseMapper` -> `AIPoseProvider` -> unchanged Rig -> original pixels.
  * **Constraints**: single-frame spikes (pulled back to an allowed overshoot, predicted from four neighbours so real swing peaks survive), lopsided walk/run stride (only when the legs swing about centre lines more than 8 deg apart), joint speed (spread over
    neighbouring frames, not cut off), leg extension (no leg folded into the body), root movement (sway/lurch/hop speed and range), hip step in a flight phase, foot step (rendered foot displacement per frame, stricter for a planted foot), foot lock (planted sole within 5 deg of the ground), weapon (stays in the hand, tip kept out of the ground).
    Limits are fractions of the rig's leg length / degrees per animation type, scaled by *Limit Scale*; all configurable in *Pose clean-up > Constraints*.
  * **Validation improvements**: angles that wrapped around +-180 deg are normalised (the Idle forearm measured as -226 deg no longer flickers between two clamp limits); angles still far outside the range are interpolated from their neighbours instead of being clamped to the limit;
    frames the estimator got mostly wrong are *rejected* and rebuilt from their neighbours (sequence rejected when more than 25% of the frames are unusable); the step limit spreads excess over neighbouring frames instead of truncating; discarded values are no longer counted twice towards the repair fraction.
  * **Idle prompt and selection**: the original idle prompt produced clips whose keypoint noise was as large as the motion; the new prompt ("... subtly breathing, shoulders gently rising and falling, relaxed idle stance") gives smooth clips, and idle clip selection now rewards visible motion (a perfectly still clip is a frozen idle). Measured: quality 0.27 -> 0.58, 4 candidates -> 1 (186 s -> 49 s), no value to repair (36 of 136 before).
  * **Idle sway**: if the generated idle moves the neck and hands by less than about 1.2 px (invisible on a 48 px sprite) the coherent part of the AI's own sway (slow harmonics, noise removed) is amplified, as little as needed (at most x4); the gain is reported. Measured: the generated idle videos contain no breathing at keypoint resolution (see `ai-pose-rig.md`).
  * **Quantitative validation report** for every animation (`PoseReport`, `PoseMetrics`, `PoseQualityData`): mean AI-vs-procedural difference, maximum joint deviation, maximum foot and root displacement (final and as the AI delivered it), foot slide, leg extension, spike size, weapon gap,
    corrected and rejected frames (count and %), per-correction counts. Stored in the pose asset, shown in the window (*Validation report* foldout), printed by the batch tools.
  * **The pose asset now contains the final poses.** `frames` = final (validated, constrained, smoothed); preview and build use them as they are (no re-cleaning on every build). The AI poses as delivered are kept as `sourceFrames` (was `rawFrames`; `FormerlySerializedAs` keeps old assets working)
    for diagnostics and for the new **Re-apply Constraints** button / `-aiPoseAction reclean` (rig edited, settings changed; no ComfyUI). A build at another frame count or intensity only re-applies the limits (`PoseConstraints.Enforce`).
  * `tools/posetests`: constraint tests (synthetic faults with known answers, procedural pass-through, idempotence, real SDPose clips as fixtures) and an `assess` mode that prints the report and renders procedural / AI-source / final sheets without ComfyUI.
  * Rig and AI Redraw output unchanged (byte-identical to 1.1.0). ComfyUI lifecycle unchanged.

## 1.3.0

- **AI Pose + Rig now produces AI-generated motion: SDPose backend (default).** AnimateDiff makes a short text-to-video clip of a generic person doing the requested animation seen from the side
  (no sprite involved), **SDPose** (ComfyUI core node `SDPoseKeypointExtractor`, checkpoint `sdpose_wholebody_fp16.safetensors`) reads the body pose of every frame, the keypoints are cleaned and retargeted onto the character's own rig,
  the best repeatable stretch is cut out as the animation, several clips (seeds) are tried and the best is kept. The saved pose asset contains that motion; the rig renders it from the original pixels.
  * `IPoseGenerator` with `SDPosePoseGenerator` (AI), `ProceduralPoseGenerator` and `SketchEvidencePoseGenerator` (the 1.2.0 behaviour; both labelled "NOT AI motion"). *Pose Backend* in the window. **No silent fallback**: if SDPose cannot run, the window and batch runner say why and nothing is saved.
  * **SDPose model management**: detection (disk and live), a clear "SDPose model is required for AI Pose + Rig" message, **Install / Download Model** (confirmation, resumable, SHA-256 verified; never silent), diagnostics rows, `Documentation~/sdpose-model.md` (name, URL, size, hash, location, license).
  * `OpenPoseTracking` (facing detection, left/right limb label repair by continuity), `MotionCycleExtractor` (full cycle or mirror-symmetric half cycle for walk/run, closed stretch for idle, most active stretch for attack), `SDPoseAnalysis` (clip quality: detection coverage, side view, smoothness, stride, seam),
    arm choice for the weapon arm and weapon-arm damping (retargeting), kind-specific limb limits for realistic poses.
  * **AI contribution report** (`PoseContribution`): mean difference to the procedural animation of the same type at the best alignment, split into movement and stance, with levels negligible (< 5 deg) / moderate (5-15) / significant (> 15). Saved in the pose asset and shown in the window and the preview.
  * `ComfyUIClient.WaitForResultAsync` (text outputs), `ComfyUIWorkflowRunner`, workflow `SDPoseMotion.json`, `ComfyWorkflowBuilder.Build(..., requireInputImage: false)`.
  * Pose assets record `motionSource`, `isAiMotion`, contribution; imported poses are labelled "Imported". Batch: `-aiPoseBackend`, new `-aiOverride` keys, `preview` prints the AI-contribution table.
  * `tools/posetests` now covers tracking, cycle extraction (synthetic and real SDPose clips), facing/mirroring and the contribution metric. *Test Full Pipeline* tests SDPose when the model is installed and warns (never silently passes) when it is not.
  * Rig and AI Redraw output unchanged (byte-identical to 1.2.0). Replaced the 1.2.0 "Sketch + AnimateDiff evidence" as default: it is kept as a labelled legacy backend because it measurably adds no AI motion.

## 1.2.0

- **AI Pose + Rig (Beta)**, a new optional animation method. The AI only decides *how the character moves*; every pixel still comes from the original sprite through the unchanged Rig renderer.
  Animation Method is now *Rig (Recommended for Pixel Art)* / *AI Pose + Rig (Beta)* / *AI Redraw (Experimental)*; the default stays Rig.
  * **Generate Poses / Preview Poses / Build Animation** in the window. Poses are generated once (ComfyUI is started only for that and stopped afterwards if Unity started it) and saved as
    `<Sprite>_<Animation>_AIPose.asset` (`AIPoseAsset`: raw and cleaned joint rotations per frame, root offset, clean-up settings and report, generation metadata, JSON export/import).
    Rebuilding the animation from saved poses takes under a second and never starts ComfyUI.
  * `AIPoseProvider` implements the existing `IRigPoseProvider`; `ProceduralRigPoses` is untouched. Rig output is byte-identical to 1.1.0.
  * **Pose validation and smoothing** between any pose source and the renderer (`PoseValidator`, `PoseSmoother`, `PoseCleanup`, `PoseCleanupSettings`): non-finite values, impossible joint angles, estimator flips, vertical drift and
    invalid flight phases are repaired; unusable sequences are rejected and nothing is saved; edge-preserving smoothing, loop closing and optional stepped angles, all configurable.
  * **OpenPose mapping layer** (`OpenPoseMapper`, `OpenPoseJson`): OpenPose body-18 keypoints -> rig bone rotations (side view, near/far limbs, mirrored for left-facing sprites), round-trip tested. Pose JSON and OpenPose keypoint JSON can be
    imported (`Import JSON...`, `AIPoseImporter`), so a real motion generator/estimator can replace the bundled backend without touching the rig.
  * Bundled backend: a sketch skeleton built from the rig's proportions guides one ComfyUI/AnimateDiff run; the AI frames are used as evidence only (silhouettes), the rig is fitted to them by analysis-by-synthesis (`RigPoseFitter`,
    parallel, 2-5 s) and blended with the sketch by *AI Pose Evidence Weight*. The report states how much motion the AI really produced. **Honest limitation:** with the installed SD1.5/AnimateDiff stack the AI adds little
    independent motion on a 48 px sprite (measured), so the saved poses are close to the sketch. See README "Where the poses come from".
  * Sprite Rig Editor: **AI poses preview** (original sprite beside the animated rig, play/pause/previous/next, `Frame: n / N`, optional bones) driven by `PosePreviewPlayer`.
  * `RigKinematics` (forward kinematics of the rig without rendering), `PoseSketch`, `AIPoseGenerator`, `BatchPoseTools`.
  * Batch: `-aiMode rig|pose|redraw`, `-aiPoseAction generate|build|both|preview|importjson|openpose`, `-aiPoseDump`, `-aiPoseSheet`, `-aiPoseExportJson`, `-aiPoseFile`, new `-aiOverride` keys.
  * *Test Full Pipeline* now also generates a tiny AI Pose + Rig animation and rebuilds it from the saved poses.
  * `AnimationMode` gained `AIPoseRig = 2` (existing values unchanged, so saved settings keep working); new settings are added with defaults.
- Internal: `SpriteRig.Parent/Pivot/GroundParts/Affine` are now `internal` (no behaviour change); `PoseSequenceGenerator.EncodeSkeleton`; `SpriteFrameProcessor.EncodePng` is internal.
- Repo: `tools/posetests` (console tests of the pose layers).

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
