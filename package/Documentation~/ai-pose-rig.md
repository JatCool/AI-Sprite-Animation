# AI Pose + Rig with SDPose: design, measurements and limits

Status: **beta** (package 1.4.0: SDPose backend of 1.3.0 plus the validation/constraint stage). Tested on one sprite (48x48 side-view humanoid, `player_east`, its rig `player_east_Rig.asset`), Windows 11, RTX 3080 10 GB, Unity 6000.6.0f1, ComfyUI 0.38.0.
Model: SDPose-Wholebody `sdpose_wholebody_fp16.safetensors` (details in `sdpose-model.md`).

## Goal and architecture

Rig keeps the character pixel-exact, AI Redraw cannot (`benchmarks.md`). AI Pose + Rig keeps the Rig's pixels and lets AI generate the motion:

```
animation request -> AnimateDiff video of a generic person -> SDPose keypoints -> cleaning + retargeting to THIS rig -> cut out the animation
   -> validation/smoothing -> pose asset -> IRigPoseProvider -> unchanged Rig renderer -> original pixels -> frames -> AnimationClip
```

* **The AI never produces a sprite frame.** The generated video is read for the person's pose and discarded; the character in it has nothing to do with the sprite.
* **SDPose is an estimator, not a generator.** The motion is created by the video model (AnimateDiff v3 on SD 1.5); SDPose turns the video into data. A text prompt per animation type (`SDPosePrompts`) asks for a side-view person walking / running / lunging with a sword / standing still.
* **Retargeting.** Each bone's rotation is the angle that turns the rig's own rest direction onto the observed direction, so the AI's body proportions never matter and the rig is never changed.
* `IRigPoseProvider` stays the single extension point (`ProceduralRigPoses`, `AIPoseProvider`); `IPoseGenerator` is the replaceable source of *saved* poses (`SDPose`, `ProceduralFallback`, `SketchEvidenceLegacy`). Nothing about the rig, its assets, the renderer, grounding, pivot, the clip builder or the Animator integration changed.
* **No silent fallback**: if SDPose cannot run, the user is told why and nothing is saved. The two other backends are labelled "NOT AI motion" in the window, in the asset and in the preview.

## Validation and constraint stage (1.4.0)

Between "AI pose extraction" and "saved pose asset" there is one clean-up stage (`PoseCleanup`). It exists to stop **obviously broken poses**; it must not turn the AI motion back into a procedural one. Its design rule: *a valid pose is never touched*, a pose that breaks a limit is pulled back to the limit (not replaced), and every change is counted.

```
SDPose keypoints -> OpenPoseMapper (retarget) -> cycle extraction -> source poses
   1. validate/repair   wrap-around angles, impossible angles (discarded + interpolated, not clamped), rejected frames, flip limit
   2. constraints       single-frame spikes, lopsided gait, joint speed, leg extension, root movement, foot step, foot lock, weapon
   3. loop closing, smoothing, optional stepping
   4. the limits again  (smoothing can move a value back over a limit) + idle sway visibility
   -> FINAL poses + quantitative report -> AIPose asset
```

| Constraint | What it prevents | How it corrects |
|---|---|---|
| Wrap-around / impossible angles | An elbow measured as -226 deg (= +134 deg) or flickering between impossible values | Equivalent angle nearest to the allowed range; an angle still far outside is an estimator failure and is interpolated from its neighbours (a joint with no usable frame holds its typical clamped value) |
| Rejected frames | A frame in which more than 50% of the joints are unusable | Replaced by the interpolation of its neighbours, counted as *rejected*; more than 25% rejected frames reject the whole sequence |
| Single-frame spikes | One frame sticking out of its neighbours (a 99 deg arm between 30 deg and 33 deg) | Predicted from the four surrounding frames (exact for smooth swings, so real peaks survive); the overshoot is reduced to an allowed size per animation type, not removed |
| Gait balance (walk/run) | A lopsided stride: one leg swinging about a line 30 deg in front of the other | Only if the legs' centre angles differ by more than 8 deg: the leg that really strides sets the waveform and the other follows it half a cycle later (blend up to 75%). Not applied to a gait that already swings about one line |
| Joint speed | A joint turning faster than a person can between two frames | The excess is spread over the neighbouring frames (a wider, slower movement), never cut off |
| Leg extension | A leg folded into the body | Knee bend of the offending frame reduced until hip-to-foot is at least 75% of the leg (walk), 44% (run), 50% (attack), 92% (idle) |
| Root movement | Lurches of the body: root sway and hop speed and range | Step limits per animation type, spread over neighbouring frames; applied before the foot limit because the root is part of where a foot is drawn |
| Foot step | A foot jumping between frames (the 19 px jump of the attack) | Rendered foot displacement limited per animation type (fraction of the rig's leg length); a foot that stands on the ground is limited further (it may not skid as fast as a swinging foot) |
| Foot lock | A planted foot with its sole tilted, a planted foot skidding | The planted foot's sole is tilted back to within 5 deg of the ground; skid limited as above. The renderer's own grounding (lowest foot on the ground line) is unchanged |
| Weapon | A sword leaving the hand or digging into the ground | The weapon is a child bone of the hand, so it cannot leave it (verified as a gap in px in every report); its tip is kept above the ground line |
| Idle sway | An idle whose upper body moves less than about 1 px, i.e. looks frozen on a 48 px sprite | Only if the neck and hands move less than 1.2 px: the coherent part of the AI's own sway (slow harmonics, noise removed) is amplified, as little as needed (at most x4), and the gain is reported |

All limits are expressed in the rig's own units (fractions of its leg length, degrees) and scale with *Pose clean-up > Constraints > Limit Scale*. They were calibrated so that the procedural animations of the Rig method (valid by definition) pass the stage **unchanged** (a unit test), and idempotent: applying the stage to finished poses changes nothing (a unit test).

### Quantitative validation report

Every generated animation gets one; it is saved in the pose asset (`quality`, `cleanupReport`), shown in the window (*Validation report* foldout) and printed by the batch tools:

* mean AI vs procedural pose difference and the maximum joint deviation (and where),
* maximum foot displacement and maximum root (hip) displacement per frame (px), final and as the AI delivered it, with the limits,
* foot slide while planted, minimum leg extension, largest single-frame spike, weapon gap,
* number and percentage of corrected frames, number of rejected frames, the largest change of a joint made by validation and constraints,
* each correction with its number of values and frames.

### What the pose asset contains

`AIPoseAsset.frames` are the **final** poses (after the stage). *Preview* and *Build Animation* use them as they are; nothing is cleaned again on the way, so what you previewed is what is built. The poses as the AI delivered them are kept as `sourceFrames` (renamed from `rawFrames`, old assets are migrated by Unity's `FormerlySerializedAs`) only for diagnostics and for the explicit **Re-apply Constraints** button (rig edited, settings changed); they are never used to build. Building at another frame count or intensity applies only the limits again (`PoseConstraints.Enforce`), never the shaping steps.

## What the 1.2.0 test showed, and why SDPose

1.2.0 used AnimateDiff *on the sprite* guided by a procedural skeleton. Measured on this sprite: free-running AnimateDiff leaves it standing, guided runs only follow the skeleton, so the "AI correction" averaged 0-3 degrees: not AI motion.
The fix is not a better fit but a different source: let AnimateDiff draw a *generic* person (no sprite, no guidance, so it is free to move), and read that person's pose with a real pose estimator.
Feasibility was measured before building on it: with the prompt "full body shot of a young man walking, side view in profile ..." AnimateDiff v3 produces a recognisable side-view walker, SDPose finds the 14 body joints in 80-99% of the frames of the usable clips, and the extracted gait differs clearly from the procedural one.

## Measurements

All numbers: seed 22, 8 frames, 12 FPS, default settings (clip 384x576, 16 frames, 16 steps, motion scale 1.3, up to 4 candidate clips, stop at quality 0.4), RTX 3080 10 GB, package 1.4.0, sprite `player_east` with its rig. "Source" = the AI poses as they came out of the backend, "final" = after validation and constraints (what is saved and built).

### Generation

| Animation | Candidates used | Chosen clip quality | Total | of which ComfyUI incl. start |
|---|---|---|---|---|
| Walk | 2 | 0.54 | 73.3 s | 51.0 s |
| Run | 1 | 0.41 | 76.6 s | 28.2 s |
| Attack | 4 (all) | 0.21 | 115.8 s | 93.5 s |
| Idle | 1 | 0.58 | 49.4 s | 27.1 s |

A warm external ComfyUI makes a single-candidate generation take about 27-35 s. The constraint stage itself takes well under a second; **Build Animation** from saved poses: 0.7-0.9 s of work (about 9-10 s with an editor batch start), no ComfyUI.

### AI contribution (difference to the procedural animation of the same type; negligible < 5 deg, moderate 5-15, significant > 15)

| Animation | Source (before constraints) | Final (saved) | Level | Movement / stance (final) | Joint motion not explained by the procedural curves |
|---|---|---|---|---|---|
| Walk | 15.8 deg | **12.6 deg** | moderate | 9.9 / 9.6 deg | 78% |
| Run | 29.0 deg | **29.4 deg** | significant | 23.3 / 21.9 deg | 78% |
| Attack | 43.5 deg | **38.7 deg** | significant | 22.0 / 33.7 deg | 88% |
| Idle | 11.4 deg | **11.3 deg** | moderate | 1.8 / 11.3 deg | 84% |

The constraints take little of the AI's motion away (Walk loses 3 deg, mostly the glitch frame and the lopsided stride; Run and Idle nothing measurable; Attack 5 deg, mostly the flip frames). The Idle's low *movement* number is real and explained below.

### Quantitative validation report (final poses)

| | Walk | Run | Attack | Idle |
|---|---|---|---|---|
| Mean AI vs procedural pose difference | 12.6 deg | 29.4 deg | 38.7 deg | 11.3 deg |
| Maximum joint deviation (where) | 53.0 deg (far shin, frame 2) | 115.0 deg (far arm, frame 5) | 122.2 deg (near arm, frame 1) | 39.1 deg (far forearm, frame 3) |
| Max foot displacement per frame, final (AI source; limit) | 4.4 px (7.4; 7.2) | 7.8 px (16.5; 13.2) | 9.9 px (20.3; 9.9) | 0.4 px (0.7; 1.6) |
| Max root (hip) displacement per frame, final (AI source) | 1.0 px (4.0) | 3.6 px (8.2; limit 4.0) | 3.0 px (3.0) | 0.0 px (0.0) |
| Largest single-frame spike, final (source) | 8 deg (65) | 33 deg (42) | 45 deg (131) | 2 deg (3) |
| Frames corrected / rejected | 8 of 8 (100%) / 0 | 3 of 8 (38%) / 0 | 7 of 8 (88%) / 0 | 8 of 8 (100%) / 0 |
| Largest change of one joint by validation/constraints | 66.9 deg (the spike frame) | 34.3 deg | 164.7 deg (a forearm flip) | 4.1 deg |
| Min leg extension / weapon gap / planted-foot skid | 98% / 0.00 / 0.0 px | 53% / 0.00 / 0.0 px | 55% / 0.00 / 0.0 px | 99% / 0.00 / 0.0 px |

"Frames corrected" counts every frame in which some step changed a joint by more than 1 deg or the root by more than 0.2 px. In the Walk the gait balance touches every frame (it shifts the near leg's centre angle), in the Idle the sway amplification does, so 100% there does not mean "heavily repaired": the per-correction counts (`corrections`) say what happened. Run needed the least correction (a clean source clip).

### Per-joint comparison (final poses, peak-to-peak range AI / procedural, degrees)

| Joint | Walk | Run | Attack | Idle |
|---|---|---|---|---|
| Near arm upper | 29 / 40 | 41 / 84 | 102 / 96 | 6 / 5 |
| Far arm upper | 27 / 40 | 65 / 84 | 83 / 39 | 7 / 4 |
| Near thigh | 27 / 52 | 52 / 92 | 73 / 31 | 2 / 0 |
| Far thigh | 28 / 52 | 44 / 92 | 61 / 26 | 3 / 0 |
| Near shin (knee) | 14 / 48 | 78 / 105 | 62 / 10 | 3 / 0 |
| Far shin (knee) | 14 / 48 | 40 / 105 | 68 / 4 | 4 / 0 |

### The pixels are still the sprite's

For all 32 AI-pose frames built from the saved final poses (`ai-pose-comparison.png`): 0 opaque pixels outside the source's 51-colour palette, alpha only 0 or 255, no frame touches its border, every animation has one frame size and one pivot (`Idle` 50x50, `Walk` 50x50, `Run` 53x50, `Attack` 50x55). Rig output (Idle/Walk/Run/Attack frames and clips, plus the project's existing Idle/Walk/Turn clips) and AI Redraw output are byte-identical to the 1.1.0 package.
Grounding (lowest opaque row of the rendered character per frame): **Walk and Idle: the same row in all 8 frames**. Attack: the same row in 7 of 8 frames, one lunge frame is 1 px higher (the renderer rounds the ground shift to whole pixels and drops a row the lowest sole corner only just misses; the constraint stage cannot fix it without bending the leg by more than is worth it, and the procedural Rig has the same effect in other frames). Run leaves the ground by design (rows 45-49).
The weapon stays in the hand (weapon gap 0.00 px in every frame, because it is a child bone of the hand).

### Visual comparison with the procedural Rig (`ai-pose-comparison.png`, for each animation the procedural row above the AI row)

* **Walk**: clearly a different gait: the sword is carried forward and the arms swing less, the stride is short and shuffling but symmetric (the AI's lopsided stride and the one-frame sword-arm glitch are gone). It reads as a relaxed, small-stepped walk, not as an energetic one.
* **Run**: a different run: the knees stay much more bent on average (mean shin angle -72 deg and -64 deg vs -38 deg in the procedural run) with a smaller range of knee movement (78 / 40 deg vs 105 deg), arms carried differently, and no single-frame hop spike any more (hip step 8.2 -> 3.6 px). On this sprite (overlapping legs) it still looks a little compressed and crouched; its minimum leg extension (53%) is no lower than the procedural run's (45%), so the constraint stage had nothing to correct there.
* **Attack**: anticipation (sword back), a guard/lunge with a wide stance, sword up in the follow-through; the strike itself is calmer than the source because the one-frame whip of the arm (-142 deg) and the 19 px foot jump were reduced to the allowed size. It is still the roughest animation (video-model limit).
* **Idle**: the AI's relaxed standing pose with arms down and a smooth, subtle sway of head, shoulders and the far forearm (see below). It is not a dramatic idle and the procedural idle is as good.

### Final visual QA of the rendered 8-frame animations (1.4.0)

Method: the actually built frames of each AI animation next to the procedural Rig animation at 5-9x zoom (whole body and leg crops, half a cycle per image), pixel counts changed between consecutive frames including the loop wrap (no sudden jump: largest/median <= 1.8 everywhere), the joint curves next to the aligned procedural reference, and, for the two large deviations, the source video with the detected skeleton.

* **The 115 deg (Run, far upper arm, frame 5) and 122 deg (Attack, near upper arm, frame 1) are legitimate motion, not retargeting artifacts.** Run: the source is a real side-view runner whose elbows are bent about 90 deg, one arm forward and the other swinging back; the retargeted far arm goes smoothly from -9 to -75 deg and back (elbow 85-120 deg) while the procedural run swings symmetrically around vertical (+-42), so the difference at the back-swing frame is 115 deg. The mapper round trip is exact (0.000 deg), the curve is smooth, the arm is visible behind the torso in frames 5-6.
  Attack: the source swordsman winds the blade up overhead, steps into a lunge and grips with both hands; frame 1 is the one-frame overhead wind-up (-142 deg in the source, reduced to -56 deg by the spike limit) against the procedural arm raise at that phase. The skeleton follows the video accurately. A large difference to the procedural animation means "a different attack", not "a wrong joint".
* No joint flips, no hyperextended knees or elbows (all within the animation's ranges), no feet sliding (planted-foot skid 0.0 px, foot extents move by 1-3 px), no frame-to-frame redesign: the character is recognisably the same in every frame (same palette, silhouette parts, face and hair).

| | Reads as | Remaining issues (nothing changed because of them) |
|---|---|---|
| Walk | A relaxed walk with a small symmetric shuffle, sword carried low, arms swinging a little. | The legs overlap into one mass in most frames, so at 1x the stride is hard to read; head dips a little in frame 6; less energetic than the procedural walk. |
| Run | A lower, more crouched jog: sword held forward, the far arm pumping back, a visible flight phase. | Weaker leg drive than the procedural run (legs form a column on this sprite); the far arm snaps from -75 to -11 deg between frames 6 and 7 (64 deg in one frame, within the limit, not visible as a jump in the pixel counts). |
| Attack | A wind-up, wide lunging stance and a raised/thrust sword: a brandish more than a slash. | Frames 5-6 hold the sword close to the torso with the arm hidden; the **last frame ends with the sword raised**, not at rest, so an Animator exit into Idle needs a crossfade (the procedural attack returns to its start pose); one lunge frame sits 1 px above the ground (renderer rounding, left alone: fixing it would bend a valid pose). |
| Idle | A calm stand with the free arm folded at the chest, a smooth subtle sway of head, shoulders and forearm. | Very subtle by nature (about 1.2 px at the neck/hands); loop seam 7 -> 0 changes slightly more pixels than the other steps; no lateral weight shift. |

Decision: visually acceptable for a beta; no further constraints were added.

### Idle: why the movement difference was only 4.3 deg, and what changed

Measured on the four candidate clips of the 1.3.0 run (old prompt "standing still and breathing"): only one was a usable idle. In it the neck moves 0.044 of the torso length over the whole clip (about 0.6 px on this sprite), and the joint angles have a frame-to-frame roughness (second difference) of 8-10 deg while their standard deviation is 3-4 deg: **the signal was as small as the keypoint noise**, so smoothing and range limits took the little motion away and what remained was a different static stance (arms forward, legs apart). Motion scale 2.0 destroys the video (SDPose finds no person); a prompt that asks for weight shifting makes the person step (legs move 50 deg, the idle limits rightly reject it).
What helped: the prompt. "full body shot of a young man standing in profile facing right, subtly breathing, shoulders gently rising and falling, relaxed idle stance ..." gives smooth clips (roughness 0-3 deg instead of 24), 3 of 8 test seeds were usable, the best has body 2 deg, head 7 deg, near arm 6 deg and far forearm 13 deg peak-to-peak with clean sinusoid-like curves. Idle clip selection now also rewards visible motion (a perfectly still clip is a frozen idle). Result (seed 22, first candidate accepted): quality 0.58 instead of 0.27, 1 candidate instead of 4 (49 s instead of 186 s), no value had to be repaired (36 of 136 before).
Still honest: the video model produces *subtle* motion; after the clean-up the neck and hands move about 1.2 px (the constraint stage amplified the coherent part of the AI's own sway by 2.7x, reported as "idle sway made visible"; without it 0.8 px). The difference to the procedural idle is mostly the AI's relaxed arm and leg positions (stance 11.3 deg) and a larger, smoother sway (AI joint range 5 deg vs 2 deg); the *movement* difference stays small (1.8 deg) because both idles move only a few degrees. A lateral weight shift does not appear in the generated clips.

### Tests

* `tools/posetests` (console, seconds, 109 checks): OpenPose mapper round trip 0.000 deg; facing detection; limb-label repair; cycle extraction from synthetic and real clips; a still person is not accepted as a walk; left-facing sprite gets the same motion; the contribution metric; validation, smoothing, resampling, kinematics, the legacy fitter; and the **constraint stage**:
  the procedural Idle/Walk/Run/Attack pass it unchanged; a +70 deg arm glitch is reduced to 11 deg while a smooth +-50 deg swing keeps its peaks; an injected foot jump is spread over neighbouring frames and ends within the limit; a folded leg is opened to the minimum extension while a run's deep knee flexion survives; a 7 px root lurch becomes steps within the limit;
  a lopsided stride (centre lines 36 deg apart) is balanced to 0 deg and keeps its swing; -330 deg is read as 30 deg and a flickering impossible elbow does not flicker; one unusable frame is rejected and rebuilt, half unusable frames reject the sequence; a too small idle sway is amplified to the visible threshold, idempotently, and can be switched off; `Enforce` never reshapes;
  a planted foot skidding 7.3 px per frame is limited to 5.9 and a sole tilted 40 deg comes back to 5 deg; the sword stays in the hand and out of the ground; the stage can be switched off; and the four real SDPose clips (fixtures) end within every limit, with their AI contribution intact, idempotently.
* Unity batch: all four animations generated through Unity-owned ComfyUI (stopped afterwards each time, port closed, VRAM back to baseline); builds from the saved final poses never start ComfyUI (0.7-0.9 s); `reclean` rebuilds the final poses from the stored AI source poses without ComfyUI; model missing -> exit 1 in 9 s ("SDPose model is required for AI Pose + Rig.", ComfyUI never started); cancel -> exit 2 and ComfyUI stopped, saved asset byte-identical;
  an external ComfyUI is used and left running; Rig frames and clips and AI Redraw frames byte-identical to the 1.1.0 package; `-aiSelfTest 1` passes (tiny SDPose generation, rebuild, ComfyUI started and stopped by Unity).

## Design decisions

* **Clips, not poses.** A generated video is not an animation. `MotionCycleExtractor` finds a repeatable stretch (full cycle, or a mirror-symmetric half cycle for walk/run) so even a clip with only 0.6 of a gait cycle gives a seamless loop.
* **Several seeds, best one.** About half of the generated Walk/Run clips are unusable (front views, crops, garbage). Rather than trying to fix a bad clip, the generator scores up to four and keeps the best; quality = detection coverage x side view x smoothness x stride x seam.
* **Smoothness as a quality signal.** Mislabelled left/right limbs and flickering detections make joint angles jump; the second-difference "roughness" separates the good runner from the same seed's bad candidate (0.33 vs 0.05) where all other signals agreed.
* **Retargeting rules for a sword-carrying character**: the calmer arm is the near (sword) arm during Idle/Walk/Run and the active arm during Attack; the near arm moves at 0.6-0.8 of the AI's amplitude outside attacks (the procedural animations do the same). Limb limits per animation type keep a walk a walk.
* **Evidence of a good result is measured, not claimed.** The contribution report is saved with every pose asset; the window and the preview show it; procedural/legacy/imported poses never get a "level".
* **The model is a conscious download.** Never bundled, never silent, resumable, verified; `.gitignore` blocks model files.
* **Nothing is saved from a failed or rejected run.** Poses are built in a transient asset and written only after validation.

## Known limitations

1. **Quality depends on the video model**: SD 1.5 + AnimateDiff v3 draws a clean side-view walker/runner roughly every second seed, a still person or a sword attack much less often. Attack can fail with "no usable motion" (nothing saved); Idle mostly changes the stance.
2. Walk strides are short (the foot travels 4-5 px per frame at most) because the clip's person walks slowly; the constraint stage removes the glitch frame and the lopsided stride but does not lengthen the stride (that would be procedural). A stronger video model or a photoreal fine-tune would help every animation; the pipeline needs only keypoints of a moving person.
3. Generation takes 49-116 s (1-4 candidates of about 25 s, ComfyUI start 15 s; the new Idle prompt usually needs one candidate). Lower *Pose Candidates* for speed (less reliable) or raise it for reliability.
4. Side-view humanoids only (as specified); the near arm is assumed to hold the weapon; feet, weapon and hair are derived (OpenPose has no toes, hand orientation or hair).
5. Left-facing sprites are unit-tested (identical motion), not run through a full generation. The interactive windows were rendered in a real editor but not clicked through; the logic behind every button was exercised in batch mode.
6. The contribution metric compares with the Rig method's procedural animation: a motion that is simply bad also scores "significant". Look at the preview.
7. The constraint stage rounds off what the video model got wrong; it cannot add what is missing. An Attack whose strike was drawn as a chaotic figure stays a rough attack (the 1-frame sword whip is reduced, not replaced), and an Idle video without breathing gets its sway amplified at most x4.
8. Grounding: the renderer rounds the vertical ground shift to whole pixels, so an animation can have a frame in which the lowest sole corner lands 1 px short of the ground row (Attack, 1 frame of 8; none in Walk and Idle). This is a property of the unchanged Rig renderer.
9. SDPose estimates in the image plane: depth (which leg is nearer) is assigned by label continuity and is arbitrary but consistent.

## Next steps (not done here)

1. A better text-to-video source (photoreal SD 1.5 fine-tune, SDXL-class motion models) and a larger candidate pool for Attack; the analysis and mapping are model-agnostic.
2. Use SDPose's foot keypoints (heel/toe) for the rig's foot rotation and 3 candidate phases for stance correction in Idle.
3. Run on more sprites and a left-facing sprite end to end.
