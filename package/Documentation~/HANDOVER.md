# Handover: AI Sprite Animation (`com.limpo.ai-sprite-animation`)

Read this first if you are taking over the package or the game integration. It explains why the package exists, how it got to its current shape, how it works,
what is verified and what is not, and what you will need to update. User-facing documentation is in `../README.md`; measurements are in `benchmarks.md`.

---

## 1. Context

* **Project:** *The Legacy of Shadows*, a Unity 6 (6000.6.0f1) 2D side-scroller, URP 2D renderer, 16 pixels per unit, Point filtering, new Input System only.
  The player sprite is **48x48 pixel art** (`Assets/Art/Player/player_east.png`) facing right, ponytail, sword held at the hip.
* **Goal:** take one existing sprite and produce animation frames (Idle, Walk, Run, Attack) plus an `AnimationClip`, from inside the Unity editor, automatically,
  without cloud services. The first idea was AI generation with a local ComfyUI (AnimateDiff).
* **Hardware:** Windows 11, RTX 3080 10 GB, 32 GB RAM. (The very first request mentioned an RTX 4070 Super; that was corrected to the 3080.)
* **Where things live**

| What | Where |
|---|---|
| Reusable package + repo | `C:\Users\limpo\AI-Sprite-Animation` (git, tags `v1.0.0`, `v1.1.0`, **no remote yet**); the package is `package/` |
| The game | `C:\Users\limpo\The-legacy-of-shadows`; references the package with `file:../../AI-Sprite-Animation/package` in `Packages/manifest.json` |
| Game-specific files | `Assets/Settings/AISpriteAnimation/AIAnimationSettings.asset` (project settings), `Assets/Art/Player/player_east_Rig.asset` (the player's rig), a section in `CLAUDE.md` |
| ComfyUI (AI Redraw and AI Pose generation; SDPose model in `models/checkpoints`) | `C:\AI\ComfyUI` (portable NVIDIA build v0.38.0; `main.py` is in `C:\AI\ComfyUI\ComfyUI`) |
| Throw-away test projects | `C:\AI\unitytest` (copy of the game), `C:\AI\unitytest2` (empty project with only the package), `C:\AI\riglab` (harness). Safe to delete. |

**Rule for the game:** the game must not contain its own copy of this system. It only supplies sprites, rig assets and settings. Fix or extend the package instead.

---

## 2. History and the decisions that shaped the design

1. **First version** lived inside the game (`Assets/Editor/AIAnimation`), with a ComfyUI workflow written from memory. It compiled but was untested because ComfyUI was not installed.
2. **Real installation and validation.** ComfyUI (portable) was installed, plus `ComfyUI-AnimateDiff-Evolved` and `comfyorg/comfyui-ipadapter` (a fork of cubiq's IPAdapter Plus, about 6 commits behind upstream; it works).
   The workflow was rebuilt against the node definitions actually installed (`/object_info`), not against memory. Models: SD 1.5, `v3_sd15_mm.ckpt`, IP-Adapter Plus SD1.5, CLIP-ViT-H, ControlNet tile and openpose.
3. **The code was extracted into this package** (generic, no game references) and the game now consumes it.
4. **AI Redraw was improved** until it plateaued: padded canvas, a tight IPAdapter reference crop, OpenPose driven by a procedural skeleton (so the skeleton, not a text prompt, controls movement),
   Tile ControlNet kept active for 60% of the steps, a tail buffer (see section 6), palette snapping and a source-derived silhouette mask.
5. **The plateau, measured** (`benchmarks.md`): with every variant tried (IPAdapter weights, FreeNoise, FreeInit, context windows) only 10-30% of the original head/torso pixels survive.
   The silhouette stays but the face and clothes are redrawn in every frame. That is unacceptable for pixel art, where consistency matters more than detail.
6. **Rig became the primary method.** The sprite's own pixels are moved, never redrawn. First a simple 3-part rig (head/body/legs), then, because arms and weapon could not move independently,
   the current rig with a per-pixel part map, a bone hierarchy and an editor (package 1.1.0).
7. **AI Redraw was kept** as "AI Redraw (Experimental)", for stylised sprites where exact pixels do not matter.

The owner's stated priorities, in order: *same character in every frame* > no clipped limbs and a stable pivot > pixel-art cleanliness > speed > AI involvement.
Keep that order when making trade-offs.

---

## 3. What exists now

Three animation methods (setting `mode` in the project's `AIAnimationSettings`; UI order and labels in `AnimationModeLabels`; enum values are serialized and append-only: Rig 0, AIRedraw 1, AIPoseRig 2):

| | Rig (default) | AI Pose + Rig (beta, 1.2.0) | AI Redraw |
|---|---|---|---|
| Idea | Assign every source pixel to a part; move parts with a bone hierarchy | The same renderer, but the joint rotations come from saved AI pose data | Redraw frames with AnimateDiff + IPAdapter + ControlNet |
| Needs | A rig asset for the sprite (auto-created or edited) | A rig asset; ComfyUI + AnimateDiff-Evolved + SD1.5 + the **SDPose model (1.92 GB, one-time)** only to *generate* poses | ComfyUI, nodes, models, GPU |
| Identity | Exact (only original pixels, only source palette) | Exact (same renderer; byte-identical pipeline tail) | Approximate |
| Time | about 1 s of work (about 10-13 s including an editor batch start) | poses 50-130 s once (1-4 candidate videos of ~25 s + ComfyUI start); build 0.6-1.0 s (about 9-10 s with an editor batch start), no ComfyUI | 90-125 s |

Common back end for both: padded working canvas -> frames on a "cell grid" (1 cell = 1 source pixel) -> crop to the union of used pixels with the pivot mapped from the source ->
import as sprites (PPU/filter from source or from settings) -> `AnimationClip` -> optional Animator Controller state (never overwriting hand-made states).

---

## 4. Code map (`package/Editor`, assembly `AISpriteAnimation.Editor`, editor-only)

| File | Responsibility |
|---|---|
| `AIAnimationGenerator.cs` | Orchestrates one run: validate -> find/create rig -> pick method -> post-process -> import -> clip -> controller -> release backend. Locks assembly reload while running. |
| `AIAnimationSettings.cs` | Project settings asset (`ScriptableObject`), presets, enums. Created on first use at `Assets/AISpriteAnimation/`. |
| `AIAnimationWindow.cs`, `AIAnimationMenu.cs` | Main window (method, presets, ComfyUI settings, Diagnostics, Test Connection, Test Full Pipeline) and menu entries. |
| **Rig** | |
| `RigDefinition.cs` | Pure data: `RigPart` (14 parts), `RigJoint` (15 joints), per-pixel part bit mask, ground line; optional underlay and `legSwingScale` (1.7.0). |
| `ChargenRigImporter.cs` | (1.7.0) Reads generated rig data (`rig/rig.json`, `chargen-rig/1`: joints, ground, `parts.png`, `underlay.png`, leg swing scale) into the sprite's rig asset; menu *AI > Import Generated Rig Data*. Self-contained; the rest of the package only calls `TryImportFor` where a rig is missing. |
| `SpriteRigAsset.cs` | `ScriptableObject` wrapper; find/create the rig for a sprite (`<Sprite>_Rig.asset`). |
| `RigAutoBuilder.cs` | Heuristic starting rig (joints from body proportions; `AssignParts` derives the part map from the joints). |
| `RigAnimator.cs` | `RigPose`, `IRigPoseProvider` and the procedural Idle/Walk/Run/Attack/Jump/Sit poses (Jump: a key table on the 10-frame grid, no travel height, Rig only; Sit: a key table on the 8-frame grid, held last frame, Rig only; CrouchWalk: a formula (loop, thighs 96/90 degrees +-15, knee fold lifted by up to 26 degrees on the leg moving forward, same lean and arm values as the held Crouch); Crouch: a key table on the 6-frame grid, goose-step crouch (thigh about 96 degrees, shin about 36 degrees behind the vertical, torso lean 32 degrees with `Head = -Body`, arm angles = world angle minus the lean), held last frame, Rig only; the renderer fills the pelvis cells a thigh beyond 65 degrees leaves behind (`SpriteRig.FillVacatedBody` with `ThighParts`); `Head` is exactly `-Body` so the face is not re-sampled; the player's cloak tail pixels (x 17-19, y 32-38) are mapped to `LegNearUpper` in `player_east_Rig.asset` so a deep knee bend does not leave a gap under them; the Crouch test has a 4-connectivity check for floating pieces, but the visual check at 9x is what caught the gap). |
| `TurnThroughFront.cs`, `FrontSprite.cs` | Rotate preset (`AnimationPreset.turnThroughFront`): side view -> front view held for `frontHoldSeconds` -> opposite side, from the character's own side sprite (rig-rendered) and front sprite, nearest-neighbour squashes in between (about 55% width at the half-way point), axis = centre of the side view's feet. `FrontSprite` finds the front art (`frontSpritePath` or `<base>_front/_south/_down.png`) and never invents it. Applied in `AIAnimationGenerator` right after `SpriteRig.Render` (the frame count then follows from the hold time); Rig mode only. `TurnThroughFront` is pure math, tested in `tools/posetests`. |
| `SpriteRig.cs` | The renderer: bone hierarchy (affine matrices), grounding, composition at 4x, majority-vote downsample, vacated-body fill; underlay as extra Body pixels and `ApplyLegSwingScale` (1.7.0). Pure math. |
| `SpriteRigEditorWindow.cs` | Rig Editor (joints, ground line, paint/fill parts, auto-assign, live preview, undo; since 1.2.0 also the *AI poses* preview mode). |
| **AI Pose + Rig** (1.2.0) | |
| `AIPoseAsset.cs` | `ScriptableObject` `<Sprite>_<Anim>_AIPose.asset`: `frames` = FINAL poses (what is previewed and built), `sourceFrames` = the AI poses as delivered (diagnostics + `Reclean` only; was `rawFrames`, `FormerlySerializedAs`), clean-up settings, report text, `PoseQualityData` (the quantitative report), metadata, JSON export/import (rig part names). `NewTransient` + `SaveAs`: nothing is written before validation succeeded. |
| `AIPoseProvider.cs` | `IRigPoseProvider` over a pose asset: reads the stored FINAL poses (no re-cleaning), read-only `PoseValidator.Check`, cyclic resample, rig intensity; only when frame count or intensity differ `PoseConstraints.Enforce` re-applies the limits. |
| `AIPoseGenerator.cs` | Orchestrator: validate -> rig -> `IPoseGenerator` -> `PoseCleanup` -> AI-contribution report -> save (`SaveAs`) -> generator release (ComfyUI stopped if Unity started it). Never falls back to another backend. |
| `IPoseGenerator.cs` | `PoseBackend`, `IPoseGenerator`, context/result types, `PoseBackendUnavailableException`, `PoseGenerationFailedException`. |
| `SDPosePoseGenerator.cs` | The AI backend: prompts (`SDPosePrompts`), ComfyUI workflow `SDPoseMotion.json`, candidates, analysis, selection. |
| `SDPoseAnalysis.cs`, `OpenPoseTracking.cs`, `MotionCycleExtractor.cs` | Clip analysis: facing/limb labels, mapping, cycle cutting, quality, sword arm. Pure math. |
| `SDPoseModel.cs`, `SDPoseModelInstaller.cs` | Model facts, on-disk status, resumable verified download. |
| `ComfyUIWorkflowRunner.cs` | Start/run/stop ComfyUI for whole workflows with text outputs (`ComfyUIClient.WaitForResultAsync`). |
| `ProceduralPoseGenerator.cs`, `SketchEvidencePoseGenerator.cs`, `PoseSketch.cs`, `RigPoseFitter.cs` | The two non-AI backends (procedural; 1.2.0 sketch + AnimateDiff evidence with the analysis-by-synthesis fitter). |
| `PoseContribution.cs` | The AI-contribution metric (mean difference, movement vs stance, levels). |
| `OpenPoseMapper.cs`, `OpenPoseKeypoints.cs` | OpenPose body-18 <-> rig rotations; `OpenPoseJson` parser (ComfyUI POSE_KEYPOINT, classic OpenPose, bare list). The only code that knows both skeletons. |
| `PoseValidator.cs` (+`PoseLimits`), `PoseConstraints.cs` (+`ConstraintLimits`), `PoseMetrics.cs`, `PoseSmoother.cs`, `PoseCleanup.cs`, `PoseCleanupSettings.cs` (+`PoseConstraintSettings`), `PoseReport.cs`, `PoseChannels.cs` | Validation/repair (wrap-around, discarded impossible angles, rejected frames, step spreading), the **constraint stage** (spikes, gait balance, joint speed, leg extension, root, foot step/lock, weapon, idle sway), measurements (`PoseMetrics`: foot/root displacement, extension, slide, spike, weapon gap, visible motion), loop closing, smoothing, quantising, rejection (`PoseRejectedException`), the quantitative `PoseReport`; the float-channel representation they share. |
| `RigKinematics.cs` | Forward kinematics of the rig (joint positions, grounded root offset) without rendering. Uses `SpriteRig.Parent/Pivot/GroundParts/Affine`, which became `internal` in 1.2.0 (no behaviour change). |
| `PosePreviewPlayer.cs` | UI-independent preview (frames, Prev/Next/Seek, `Frame: n / N`, bones, contact sheet). |
| `AIPoseImporter.cs`, `BatchPoseTools.cs` | Import pose JSON / OpenPose JSON through the same validation; batch actions `preview`, `importjson`, `openpose`. |
| **AI Redraw** | |
| `ComfyUIAnimationBackend.cs` (+ `IAIAnimationBackend`, `GenerationRequest`) | Uploads images, builds and submits the workflow, polls, downloads frames. |
| `ComfyUIClient.cs`, `MiniJson.cs` | HTTP API wrapper; a tiny JSON reader (no Newtonsoft dependency). |
| `ComfyUIProcessManager.cs`, `ComfyUILifecycleHooks.cs`, `ComfyUIConnectionSettings.cs` | Start/detect/stop ComfyUI with ownership tracking; PID file; per-machine settings in `EditorPrefs`. |
| `ComfyWorkflowBuilder.cs` | The only place that knows workflow placeholders (`__PROMPT__`, `__POSE_00__`...). No node IDs in C#. |
| `SkeletonPoses.cs`, `PoseSequenceGenerator.cs` | Draws OpenPose-style skeleton images for the ControlNet. |
| **Shared** | |
| `SpriteFrameProcessor.cs` | Canvas preparation, palette/key colour, majority-vote conversion of AI frames, silhouette masking, crop + pivot (`Finalize`). |
| `SpriteAnimationImporter.cs` | Writes/imports frames (in place, keeps GUIDs), builds the clip, assigns Animator states safely. |
| `SourceSprite.cs`, `PackagePaths.cs` | Resolves the selected sprite; resolves the package's own paths and version. |
| `ComfyUIDiagnostics.cs`, `PipelineSelfTest.cs` | Setup checklist with versions; the "Test Full Pipeline" end-to-end self-test. |
| `BatchRunner.cs` | Command-line entry (`-executeMethod AISpriteAnimation.BatchRunner.Run`), used for all automated testing. |

Other package files: `Workflows/AnimateDiffSprite.json` (API-format workflow, 24 pose slots), `package.json`, `CHANGELOG.md`, `README.md`, `Documentation~/`.
Repo-level `tools/` holds development scripts (see `tools/README.md`).

---

## 5. Rig method in detail

**Parts** (`RigPart`, order matters, see section 8): Body, Head, Hair, ArmNearUpper, ArmNearLower (forearm + hand), Weapon, ArmFarUpper, ArmFarLower, LegNearUpper, LegNearLower, FootNear, LegFarUpper, LegFarLower, FootFar.
"Near" = facing the camera, drawn in front; "far" = behind.

**Hierarchy** (`SpriteRig.Parent`): Body -> {Head -> Hair, ArmNearUpper -> ArmNearLower -> Weapon, ArmFarUpper -> ArmFarLower}; legs hang from the root (so a leaning torso does not tilt the legs):
LegUpper -> LegLower -> Foot. Each part rotates about a joint (`SpriteRig.Pivot`). World transform = parent's world transform composed with a rotation about the pivot.

**Data model:** `RigDefinition.joints` (sprite pixel coordinates, **y down**, origin top-left), `groundY`, and `partMask` (one `ushort` per pixel, bit *i* = `RigPart` *i*; 0 = transparent; two bits = overlapping legs).
The mask is serialized as bytes (`maskBytes`); call `Flush()` after editing and `Invalidate()` after undo.
Two optional fields (1.7.0, filled only from generated rig data, see below): `underlayBytes` (RGBA per pixel, y down; empty = none; `HasUnderlay`, `GetUnderlay`, `SetUnderlay`)
and `legSwingScale` (initialised to 1; `EffectiveLegSwingScale` turns 0, negative or non-finite into 1, so rig assets saved before 1.7.0, which lack the field, behave as 1).
`Clone()` copies both.

**Frame rendering (`SpriteRig.Render`)**, per pose:
1. Compute world matrices from the pose angles.
2. **Grounding:** the lowest corner of all leg/foot pixels is moved onto `groundY`; root offset is rounded to whole pixels; `RigPose.hop` is the only intentional lift. This is why feet do not drift.
3. Draw parts back-to-front by **inverse mapping** at 4x sub-pixels (each destination sub-pixel looks up its source pixel, so rotation leaves no holes).
4. **Majority vote** per output pixel over its 4x4 sub-pixels -> only original colours, hard edges.
5. `FillVacatedBody`: where an arm drawn over the torso swung away, fill the hole with the nearest body colour. Then `CloseHoles` (1-pixel seams).
   With an underlay (1.7.0) those pixels were already drawn as Body pixels in step 3 (underlay colours, beneath the arm), so there is usually nothing left to fill.
   Without an underlay `bodySrc` is `src` and the code path is the 1.6.0 one (verified byte-identical, see section 9).
6. Mirror back for left-facing sprites (verified to be an exact mirror).

**Sign convention** (documented on `RigPose`; easy to get wrong): angles are **counter-clockwise positive on screen (y down)**. A limb hanging down swings *forward*, a forward-pointing sword tip goes *up*,
an upright torso leans *back*. So a forward lean is a *negative* body angle.

**Animations** are `RigPose` sequences (`ProceduralRigPoses`): loops sample exactly one cycle over N frames (frame N would equal frame 0); Attack is a one-shot with keyframes
(anticipation 0.25, swing 0.45, follow-through 0.60, recovery 0.80) sampled with both endpoints. `rigIntensity` (per preset) scales all rotations.
To change a motion, edit the numbers in `RigAnimator.cs`; to see the effect in seconds use `tools/riglab` (or the Rig Editor preview).

**Rig creation:** `RigAutoBuilder.Build` guesses joints from body-box proportions (neck 33%, hip 65% of the height by default, configurable in settings) and `AssignParts` derives the part map
from the joints. It is only a starting point (for the 48px player it needed manual joints). The player's joints are in `tools/riglab/example_player_joints.txt` and can be re-applied with
`-aiRigJoints <file> -aiRigOnly 1` in batch mode.

**Generated rig data (1.7.0, `ChargenRigImporter`):** an external generator can ship `rig/rig.json` (format `chargen-rig/1`) + `parts.png` (+ `underlay.png`) next to the sprite.
`ChargenRigImporter.Read` checks format, size, the sprite's sha256 and the part-bit names, reads joints / `ground_y` / part map / underlay / `animation_hints.leg_swing_scale` (accepted
when 0 < k <= 1) straight from the files (`Texture2D.LoadImage`, never through import settings), and `Import` writes them into the sprite's normal rig asset (created, or updated
in place keeping its GUID). Hooks, all of the form "`FindFor(...) ?? ChargenRigImporter.TryImportFor(...)`", i.e. only when a sprite has no rig asset: `AIAnimationGenerator` (unsaved in
AI Redraw mode, like `CreateAuto`), `AIPoseGenerator`; plus the menu *Assets > AI > Import Generated Rig Data*, a Rig Editor button and `BatchRunner -aiRigImport`. Any mismatch is a
warning and the old path (`CreateAuto` / dialog) runs. The leg swing scale is applied by `SpriteRig.ApplyLegSwingScale(rig, poses, animation)` to the poses (multiplies the six leg angles; for `animation == "run"` it also multiplies the torso angle by `ReducedSwingRunLeanScale` = 0.5 and adds the removed part to Head, ArmNearUpper and ArmFarUpper so their world angles are unchanged; returns the same array when the scale is 1) at the three places that turn poses into frames for a character: `AIAnimationGenerator`, and the Rig Editor's procedural and AI-pose previews. It is deliberately not inside `Render`, so `RigPoseFitter` and `RigKinematics` keep seeing the angles they set. Nothing else reads the new fields.

**Source of truth for identity:** original pixels. Never add a step that recolours, smooths or redraws in Rig mode.

---

## 5b. AI Pose + Rig in detail (1.2.0 architecture, 1.3.0 SDPose backend, 1.4.0 constraint stage)

Read `ai-pose-rig.md` for the measurements and `sdpose-model.md` for the model. Essentials for a maintainer:

* **History in one paragraph.** 1.2.0 shipped the pose architecture (asset, provider, validation, preview, OpenPose mapper) with a backend that measured as *not producing AI motion*: AnimateDiff on a 48 px sprite stays static or follows the guidance skeleton, so the result was the procedural sketch
  plus 0-3 degrees. The owner approved a 1.9 GB model download (SDPose) and asked for genuinely AI-generated motion. 1.3.0 does that; the old backend is kept as a labelled legacy option.
* **Data flow (SDPose).** `AIPoseGenerator` (orchestrator) -> `SDPosePoseGenerator`: workflow `Workflows/SDPoseMotion.json` (SD1.5 checkpoint + AnimateDiff-Evolved text-to-video, then `SDPoseKeypointExtractor` on the decoded frames, then `PreviewAny` that returns the keypoints as JSON text; the video
  frames are only downloaded when dumping) -> `OpenPoseJson.Parse` -> `SDPoseAnalysis.Analyze` = facing detection (+ mirror), `OpenPoseTracking.FixLimbSwaps`, `OpenPoseMapper.Map` (retarget to this rig), `MotionCycleExtractor.Extract` (cut the animation), sword-arm choice and damping,
  quality score -> best of up to `poseCandidates` seeds (`seed + 1013*i`, early stop at `poseGoodEnoughQuality`) -> back in the orchestrator: `AIPoseAsset.SetSource` + `BuildFinal` (`PoseCleanup.Run` = validate -> constraints -> smooth -> limits again, with the procedural reference for the report), `PoseContribution` vs `ProceduralRigPoses`, `SaveAs`.
  `AIAnimationGenerator` in `AIPoseRig` mode only resolves the asset (`PoseSource`: `Saved` default, `GenerateIfMissing` for the right-click menu, `Regenerate`), builds an `AIPoseProvider` and continues with the unchanged Rig tail.
* **Constraint stage (1.4.0), `PoseCleanup.Run`.** validate/repair (`PoseValidator`: wrap-around normalisation, impossible angles *discarded and interpolated* rather than clamped, rejected frames, step spreading) -> `PoseConstraints.Apply(finalPass:false)` (spikes, gait balance, joint speed, leg extension, root, hip step in flight, foot step with a stricter planted-foot limit, foot lock = sole tilt, weapon)
  -> loop closing, smoothing, quantising -> `PoseConstraints.Apply(finalPass:true)` (idle sway visibility first, then the same guarantees again because smoothing moves values back over limits) -> `PoseValidator.Check` -> `PoseMetrics` before/after -> `PoseReport` (quantitative) -> `PoseQualityData` in the asset.
  Rules to keep when you touch it: (a) **a valid pose is never touched**: the procedural animations of the Rig method must pass `PoseConstraints.Apply` unchanged (unit test); calibrate limits on them, never tighten a limit that makes them fire; (b) **every step must be idempotent** (unit test: applying the final pass to finished poses changes nothing); (c) the order matters: ranges, leg extension, root, hip, foot step (it moves the derived foot angle itself, so `Step(..., syncDerived:false)`), foot lock, weapon;
  (d) derived channels (feet, weapon) are kept consistent by `Context.SyncDerived` (incremental, linear in the limb angles, same formula as `OpenPoseMapper.DeriveSecondary`); (e) `Enforce` = limits only, used by `AIPoseProvider` when frame count or intensity differ from the saved poses; (f) pose assets store FINAL poses in `frames`, the AI source in `sourceFrames` (only for `Reclean`).
  Things that were tried and removed, so nobody repeats them: knee-bend "foot lift" of the swinging foot (0.4 px of lift needs ~20 deg of knee bend, and the procedural walk slides its feet the same way); a "ground snap" that nudged the lowest foot/knee/hip by a few degrees to put the lowest corner on a pixel boundary (could not fix the one 1 px frame of the Attack without bending the leg, and changed valid procedural poses); mean-shifting both legs to a common centre (flattened the stride: the master-leg approach in `GaitBalance` replaced it).
  Idle: the generated idle videos barely move (keypoint noise as large as the motion with the original prompt). The 1.4.0 prompt ("subtly breathing, shoulders gently rising and falling") gives smooth clips; clip selection rewards visible motion; `IdleMotion` amplifies the coherent (first two harmonics) part of the AI sway up to x4 only if the neck/hands move less than 1.2 px, and reports the gain.
* **Backends** (`PoseBackend`, `IPoseGenerator`): `SDPose` (AI), `ProceduralFallback` (`PoseSketch`, no ComfyUI, labelled NOT AI), `SketchEvidenceLegacy` (the 1.2.0 AnimateDiff-on-the-sprite + `RigPoseFitter`, labelled NOT AI). A backend that cannot run throws
  `PoseBackendUnavailableException` (model missing, nodes missing); one that runs but finds nothing throws `PoseGenerationFailedException`. **Never add a silent fallback between backends**: the owner explicitly forbids presenting procedural motion as AI motion.
* **Model.** `SDPoseModel` (constants, on-disk status) and `SDPoseModelInstaller` (resumable download, SHA-256). The model is never bundled or committed (`.gitignore` blocks `*.safetensors`). The authoritative availability check is ComfyUI's own `/object_info`
  (`SDPosePoseGenerator.CheckNodesAndModelsAsync`); the disk check only avoids starting ComfyUI to learn the model is missing.
* **ComfyUI lifecycle** is the Redraw one (`ComfyUIProcessManager`) through `ComfyUIWorkflowRunner`; the orchestrator calls `generator.ReleaseAsync` in `finally`. Verified: stopped after success, failure, cancel; an external instance is used and left running. Building from saved poses never creates a runner.
* **What makes real clips usable** (all in pure math, unit-tested in `tools/posetests` with real SDPose output in `tools/posetests/fixtures`):
  facing + mirror; limb label continuity (constant-velocity prediction, swap if clearly better); `Roughness` (second differences) to reject flickering detections; gait symmetry to complete a cycle from a partial clip; cycle seam handling in `PoseSmoother.CloseLoop`;
  `PoseLimits` per kind (a walker's limbs are narrower than a runner's); sword arm = the calmer arm (Idle/Walk/Run) or the more active arm (Attack); near-arm damping (`OpenPoseMapperSettings.weaponArm*`).
* **Measured reality** (see `ai-pose-rig.md`): AnimateDiff v3 + SD1.5 produces a clean side-view clip for roughly half of the seeds (Walk/Run), less for Idle and Attack (front views, crops, psychedelic figures). That is why several seeds are tried; defaults trade time (50-130 s) for success.
  Attack is the weakest (can fail with "no usable motion"), Idle is a subtle, smooth sway on a relaxed stance (1.4.0 prompt + visible-motion selection; with the original prompt the clips were keypoint noise). Improvements would come from a better video model/checkpoint (a photoreal SD1.5 fine-tune, SDXL/other video models), not from this package's analysis.
* **AI contribution** (`PoseContribution`): thresholds negligible < 5 deg, moderate 5-15, significant > 15 (mean difference to the procedural animation after best cyclic alignment); also reports movement vs stance. Real SDPose clips land at 13-40 deg; the procedural walk with another phase or +10% amplitude is negligible.
* **Replacing the source again.** Anything that yields OpenPose keypoints enters through `AIPoseImporter` (clips with >= 6 frames go through the same analysis), or write `PoseFrameData` directly (pose JSON). Everything after the asset is source-independent.
* **Conventions that bite.** The legacy fitter works on *world* bone angles, poses store *relative* angles. Angles are degrees in assets/channels and radians in `RigPose`. Keypoints are y-down; `OpenPoseTracking.Mirror` flips x only (angles are all that matter).
  `OpenPoseMapper.Map(..., facingLeft)` expects keypoints that look like the sprite's own canvas (so `SDPoseAnalysis` mirrors twice for a left-facing sprite). `RigKinematics` and the mapper are right-facing; left-facing sprites are mirrored at the edges.
* **Threading.** `RigPoseFitter.Fit` (legacy) runs on worker threads; `SDPoseAnalysis` is cheap and runs inline.
* **Settings** (`AIAnimationSettings`): `poseBackend`, `sdposeModelName`, `sdposeWorkflow`, `poseVideoWidth/Height/Frames/Steps/MotionScale`, `poseCandidates`, `poseGoodEnoughQuality`, `poseMinQuality`, `poseAssetFolder`, `poseCleanup` (+ legacy `aiPoseGenerationSize/Guidance/SketchVariation/EvidenceWeight`).

## 6. AI Redraw method in detail

Pipeline (`AIAnimationGenerator` + `ComfyUIAnimationBackend`): sprite is scaled up (nearest) onto a padded canvas with a flat key-colour background (white unless the sprite uses near-white; chosen automatically).
Uploaded inputs: the canvas, a **tight reference crop** (so IPAdapter's 224px view is filled by the character), and one **pose image per frame** from `SkeletonPoses`.
Workflow `AnimateDiffSprite` v3 (about 70 nodes with 24 pose slots): checkpoint -> img2img start latent from the canvas -> IPAdapter Plus (reference crop) -> Tile ControlNet (anchor, `__TILE_END__` = 0.6)
-> OpenPose ControlNet (pose batch, trimmed to the first `__FRAME_COUNT__` by `ImageFromBatch`) -> AnimateDiff-Evolved sampling -> KSampler -> VAE decode -> PreviewImage.
Post-processing (`SpriteFrameProcessor.ProcessFrame`): majority-vote into the source palette, key out the background, then **mask to the rig's silhouette of the source sprite** (grown by `aiMaskRadius`),
despeckle, peel halos, crop with pivot.

**Why a tail buffer:** AnimateDiff v3 is trained on 16-frame windows; in a native 8-frame batch the last 1-2 frames degrade. The generator therefore creates `tailBufferFrames` (default 4) extra frames
and drops them, so you always get exactly the frames you asked for (cap: 24 pose slots, so N + buffer <= 24).

**Process lifecycle (`ComfyUIProcessManager`) - the part that must stay correct**
* If `/system_stats` answers, use that ComfyUI and **never stop it**.
* Otherwise start the configured process hidden, mark it owned, wait for the API; after import/clip creation stop it: Ctrl+C (graceful), then `taskkill /T /F` on that tree after a grace period.
  The same shutdown runs on failure/timeout/cancel/editor quit/reload.
* Crash safety: `Library/AISpriteAnimation/comfyui.pid` stores the ComfyUI PID + start time **and the owning Unity process**. At the next editor start (`ComfyUILifecycleHooks`) a leftover process is killed
  only if its owner Unity process is gone. Never kill by process name.
* Gotchas learned the hard way: (a) Unity's **asset-import worker** processes also load editor assemblies and their static constructors ran the orphan cleanup, killing the ComfyUI the main editor had just started
  (fixed: workers skip the hook, and the PID file records the owner); (b) `EditorPrefs` is main-thread only (snapshot values before `Task.Run`); (c) a script reload during a run drops the async work, so `LockReloadAssemblies` is held while generating.

**What was tested and rejected** (details in `benchmarks.md`): stronger IPAdapter weights (no consistent gain), FreeNoise (no effect below one context window), FreeInit (2x time, no identity gain),
context windows (the core ControlNet is incompatible with sliding windows; would need `ComfyUI-Advanced-ControlNet`, not worth it for <= 24 frames).

---

## 7. Conventions that matter when editing

* **Coordinates:** sprite/rig/pose math is **y down, origin top-left**, in sprite pixels. Unity textures (`Color32[]`, `GetPixels32`) are **bottom-left**. Conversions happen at the edges
  (`SpriteRig.Render` input/output, `SpriteFrameProcessor`). Output frames on the "cell grid" are bottom-left, like textures. Mixing these up is the most likely source of upside-down or offset results.
* **Cell grid:** 1 cell = 1 source pixel = `PreparedInput.Scale` canvas pixels; the padded grid is `Cells x Cells`; the source sprite sits at (`LeftCells`, `BottomCells`).
* **Pivot:** `SpriteFrameProcessor.Finalize` maps the source pivot into the cropped frame so every frame has the same pivot; do not recompute per frame.
* **Pixel art:** nearest-neighbour or majority vote only; hard 0/255 alpha; colours only from the source palette. Importer filter/PPU come from the source sprite or from the project's settings.
* **One class per file**, PascalCase public / camelCase private, `[SerializeField]`-style tuning exposed on the settings asset, not hard-coded. The package must stay game-agnostic (no game names, paths or namespaces).
* **Unity metas:** every file needs its `.meta` committed (Unity writes them into the package folder when the package is referenced by `file:`). Check with the loop in section 11.
* Shell/editing warning from development: heredocs in some shells mangled backslashes (e.g. `'\\'`); verify string literals containing backslashes after scripted edits.

---

## 8. What you must update, and when

| Trigger | What to do |
|---|---|
| **Publishing** | Create a GitHub repo, then `git remote add origin https://github.com/<user>/AI-Sprite-Animation.git` and `git push -u origin main --tags`. Then change the game's `Packages/manifest.json` to `https://github.com/<user>/AI-Sprite-Animation.git?path=/package#v1.1.0`, and fix the placeholder URLs in both READMEs and in the game's `CLAUDE.md`. Nothing is pushed yet. |
| **Any release** | Bump `version` in `package/package.json`, add a `CHANGELOG.md` entry, commit, `git tag vX.Y.Z`, push with `--tags`. SemVer: patch = fixes, minor = compatible features, major = breaking settings/workflow/rig-asset format. |
| **Changing `RigPart` or `RigJoint` order/count** | **Breaking for every saved rig asset** (the part mask is bit-indexed, joints are index-addressed, `PartCount`/`JointCount` are constants, `Parent`/`Pivot`/`DrawOrder` in `SpriteRig` are arrays indexed by part). Only append at the end, add a migration, and bump MAJOR. |
| **Changing the workflow JSON** | Edit/regenerate (`tools/build_workflow.py`), keep placeholders in `ComfyWorkflowBuilder`, bump `BundledWorkflowVersion`, run *Test Full Pipeline* and `-aiDiagnostics live`. If you change the slot count, update `ComfyWorkflowBuilder.MaxFrames` and the README. |
| **Updating ComfyUI / AnimateDiff-Evolved / IPAdapter** | Node names or inputs may change. Run Diagnostics > *Validate with ComfyUI* (it checks every node class and every model/option of the workflow against `/object_info`), fix the workflow, rerun the self-test. Update the versions quoted in the READMEs. |
| **Supporting another Unity version** | `package.json` says `6000.6` because that is the only version tested. Test, then lower it (and update the README line). Do not claim more. |
| **New character** | Select the sprite -> Sprite Rig Editor -> *Create Rig (auto)* -> drag joints, paint parts, check the preview. Commit `<Sprite>_Rig.asset`. |
| **New animation type** | See the checklist "Adding an animation type" right below this table. |
| **Changing `RigPart` / `RigJoint`** (additional) | Saved `AIPoseAsset`s are indexed by `RigPart` order (`PoseFrameData.degrees`) and `PoseChannels` assume `PartCount`: migrate them too. `Parent`/`Pivot` tables are shared by the renderer, `RigKinematics` and the fitter. |
| **Changing the pose clean-up defaults** | Saved assets keep the final poses made with the settings of their time; builds use them as they are. To apply new settings or an edited rig to an existing asset use **Re-apply Constraints** (window) or `-aiPoseAction reclean` (recomputes `frames` from `sourceFrames`, no ComfyUI). |
| **Game-specific conventions change** (PPU, folders, binding path, filter) | Edit only the game's `Assets/Settings/AISpriteAnimation/AIAnimationSettings.asset`. |
| **Settings fields renamed/removed** | Unity ignores unknown serialized fields and uses initializer defaults for new ones, but presets are serialized: after changing preset defaults, existing assets keep old values until their `presets:` block is removed or edited. The game's asset still contains a few obsolete fields (`rigSwing` etc.); harmless. |


### Adding an animation type (jump, double jump, hurt, die, ... ; this is the normal way to answer "I would like to add an X animation")

Do it end to end, without asking for approval of the design; state the assumptions in the report. Checklist (all inside the package repository, never in the game):

1. **Pose function**: add `case "<kind>"` to `RigAnimator.Evaluate` and write `private static RigPose <Kind>(int t, int n, float k)` (t = frame index, n = frame count, k = rig intensity). Angles are per `RigPart` in degrees via `p.SetDegrees(part, deg)` (counter-clockwise on screen = positive, relative to the parent bone), `p.root` moves the whole character in pixels (y down), **`p.hop` lifts it off the ground** (pixels, positive = up; the renderer otherwise keeps the lowest foot on the ground line).
   Phase it like an animator: anticipation, action, follow-through/recovery; keep one-shot animations ending near their start pose (or say they do not). Example outline for a **double jump** (12 frames, 12 FPS, one-shot): crouch (knees bent, arms back, no hop) -> launch (legs extend, arms swing up, hop rises) -> first apex (tuck, hop ~10 px) -> second push in mid-air (arms swing down, small extra hop, legs re-tuck, optional slight body flip via `RigPart.Body`) -> second apex (~14 px) -> fall (legs reach down, arms up) -> land (knees bent, hop 0) -> recover. Use `hop` for height, not `root.y` (root.y is bounded for AI pose paths).
2. **Preset**: add an `AnimationPreset` to `AIAnimationSettings.CreateDefaultPresets` (`name`, `frames`, `fps`, `loop`, `poseKind` = the case name, a short `prompt`), and **add its name to `AddedAfterFirstRelease`** so existing settings assets receive it.
3. **Airborne kinds**: if the animation leaves the ground, add the kind to `PoseLimits.AllowsAirborne` (and a range in `PoseLimits.RangeScale`, constraint limits in `ConstraintLimits.For`) only if it must also work through AI Pose + Rig; Rig mode needs nothing else.
4. **Menu**: one `[MenuItem("Assets/AI/Generate Animation/<Name>")]` plus its validate entry in `AIAnimationMenu` (menu items are static).
5. **Tests and checks**: extend `tools/posetests` (pose finite, joints within `PoseLimits`, first/last pose as intended, no NaN), build it, then generate the animation on the test sprite in a throw-away project (`BatchRunner -aiPreset <Name> -aiMode rig`), run the pixel check (palette, alpha 0/255, one pivot/size, no border contact) and **look at the rendered frames at 5-8x zoom**.
6. **Docs/version**: README (preset table, defaults), CHANGELOG, the version in `package.json`, this file's code map if you added files. Do not commit unless asked.
7. If the animation needs **art the character does not have** (a front or back view for turning, a different pose sprite), use the character's own art: look for it in the project and in images shared in the conversation (a pixel-exact screenshot can be recovered, see how `player_front.png` was); never draw or AI-generate replacement art unless the user asks for it; if it truly cannot be found, stop with ONE message naming the exact file to add (that is the only legitimate question).

---

## 9. How to test (what was actually done, and how to repeat it)

All Unity tests were run headless on a copy of the game (so the user's open editor is not disturbed). Pattern:

```
Unity.exe -batchmode -nographics -projectPath <test project> -executeMethod AISpriteAnimation.BatchRunner.Run ^
  -aiSource Assets/Art/Player/player_east.png -aiPreset Walk -aiFrames 8 -aiController Assets/Animations/Player/Player.controller -logFile out.log
```

Useful switches: `-aiOverride "mode=ai,denoise=0.8,..."`, `-aiWorkflow <file>`, `-aiSeed`, `-aiTimeout`, `-aiCancelAfter`, `-aiDiagnostics live|offline`, `-aiSelfTest 1`, `-aiRigJoints <file> -aiRigOnly 1`, `-aiComfyDir`.
Exit code 0 = success, 1 = failure, 2 = cancelled. Look for `BATCH SUCCESS/FAILED` in the log.

AI Pose + Rig 1.3.0/1.4.0 (SDPose) was tested the same way, plus: model-missing run (`-aiOverride sdposeModel=nonexistent.safetensors`: exit 1, ComfyUI never started), missing SD checkpoint (`checkpoint=missing...`: error after ComfyUI started, ComfyUI stopped), `-aiCancelAfter 45`
(exit 2, ComfyUI stopped), external ComfyUI (used and left running), asset byte-identical after every failed run, `openpose` import of a real keypoint clip, Rig frames/clips byte-identical to the 1.1.0 package, Redraw frames byte-identical, `-aiSelfTest 1` (tiny SDPose generation + rebuild).
Analysis-only iteration without Unity: `dotnet build tools/posetests` then `posetests analyze ...` on the `keypoints_*.json` files that `-aiPoseDump` writes.

AI Pose + Rig (1.2.0) was tested this way (`C:\AI\unitytest`, scripts in `C:\AI\exp`: `ur.sh`, `gen_all.sh`, `pose_build.sh`, `rig_regress.sh`, `redraw.sh`; they are not part of the repo):
* Pose generation for Idle/Walk/Run/Attack (8 frames, seed 21) with `-aiMode pose -aiPoseAction generate`; builds from the saved poses with `-aiMode pose` (log shows no `Starting ComfyUI`); port 8188 closed and VRAM back to baseline after every generation, also after a failing run (bad checkpoint) and a cancelled run, with the saved asset byte-identical afterwards; an external ComfyUI stays alive.
* Rig regression: Idle/Walk/Run/Attack frames and clips from the 1.1.0 package (git archive of HEAD in a second project) vs the new package are byte-identical. Redraw regression: the same seed gives byte-identical frames.
* `-aiSelfTest 1` passes with the new AI Pose step (poses generated, rebuilt from the saved poses). `tools/posetests` passes (see `tools/README.md`).
* Palette/alpha/grounding/border checks on all 32 AI-pose frames; Animator Controller keeps the hand-made `Idle`/`Walk` states and adds `... (AI)` states.

Generated rig data (1.7.0) was tested like this (scripts in the scratch area, not in the repo):
* Outside Unity: the rig sources at git HEAD and in the working tree compiled side by side (`RigDefinition`, `RigAutoBuilder`, `RigAnimator`, `SpriteRig` + a small harness). Every procedural animation (84 frames), both facings: byte-identical for the player's rig asset, an auto-built rig, the riglab example rig and 12 generated characters without their data; also for leg scale 1 / 0 / -2 and an all-transparent underlay. With the data the renderer equals the approved prototype. `tools/posetests` passes.
* In Unity (a copy of the game, `-executeMethod` test driver, HEAD package vs 1.7.0): the player's 9 presets, an un-rigged sprite and a generated character without `rig/` give byte-identical frames and clips (146 files). With 1.7.0: the importer reproduces `parts.png` / `underlay.png` exactly for 12 characters, auto-import on first generation for six (normal, sword, katana, two robed, dwarf), tampered sprite refused with fallback to the auto rig, import over an existing rig keeps the GUID, the fields survive save/reload, a 1.6.0 rig asset reads leg scale 1. Unity's 204 frames equal the harness renders. Quality numbers (exposed outline pixels, tears, robe separation) are in the generator's `docs/animation-investigation.md`.

Checks that were run and passed on the final code (RTX 3080, ComfyUI 0.38.0):
* Rig: Idle/Walk/Run/Attack on the player sprite, about 10 s each; every output pixel is a source palette colour, alpha is 0/255, no clipping, identical pivot, PPU 16 / Point, correct loop flags,
  Animator state kept the hand-made `Walk` and added `Walk (AI)`; lowest foot row constant for Idle/Walk/Attack, Run only lifts off. Left-facing = exact mirror.
* AI Redraw: ComfyUI not running -> Unity starts, generates, stops it (VRAM returns to baseline); external ComfyUI -> used and left alive; failure (bad model) -> real error shown, ComfyUI stopped;
  timeout and cancel -> cleanup; hard-killing Unity mid-run -> next editor start kills exactly that process tree and not other `python.exe` processes.
* `-aiSelfTest 1` (Rig with an auto-created rig, then AI Redraw) passes in about 55-60 s; a fresh project with only the package works.
* Diagnostics: all green with a live ComfyUI; missing model produces an actionable message.

Jump (1.5.0) was checked with `tools/posetests` and by rendering the frames with `tools/riglab` on the player sprite (looked at at 6x zoom); it was then generated through the open Unity editor (menu item *Assets/AI/Generate Animation/Jump*, 10 frames @ 12, one pivot, Point filter, PPU 16, palette 100%) and wired into the game's Animator by the game, not by the package.
Sit (1.6.0) was checked the same way (`tools/posetests`, `tools/riglab` render at 3x zoom) and then generated through the open Unity editor (menu item *Assets/AI/Generate Animation/Sit*: 8 frames @ 12, one-shot, one pivot, Point filter, PPU 16, palette 100%, nothing clipped). Version 1 was a deep squat (knees folded to about 150 degrees) and was rejected by the user as ugly; version 2 sits on the ground with the knees up (thigh about 120 degrees, shin hanging down) and lowers the hips by about 8 px of 48. The rig has no pelvis or back-leg buttock pixels, so the lower body is a dark blob at this size: a drawn sit-on-the-floor sprite would look better.
**Not tested:** the interactive editor windows (Rig Editor, main window GUI, the "no rig yet" dialog) were only compiled and exercised through the code they call, because testing was headless.
A first manual pass in the editor is the most valuable next step.

Identity metrics (`tools/eval_identity.py`): frames are aligned to the source through the shared pivot; head/torso pixel match, silhouette IoU, frame-to-frame consistency, movement and clipping are computed.
Note that after the hierarchical rig, "pixel match at the same position" is lower by design (arms/hair/lean move); the invariant to check is palette fidelity = 1.0 and visual inspection of a sheet.

---

## 10. Known limitations and ideas

* **Auto-rig quality:** proportions-based; needs manual joints for unusual sprites. A better silhouette analysis could improve it.
* **Hidden arms:** an arm painted over the torso leaves a hole that is filled with the surrounding colour (visible as a lighter patch on this sprite during Attack). Since 1.7.0 a rig can carry an underlay with the hidden torso pixels (generated characters ship one); hand-made rigs have none yet, and the Rig Editor cannot paint one.
* **Generated rig data** is only as good as its part map. The Local Character Generator (0.4.0) keeps held items, far-hand items and limb fragments attached and puts the weapon pivot at the grip; with its data no such piece floats on 12 test characters. Robed rigs (scale < 1) lean half as much in Run (5.5 deg), because the full 11 deg swung the hem off the front foot (`Render` rotates the whole Body, robe included, about the hip); at 5.5 deg nothing floats, the front foot still joins the hem by only 1-3 pixels in Run frames 6-7, a limit of the 0.3 leg swing (a smaller lean does not improve it, measured down to 0 deg). The leg swing scale applies to every animation including Jump/Sit/Crouch, which then fold a robe less.
* **Walk/Run arm swing** moves the sword noticeably forward; `hold` factors in `RigAnimator.Locomotion` and `rigIntensity` tune this.
* **Rig is side-view oriented** (near/far limbs, facing left/right). Top-down or front-view characters would need another part set.
* **Sliced sprite sheets:** a rig is matched by texture path + sprite name + size; a re-sliced sheet needs a new rig.
* **AI Redraw:** cannot keep pixel-level identity (by measurement), is GPU-heavy, and other programs using several GB of VRAM make it slow or crash it (this happened once during testing).
  Uploaded sprites accumulate in `ComfyUI/input/unity_ai_animation/` (the API cannot delete them).
* **AI Pose + Rig (1.4.0)**: motion is AI-generated (SDPose) and now passes a validation/constraint stage: Walk is a symmetric, small-stepped gait clearly different from the procedural one (moderate, 12.6 deg), Run is a lower, more crouched run (significant, 29.4 deg), Attack is a rough guard/lunge (significant, 38.7 deg; the weakest, can fail), Idle is a relaxed stance with a smooth subtle sway (moderate, 11.3 deg; the movement difference to the procedural idle is only 1.8 deg because both move a few degrees). See `ai-pose-rig.md`.
  A better video model would improve every animation; the rest of the pipeline is model-agnostic (it only needs OpenPose keypoints of a person moving).
  The interactive pose preview was rendered in a real editor but not clicked through; everything behind the buttons was tested in batch mode. Left-facing sprites are unit-tested (mirror invariance), not run through a full generation.
* **Windows only** for process shutdown (`taskkill`, console Ctrl+C); other platforms fall back to `Process.Kill`.

---

## 11. Quick checks before a release

```
# every package file has a .meta
cd AI-Sprite-Animation
find package -type f ! -name "*.meta" ! -path "*~/*" | while read f; do [ -f "$f.meta" ] || echo "MISSING META: $f"; done

# compiles outside Unity (Unity 6000.6 reference assemblies)
# (or simply open a project that references the package and read the Unity Console; during development the package was also compiled with Unity's bundled Roslyn: `dotnet csc.dll -target:library -r:<each UnityEngine/*.dll> -r:<NetStandard/ref/2.1.0/*.dll> package/Editor/*.cs`)

# end-to-end: batch self-test on a project that references the package
Unity.exe -batchmode -nographics -projectPath <project> -executeMethod AISpriteAnimation.BatchRunner.Run -aiSelfTest 1 -logFile selftest.log
```

Then bump the version, update the changelog, commit and tag as described in section 8.
