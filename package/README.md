# AI Sprite Animation

Generate 2D sprite animations from a single sprite, inside the Unity editor.
Select a sprite, choose *Idle / Walk / Run / Attack*, and get imported frames and an `AnimationClip`
that look like **the same character** moving, in hard-edged pixel art, with the sprite's own transparency.

Three animation methods:

| Method | How frames are made | Identity | Needs ComfyUI | Time (RTX 3080) |
|---|---|---|---|---|
| **Rig (Recommended for Pixel Art)** - default | The sprite's own pixels are assigned to parts (head, hair, body, both arms, both legs, weapon) and moved through a bone hierarchy | **Pixel-exact**: same palette, details, face, weapon | No | about 1 s |
| **AI Pose + Rig (Beta)** | The AI generates the *motion* (AnimateDiff video of a person + SDPose pose estimation, retargeted to the rig and saved once as a pose asset); the same Rig renderer draws every frame from the original pixels | **Pixel-exact** (same renderer as Rig) | Only to generate poses (then stopped); building from saved poses never. Needs the SDPose model (1.92 GB, one-time install) | poses about 50-130 s once, then about 1 s per build |
| **AI Redraw (Experimental)** | [ComfyUI](https://github.com/Comfy-Org/ComfyUI) + AnimateDiff + IPAdapter + OpenPose ControlNet redraw every frame | Approximate (silhouette yes, pixel detail no): slower, GPU-dependent, may alter face and clothing; for stylised sprites | Yes (started and stopped automatically) | about 90-120 s |

Why Rig is the default: `Documentation~/benchmarks.md` benchmarks AnimateDiff (IPAdapter strengths, FreeNoise, FreeInit, context windows) against the rig on
the same sprite and seed. No diffusion variant kept a 48x48 character's pixels (torso pixel match 0.10-0.29); the rig only moves original pixels, so palette and details are identical by construction.
The skeleton, not a text prompt, controls the movement in both modes.

```
Rig:       sprite + rig asset -> bone hierarchy moves the parts -> grounding -> majority-vote downsample -> crop + pivot -> sprites -> AnimationClip
AI Pose:   AnimateDiff video + SDPose -> retargeted pose asset (joint rotations) -> IRigPoseProvider -> the same Rig renderer (original pixels) -> same tail
AIRedraw:  sprite -> padded canvas + reference + pose images -> ComfyUI (AnimateDiff) -> frames -> palette snap + source silhouette mask -> same tail
```

*Maintainers: see `Documentation~/HANDOVER.md` (context, architecture, what to update, how it was tested).*

## 1. Install the package

Unity **6000.6** or newer. Developed and tested on **6000.6.0f1** only; older versions are not claimed to work (the minimum in `package.json` equals the tested version).

**From Git** (Unity: *Window > Package Manager > + > Install package from git URL*):

```
https://github.com/JatCool/AI-Sprite-Animation.git?path=/package#v1.7.0
```

or in `Packages/manifest.json`:

```json
"com.limpo.ai-sprite-animation": "https://github.com/JatCool/AI-Sprite-Animation.git?path=/package#v1.7.0"
```

**From a local folder** (development; relative to the project's `Packages/` folder):

```json
"com.limpo.ai-sprite-animation": "file:../../AI-Sprite-Animation/package"
```

**Updating:** change the tag in the URL (`#v1.0.1`, `#v1.1.0`, ...) or remove the `#...` part to follow the default branch, then let Unity resolve the package.
Versions follow [SemVer](https://semver.org/) (see `CHANGELOG.md`): patch = fixes, minor = backwards-compatible features, major = breaking changes to settings or workflow.
Your project's settings asset keeps working across patch/minor updates; new fields get package defaults.

After installing you get **Tools > AI Sprite Animation > Generate Animation** (window) and
**right-click a sprite > AI > Generate Animation > Idle / Walk / Run / Attack / Rotate / Jump / Sit / Crouch / CrouchWalk**.
The first use creates `Assets/AISpriteAnimation/AIAnimationSettings.asset` (one per project; commit it).

## 2. Use it

1. Select a sprite (a PNG, or one sliced sprite of a sheet).
2. Right-click > **AI > Generate Animation > Walk**, or use the window (mode, frames, FPS, loop, optional Animator Controller).
3. Output (default `Assets/AIAnimations`, configurable):

```
Assets/AIAnimations/<Sprite>/<Animation>/
    input.png                    the image the generator started from
    frame_000.png ...            imported as Sprite (Single), same pivot on every frame
    <Sprite>_<Animation>.anim    AnimationClip (loops for Idle/Walk/Run, plays once for Attack)
```

* **Frame count**: exactly the requested number of frames, 2 to 24 (AIRedraw also generates a few extra frames and drops them, see below). Defaults: Idle 8 @ 12 fps, Walk 8 @ 12, Run 8 @ 14, Attack 10 @ 12, Rotate 18 @ 12 (one-shot), Jump 10 @ 12 (one-shot), Sit 8 @ 12 (one-shot, the last frame is the held pose), Crouch 6 @ 12 (one-shot, the last frame is the held pose), CrouchWalk 8 @ 12 (loop).
* **No clipping**: work happens on a padded canvas (default: sprite + 50% each side, i.e. 2x). Frames are then cropped to the union of all pixels any frame uses
  (plus the original sprite rectangle), so every frame has the same size and no limb is cut off.
* **Stable pivot**: the source sprite's pivot is mapped into the cropped frame, identically for every frame, so the character does not jump.
* **Pixel art**: nearest-neighbour only, majority-vote downsampling, colours only from the source palette, hard 0/255 alpha, no smoothing.
* Regenerating overwrites in place (GUIDs and Animator references survive) and removes stale frames. The source sprite is never modified.

**Animator Controller** (optional): the clip goes into a state named after the animation (`Walk`). If a state of that name already holds *your own* motion, a separate
`Walk (AI)` state is used; only an empty state or one that already holds a generated clip is updated. Transitions are never touched.

## 3. Rig mode (Recommended for Pixel Art)

Rig mode never redraws anything. A **rig** says which pixels of your sprite are the head, hair, body, each arm, each leg and the weapon, and where the
joints are. Animations then move those parts through a bone hierarchy. Every output pixel is an original pixel; transparency is the sprite's own alpha.

**Workflow:** select a sprite > *Generate Animation* > *Walk*. If the sprite has a rig (`<Sprite>_Rig.asset`, by default next to the sprite) it is used.
If not, you are asked to **open the Sprite Rig Editor** (recommended) or **auto-create** a starting rig (and refine it later). The rig is made once per character
and reused by every animation. AI Redraw can still be chosen manually.

### Parts and hierarchy

```
Root (translation only: grounding, lunges, hops)
 |- Body (pivot: hip)
 |    |- Head (neck) --- Hair (hair pivot: ponytail swings with lag)
 |    |- Right arm upper (shoulder) -- lower arm + hand (elbow) -- Weapon (weapon pivot)
 |    '- Left arm upper (shoulder) -- lower arm + hand (elbow)
 |- Right leg upper (hip) -- lower (knee) -- foot (ankle)
 '- Left leg upper (hip)  -- lower (knee) -- foot (ankle)
```

Rotating an upper arm carries the forearm, hand and weapon with it (forward kinematics). "Right/near" parts are drawn in front, "left/far" parts behind.
Overlapping legs (the usual side-view case) are supported: a pixel may belong to both legs.

### The Sprite Rig Editor

*Tools > AI Sprite Animation > Sprite Rig Editor* (or right-click a sprite > *AI > Sprite Rig Editor*).

* **Joints tool**: drag the dots (neck, hair pivot, hip, both shoulders/elbows/hands, both knees/feet, weapon pivot and tip) and the yellow **ground line**.
* **Paint parts / Fill region**: choose a part on the right, then paint pixels with a brush (*Add* keeps overlapping legs in both parts) or fill a connected region.
* **Auto-assign parts** re-derives all parts from the current joints (overwrites painting); **Reset rig** rebuilds everything from the sprite's shape.
* The **preview** plays any preset live with the real renderer, so you see the effect of every change. Undo/redo works.

### Animations

Idle, Walk, Run, Attack, **Jump** (1.5.0) and **Sit**, **Crouch** and **CrouchWalk** (1.6.0) are procedural pose sequences (rotations per part plus root movement). **Rotate** (1.5.0) is not a pose sequence: the character turns from its side view to face the viewer, stays in front for *Front Hold Seconds* (1 s), then turns to the opposite side. It needs the character's own **front-view sprite** (`player_east.png` -> `player_front.png` next to it, same canvas and ground line, or *Front Sprite Path* in the settings): the frames are the rig-rendered side sprite, the front sprite and nearest-neighbour squashes of both (about 55% wide at the half-way point), so they are pixel-exact (no new colours, hard alpha, same pivot). The package does not draw a front view for you: without it the animation stops with a message naming the file to add. Rig only; 18 frames @ 12 FPS, one-shot. The procedural pose sequences are:

* **Walk / Run**: alternating legs with knee flex, counter-swinging arms with elbow bend, forward lean, hair lag; the weapon partly cancels arm swing so a held sword stays steady. Run has a flight phase (intentional hop).
* **Attack**: anticipation (lean back, weapon raised) -> swing -> follow-through -> recovery, with a step forward. One-shot; both endpoints are sampled.
* **Jump** (1.5.0, Rig only): wind-up with arms swung back -> deep crouch -> launch (legs push off, arms and sword thrown forward and up) -> rise -> tucked apex (held two frames) -> fall (legs reach down) -> landing crouch -> back to the standing pose. 10 frames @ 12 FPS, one-shot, first and last frame identical. **The clip carries no travel height**: a game moves the character itself (rigidbody), so only the pose changes; `hop` (at most 1 px) merely keeps the hips at standing height while the legs tuck. The sword is counter-rotated so it keeps pointing forward while the arms go up. Because the clip starts and ends standing it can follow and lead into Idle. The AI methods refuse it with a clear message.
* **Sit** (1.6.0, Rig only): a small dip with the arms swung back -> a squat -> the character drops onto the ground with the knees up (thighs forward and up, shins hanging down), torso upright, forearms resting on the knees -> settles and **holds that pose on the last frame**. 8 frames @ 12 FPS, one-shot. The clip changes the pose only: it has no hop and no root movement; the renderer pins the lowest foot to the ground line, so folding the legs lowers the hips by itself (about 8 px of 48 on the player). The game decides how long to stay seated (a one-shot clip keeps its last frame) and standing up again is the Animator returning to Idle. The AI methods refuse it with a clear message.
* **Crouch** (1.6.0, Rig only): a goose-step crouch, not a sit and not a squat on the heels: quick dip -> both feet planted and flat, knees tucked forward, hips at about knee height, torso leaning in about 32 degrees with the head forward and low (the head is counter-rotated against the torso so its net rotation is 0 and the face is not re-sampled), arms hanging in front of the knees with the sword held low -> **holds that pose on the last frame**. 6 frames @ 12 FPS, one-shot. Pose only (no hop, no root movement); the hips go down by about 6 px of 48 and the head moves forward and down with the lean. Standing up again is the Animator returning to Idle. The AI methods refuse it with a clear message.
* **CrouchWalk** (1.6.0, Rig only, loop): the Crouch posture while walking, a goose-step: the same lean (head counter-rotated, face untouched), hips at about knee height, arms hanging in front of the knees, sword low; the legs take turns, the foot moving forward lifts and the other stays flat. 8 frames @ 12 FPS, in place (the game moves the character, slower than a normal walk). The AI methods refuse it with a clear message.
* **Idle**: breathing, weight shift, hair and weapon settle.
* Presets scale all rotations with *Rig Intensity*. Frame count is free (2-24); loops sample exactly one cycle.

**Grounding:** every frame the lowest foot pixel is placed on the rig's ground line, so feet never drift; the body height follows from the legs (and `hop` for runs/jumps).
Only intentional vertical movement (`hop`) leaves the ground line. Translations are snapped to whole pixels.

**Pixel art:** parts are composed at 4x and reduced by majority vote per pixel, so the output contains only colours that exist in the source, with hard edges, no blending
and no anti-aliasing. Where an arm swings away from the torso, the vacated spot is filled with the surrounding body colour. Frames are cropped to the union of all used pixels
with the source pivot mapped identically into every frame.

### Generated rig data (1.7.0, optional)

A character generator can deliver the rig together with the sprite: a folder `rig/` next to the sprite with `rig.json` (format `chargen-rig/1`),
`parts.png` and optionally `underlay.png` (the Local Character Generator, a separate repository, writes exactly this; the format is described in its
`docs/rig-data.md`). The package then uses three things from it, all optional and all stored in the ordinary `<Sprite>_Rig.asset`:

| Data | Effect |
|---|---|
| **Joints, ground line and per-pixel part map** (`parts.png`: R = part bits 0-7, G = bits 8-15, alpha = sprite pixel) | used instead of the auto-built rig; the part map is a 1:1 copy of what the Rig Editor's *Paint parts* stores |
| **Hidden-pixel layer** (`underlay.png`: what the torso looks like behind the near arm / weapon, in the sprite's own colours) | those pixels also become *Body* pixels, drawn beneath the arm; when the arm swings away they show instead of a hole or an outline |
| **Leg swing scale** (`animation_hints.leg_swing_scale`, 0 < k <= 1) | every leg angle of every generated pose is multiplied by k: robed characters (about 0.3) keep the robe as one silhouette instead of scissoring legs; such a rig (k < 1) also leans its torso half as much in **Run** (about 5.5 instead of 11 degrees; head and upper arms keep their world angles), so the robe's hem does not swing back off the front foot; Walk, Attack and every other animation keep their lean |

**When it is used:** when a sprite has **no rig asset yet** and `rig/rig.json` exists next to it, *Generate Animation* (and *Generate Poses*) imports it
automatically instead of auto-building a rig. Explicitly: right-click the sprite > *AI > Import Generated Rig Data* (asks before replacing an existing rig;
Undo restores it), the **Import Generated Rig Data** button of the Sprite Rig Editor, or batch `-aiRigImport <rig.json|folder|auto>`.
The data must belong to exactly this sprite: same size and the sprite's sha256 as recorded in `rig.json`; otherwise it is not used (a warning in the Console names
the reason) and the rig is made as before. Joints, parts and ground line can be refined afterwards in the Rig Editor as usual; *Auto-assign parts* / *Reset rig*
keep working the old way: *Auto-assign parts* replaces the generated part map (the underlay and leg swing scale stay), *Reset rig* drops all generated data. The leg swing scale is a field of the rig (*Leg Swing Scale* in the rig asset's Inspector) and
applies to every animation of that character, also to Jump/Sit/Crouch, which then fold the robe less.

**Unchanged when the data is absent:** a rig without an underlay and with leg swing scale 1 (every hand-made or auto-built rig, and every rig asset saved by
1.6.0 or earlier, whose missing fields read as "none" / 1) renders byte-identically to 1.6.0. The leg scale is applied to the poses before rendering, not inside
`SpriteRig.Render`, so the AI pose fitter and the kinematics see the angles they set.

### Extending: pose providers

Poses come through `IRigPoseProvider` (`RigPose[] GetPoses(animation, frameCount, cycle, intensity)`):

```
IRigPoseProvider
 |- ProceduralRigPoses   Idle / Walk / Run / Attack from formulas (Rig mode)
 |- AIPoseProvider       saved AI pose assets, validated + smoothed (AI Pose + Rig)
 '- your provider        anything that returns RigPose[]; the renderer and everything after it stay unchanged
```

## 4. AI Pose + Rig (Beta)

The AI decides **how the character moves**; the sprite decides **what it looks like**. The AI never produces a pixel of the animation: it produces joint rotations
(a saved *pose asset*), and the same Rig renderer as in Rig mode applies them to the original sprite pixels. Identity, palette, hard alpha, grounding and pivot are exactly
those of Rig mode (rendering is the unchanged `SpriteRig.Render`; the Rig output of 1.1.0 to 1.4.0 is byte-identical).

```
animation request (Walk / Run / Attack / Idle)
   -> AnimateDiff text-to-video: a generic person doing it, seen from the side          (ComfyUI, no sprite involved)
   -> SDPose reads the body pose of every video frame                                   (OpenPose keypoints)
   -> facing, left/right limb labels and the sword arm are sorted out
   -> OpenPoseMapper: keypoints -> this character's rig rotations (retargeting)
   -> the best repeatable stretch of the clip is cut out as the animation               (gait symmetry for walk/run)
   -> several clips (seeds) if needed, the best one is kept
   -> validation / repair -> CONSTRAINTS (spikes, lopsided gait, foot jumps, folded legs, root lurches, foot lock, weapon) -> smoothing
   -> the FINAL poses + a quantitative validation report are saved as <Sprite>_<Animation>_AIPose.asset     (ComfyUI is stopped here)
Build Animation (any time, no AI, no ComfyUI):
   AIPoseProvider (an IRigPoseProvider) -> unchanged Rig renderer -> original pixels -> frames -> AnimationClip -> Animator
```

SDPose is a pose *estimator*: it does not invent motion by itself. The motion comes from the video model; SDPose is what makes it usable as data. The video is only ever read
for the person's pose and then thrown away.

### Pose backends

| Backend (window: *Pose Backend*) | What it is | AI motion? |
|---|---|---|
| **SDPose** (default) | The pipeline above | **Yes** |
| Procedural fallback | The Rig method's formulas with seeded variation. Instant, no ComfyUI | **No**: labelled "NOT AI motion" everywhere |
| Sketch + AnimateDiff evidence (legacy 1.2.0) | A procedural sketch guides AnimateDiff on the sprite; the AI frames only nudge it by a few degrees | **No** (measured: it stays at the sketch) |

If SDPose cannot run (model missing, nodes missing, no usable motion in any generated clip) you get a clear message and nothing is saved; **there is no silent fallback to a procedural backend.**

### SDPose model (one-time setup)

The model is not part of the package. The window shows *"SDPose model is required for AI Pose + Rig"* with an **Install / Download Model** button when it is missing: after a confirmation dialog it downloads
`sdpose_wholebody_fp16.safetensors` (1.92 GB, Hugging Face `Comfy-Org/SDPose`, MIT) into `<ComfyUI>/models/checkpoints`, resumable, verified with SHA-256. Details, URL, size, hash and license: `Documentation~/sdpose-model.md`.
It needs ComfyUI 0.38 or newer (the `SDPoseKeypointExtractor` node is built in) and the same AnimateDiff-Evolved node pack and SD 1.5 checkpoint as AI Redraw.

### Workflow

In the *AI Sprite Animation* window choose **Animation Method: AI Pose + Rig (Beta)** (the default stays *Rig*). The **Pose Generation** box has:

1. **Generate Poses**: the only step that needs ComfyUI (started if needed, **stopped again if Unity started it**; an externally started ComfyUI is left alone). Generates up to *Candidates* motion videos (about 25 s each, it stops at the first good one),
   reads their poses, retargets, validates and saves the pose asset. About 47-130 s on an RTX 3080 including ComfyUI start. Does not create an animation.
2. **Preview Poses**: opens the Sprite Rig Editor in *AI poses* mode: the original sprite beside the animated rig, `|<` / Play-Pause / `>|`, a frame slider, `Frame: 4 / 8`, and *Show bones*.
   Every pixel shown is rendered by the rig from the original sprite, exactly what *Build Animation* will import.
3. **Build Animation**: renders the saved poses, imports the frames, creates the clip and updates the Animator. About a second of work (about 9 s including an editor start in batch mode), no ComfyUI.
   Frame count and FPS may differ from the generation: loops are resampled cyclically, one-shots keep both end poses.
4. **Import JSON...**: poses from elsewhere (pose JSON, or OpenPose keypoint JSON from any estimator). Same validation and smoothing; the asset is labelled *Imported*, not AI.

The right-click menu *Assets > AI > Generate Animation > Walk* in this mode builds from the saved poses and generates them once if none exist.

### What the AI contributes (and how that is measured)

Every pose asset records **how much of its motion is the AI's own**: the mean difference (degrees, over body, head, both arms and both legs, at the best cyclic alignment) to the procedural animation of the same type and frame count.

| AI contribution | Mean difference |
|---|---|
| negligible | below 5 degrees |
| moderate | 5 to 15 degrees |
| significant | above 15 degrees |

The summary also splits the difference into movement and constant stance, and states how much of the joint motion curves the procedural reference does not explain. Procedural fallback and legacy poses are labelled *NOT AI* instead of getting a level.
Measured on the test sprite, see `Documentation~/ai-pose-rig.md` (final saved poses: Walk moderate 12.6 deg, Run significant 29.4 deg, Attack significant 38.7 deg, Idle moderate 11.3 deg, mostly the AI's relaxed stance: its movement part is only 1.8 deg because both idles move a few degrees; the generated idle sway is subtle and is made visible by amplifying its coherent part, reported as such).

### What is saved

`<Sprite>_<Animation>_AIPose.asset` (`AIPoseAsset`, next to the sprite or in *Pose Asset Folder*): animation, loop, FPS, source sprite and rig (path + fingerprint),
the **final** per-frame poses (validated, constrained, smoothed: exactly what is previewed and built), the AI poses as delivered (kept only for diagnostics and *Re-apply Constraints*), the clean-up settings, the quantitative validation report, the AI-contribution summary and generation metadata (backend, seed, prompt, candidates, timing, notes).
Per frame: one rotation per rig part in degrees (relative to the parent bone, counter-clockwise on screen = positive) plus the root offset. Only the actual rig structure is used:

```json
{ "animation": "Walk", "fps": 12, "loop": true,
  "frames": [ { "root": { "x": 0, "y": 0, "hop": 0 },
                "Body": { "rotation": -1.0 }, "Head": { "rotation": 0.4 }, "Hair": { "rotation": 3.1 },
                "ArmNearUpper": { "rotation": -3 }, "ArmNearLower": { "rotation": 9 }, "Weapon": { "rotation": 2 },
                "ArmFarUpper": { "rotation": 1 }, "ArmFarLower": { "rotation": 8 },
                "LegNearUpper": { "rotation": 3 }, "LegNearLower": { "rotation": -46 }, "FootNear": { "rotation": 32 },
                "LegFarUpper": { "rotation": -5 }, "LegFarLower": { "rotation": -6 }, "FootFar": { "rotation": 4 } }, ... ] }
```

Rotations plus a root offset are the whole representation (no pixel coordinates, no per-frame scale): the bone hierarchy moves the children
(shoulder -> upper arm -> forearm -> hand -> weapon). `AIPoseAsset.ToJson()` / *Import JSON...* read and write exactly this.

### How SDPose / OpenPose maps to the rig

`OpenPoseMapper` is the only place that knows both skeletons; the rig does not depend on OpenPose. The pipeline first normalises the keypoints: the person is turned to face the sprite's direction, and the left/right limb labels
(which estimators assign by appearance and flip between frames) are made continuous in time with a constant-velocity tracker; the arm that suits the role carries the sword (calm during Idle/Walk/Run, the active one during an Attack).
The camera-facing side (OpenPose *right*) is the rig's *near* side.

| Rig part (rotation relative to its parent) | From OpenPose body-18 |
|---|---|
| Body | rotation of mid-hip (8/11) -> neck (1) against the rig's hip -> neck |
| Head | neck -> mean of nose/eyes/ears (0, 14-17), relative to the body; the sequence median is removed (OpenPose has no head-centre joint) |
| ArmNearUpper / ArmFarUpper | shoulder -> elbow (2->3 / 5->6), relative to the body |
| ArmNearLower / ArmFarLower | elbow -> wrist (3->4 / 6->7), relative to the upper arm |
| LegNearUpper / LegFarUpper | mid-hip -> knee (8->9 / 11->12); legs hang from the root |
| LegNearLower / LegFarLower | knee -> ankle (9->10 / 12->13), relative to the thigh |
| FootNear / FootFar | derived: kept roughly flat (0.75 x counter-rotation of the leg) |
| Weapon | derived: follows the near forearm by a factor (Idle/Walk 0.3, Run 0.45, Attack 0.9); OpenPose has no hand orientation |
| Hair | derived: lagging follower of the head (secondary motion) |
| root x / hop | hip travel (walking drift removed for loops) and ankle height, scaled from skeleton size to rig size (hop only for Run) |

Each rotation is the angle that turns the rig's rest bone direction onto the observed direction. **This is the retargeting**: the character's own proportions and rest pose define the neutral, the AI's body proportions never matter,
and the rig is never changed to fit the AI. The mapping is tested as a round trip (rig pose -> OpenPose keypoints -> rig pose) at 0.000 degrees error for body, arms and legs.

### Cutting an animation out of a clip

A generated clip is not an animation: it drifts, starts and stops anywhere and rarely holds exactly one cycle. `MotionCycleExtractor` works on the rig's joint angles:
* **Walk / Run**: it searches the stretch whose end looks like its start (a full cycle) or, because a gait is mirror symmetric, a half cycle whose end looks like its start with the near and far limbs exchanged (the second half is then the mirror of the first).
  The stretch must contain a real leg swing; a clip of a person standing still is not accepted as a walk.
* **Idle**: the longest stretch that closes by itself, otherwise the clip is played forward and back.
* **Attack** (one-shot): the most active stretch whose end is close to its start.

### Choosing among clips

Each clip is scored (0..1) from: how completely the person is detected, how much of a side view it is, how smooth the motion is (flickering or mislabelled detections make the joint angles jump), the size of the leg swing / arm activity, and how well the
cycle closes. Idle also prefers calm arms. The best clip wins; the run stops early once one reaches *Good Enough Quality*; if even the best stays below *Min Quality* the generation fails with the list of what was tried and nothing is saved.

### Validation, grounding and smoothing

Nothing malformed reaches the AnimationClip: every pose sequence (generated or imported) goes through `PoseCleanup` before it is saved. The saved result is final: a build uses it as it is (a build at another frame count or intensity only re-applies the limits).

* **Validation / repair** (`PoseValidator`): all 14 joints and the root must exist in every frame; non-finite values are interpolated from neighbouring frames; joint angles are clamped to a per-animation range (`PoseLimits`: a walker's arms swing at most about 55-65 degrees,
  thighs 50-55; Idle is narrower; Run and Attack use the full range); changes above *Max Degrees Per Frame* (flips of the estimator) are limited; every child bone is verified to stay attached to its parent (weapon on the hand, head on the torso, torso on the root).
  Angles that wrapped around (an elbow read as -226 deg is +134 deg) are normalised; an angle that is still far outside the allowed range is an estimator failure and is interpolated from its neighbours instead of being clamped to the limit; a frame in which most joints are unusable is **rejected** and rebuilt from its neighbours (reported).
  A sequence in which more than *Reject Repair Fraction* (35%) of the values needed repair, or more than 25% of the frames are unusable, is **rejected**: nothing is saved, the previous poses stay, and the report explains why.
* **Constraints** (`PoseConstraints`, *Pose clean-up > Constraints*): they only prevent obviously broken poses and leave valid motion alone (the procedural animations pass them unchanged). Single-frame spikes are reduced to an allowed overshoot; a lopsided walk/run stride is balanced; no joint turns faster than a person can (spread over neighbouring frames, not cut off);
  legs are never folded into the body; the root does not lurch; no foot jumps further than the animation type allows and a planted foot does not skid or tilt; the sword stays in the hand and out of the ground; an idle whose upper body would move less than about 1 px has the coherent part of the AI's own sway amplified (the gain is reported).
  Details and numbers: `Documentation~/ai-pose-rig.md`. Every generated animation gets a **quantitative validation report** (AI-vs-procedural difference, maximum joint deviation, foot and root displacement, corrected/rejected frames) in the pose asset, the window and the batch log.
* **Grounding**: unchanged renderer rule (the lowest foot pixel lands on the ground line every frame). On top, the AI may not move the character vertically: vertical root movement and hop are removed for Idle, Walk and Attack;
  only Run keeps a bounded flight phase; horizontal root drift is bounded.
* **Smoothing** (`PoseSmoother`, all configurable in the window's *Pose clean-up* foldout): edge-preserving temporal filter (small jitter is averaged, changes above *Preserve Degrees* such as an attack swing are barely touched),
  extra smoothing for root/head/hair, *Passes*, one-shot first/last poses pinned, loops closed only when they have a real seam, and optional **Step Degrees** to snap every angle to 4-5 degrees for a deliberately stepped pixel-art look.
  Smoothing 0 changes nothing.

### Cost (RTX 3080 10 GB, 8 frames)

| Step | Time | ComfyUI |
|---|---|---|
| Generate Poses | 47-130 s (ComfyUI start about 15 s, each candidate video about 25 s, SDPose and the analysis a second or two) | started, then stopped if Unity started it |
| Preview Poses | instant | no |
| Build Animation from saved poses | 0.6-1.0 s of work (about 9-10 s including an editor start in batch mode) | **not started** |
| Rig (procedural), for comparison | about 1 s of work (about 9 s in batch mode) | no |

Limits: side-view humanoids with a rig (near/far limbs); 2-24 frames; animation types Idle, Walk, Run, Attack. See `Documentation~/ai-pose-rig.md` for measured quality per animation and the known weaknesses (Idle and Attack are the weakest).

## 5. AIRedraw mode: ComfyUI setup

Tested with **ComfyUI v0.38.0, Windows portable NVIDIA build**, Python 3.13, PyTorch 2.14 + CUDA 13, RTX 3080 10 GB (peak about 7 GB VRAM above the desktop).

1. Download `ComfyUI_windows_portable_nvidia.7z` from <https://github.com/Comfy-Org/ComfyUI/releases>, extract it, e.g. to `C:\AI\ComfyUI`.
2. In the window (*ComfyUI* section) set **ComfyUI Folder** to the folder that contains `main.py` (portable: `C:\AI\ComfyUI\ComfyUI`). The embedded Python is detected automatically.

Custom nodes (clone into `ComfyUI/custom_nodes`; no other node packs are needed):

| Repository | Provides |
|---|---|
| <https://github.com/Kosinkadink/ComfyUI-AnimateDiff-Evolved> | `ADE_*` AnimateDiff nodes |
| <https://github.com/comfyorg/comfyui-ipadapter> (fork of <https://github.com/cubiq/ComfyUI_IPAdapter_plus>) | `IPAdapter*` nodes |

Models (keep them outside Git):

| File | Folder inside `ComfyUI/` | Source |
|---|---|---|
| `v1-5-pruned-emaonly.safetensors` (4.3 GB) | `models/checkpoints` | <https://huggingface.co/stable-diffusion-v1-5/stable-diffusion-v1-5> |
| `v3_sd15_mm.ckpt` (1.7 GB) | `models/animatediff_models` | <https://huggingface.co/guoyww/animatediff> |
| `ip-adapter-plus_sd15.safetensors` (98 MB) | `models/ipadapter` | <https://huggingface.co/h94/IP-Adapter> (`models/`) |
| `CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors` (2.5 GB) | `models/clip_vision` | <https://huggingface.co/h94/IP-Adapter> (`models/image_encoder/model.safetensors`, **renamed**) |
| `control_v11f1e_sd15_tile.pth` (1.4 GB) | `models/controlnet` | <https://huggingface.co/lllyasviel/ControlNet-v1-1> |
| `control_v11p_sd15_openpose.pth` (1.4 GB) | `models/controlnet` | <https://huggingface.co/lllyasviel/ControlNet-v1-1> |

How AIRedraw works: the sprite is scaled up (nearest-neighbour) on a padded canvas with a flat background and used as the starting latent; **IPAdapter Plus** gets a tight
crop of the character as reference (so the character fills CLIP's view); **Tile ControlNet** (active for 60% of the steps) anchors structure and colours; **OpenPose ControlNet**
is driven by the same procedural skeleton as the rig; AnimateDiff v3 keeps frames coherent. Afterwards: majority-vote downsample into the source palette, key out the
background, then clip to the **rig's silhouette of the source sprite** so nothing outside the character survives.
Frame counts: AnimateDiff v3 is trained on 16-frame windows and the last frames of a shorter clip degrade, so AIRedraw generates `Tail Buffer Frames` (default 4) extra frames
after the animation and drops them; you always receive exactly the frames you asked for (cap: workflow slots 24 = N + buffer).
AIRedraw cannot keep pixel-level identity (see `Documentation~/benchmarks.md`).

### ComfyUI process lifecycle (AI Pose generation and AIRedraw)

1. **Detect**: `GET /system_stats`. If ComfyUI answers, Unity uses it and **never stops it**.
2. **Start** (otherwise): launch the configured process hidden and mark it *owned by Unity*; wait until the API answers (default timeout 180 s; if the process exits, its last output lines are shown).
3. **One generation session**: upload, submit, poll `/history`, download frames; no restarts between frames.
4. **Import** frames, build the clip, update the Animator.
5. **Stop** only after that, and only if owned: Ctrl+C (graceful), then `taskkill /T /F` on that exact process tree after a grace period (10 s).
6. The same shutdown runs on failure, timeout, cancel, editor quit and script reload (reloads are blocked during a run).
7. **Crash safety**: PID, start time and owning Unity process are stored in `Library/AISpriteAnimation/comfyui.pid`. On the next editor start, if the owning editor is gone, exactly that
   process tree is killed. Other `python.exe` processes are never touched; Unity's asset-import workers ignore the file.

**Tools > AI Sprite Animation > Stop ComfyUI Started By Unity** is a manual safety net.

## 6. Configuration

Machine-specific (EditorPrefs; the window's *ComfyUI* section): ComfyUI Folder, Python/Launcher (empty = auto-detect `python_embeded`, `.venv`, `venv`, then `python`),
Launch Args (default `main.py --listen {host} --port {port}`), ComfyUI URL (default `http://127.0.0.1:8188`) and Port, Startup Timeout. A non-local URL is used as-is (never started).

Project-specific (`AIAnimationSettings` asset, commit it):

| Setting | Meaning |
|---|---|
| Mode (*Animation Method* in the window) | Rig (Recommended for Pixel Art, default), AI Pose + Rig (Beta) or AI Redraw (Experimental) |
| Pose Backend (SDPose), SDPose Model Name, Pose Candidates (4), Pose Video Width/Height (384x576), Frames (16), Steps (16), Motion Scale (1.3), Good Enough / Min Quality, Pose Asset Folder | AI Pose + Rig: which backend makes the poses, the SDPose checkpoint, and how the motion videos are generated and judged |
| AI Pose Generation Size, Guidance, Sketch Variation, Evidence Weight | only for the legacy 1.2.0 backend and the procedural fallback (neither is AI motion) |
| Pose Cleanup (smoothing, passes, preserve degrees, step degrees, max degrees per frame, close loops, reject fraction) and its Constraints (enabled, limit scale, spike degrees, gait symmetry, foot lock, idle motion, frame rejection) | AI Pose + Rig: validation, constraints and smoothing applied when poses are generated, imported or re-applied (*Re-apply Constraints*) |
| Rig Asset Folder, Rig Neck/Hip Line, Leg Half Width, Arm Radius, Duplicate Legs | only used when a rig is auto-created; the rig itself (joints, parts, ground line) lives in `<Sprite>_Rig.asset` |
| Padding Percent (50), Crop To Used Bounds, Crop Margin | working-canvas padding and final framing |
| Facing | which way the sprite faces (Left mirrors everything; verified to be an exact mirror) |
| Output Root | where animations are written (default `Assets/AIAnimations`) |
| Sprite Binding Path | hierarchy path of the SpriteRenderer for the clip (empty = same object as the Animator) |
| Copy Import Settings From Source | on: PPU, filter, compression, pivot from the source sprite. off: *Pixels Per Unit* / *Filter Mode* below |
| Workflow, model names, Generation Size (768), Tail Buffer Frames (4), Noise Type, Key Tolerance, Snap To Source Palette, Speckle/Fringe cleanup, Mask Radius | AIRedraw |
| Presets | per animation: frames, fps, loop, pose kind, *Rig Intensity*, and the AI Redraw sampler weights |

Example for a 16 PPU pixel-art game: *Copy Import Settings From Source* off, *Pixels Per Unit* 16, *Filter Mode* Point, *Output Root* `Assets/Art/Generated`.

Add a preset: select the settings asset > *Presets* > `+` (name, frames, fps, loop, pose kind `idle|walk|run|attack|none`, rig intensity). It appears in the window's *Animation Type* list.
The right-click menu has fixed entries for the four defaults.

Workflow placeholders (all defined in `ComfyWorkflowBuilder`; no node IDs are hardcoded in C#): `__INPUT_IMAGE__ __REFERENCE_IMAGE__ __PROMPT__ __NEGATIVE_PROMPT__ __FRAME_COUNT__ __WIDTH__ __HEIGHT__ __FPS__ __SEED__ __STEPS__ __CFG__ __DENOISE__ __MOTION_SCALE__ __NOISE_TYPE__ __IPADAPTER_WEIGHT__ __IPADAPTER_PRESET__ __TILE_STRENGTH__ __TILE_END__ __TILE_CONTROLNET_MODEL__ __POSE_STRENGTH__ __POSE_CONTROLNET_MODEL__ __CHECKPOINT__ __MOTION_MODEL__ __OUTPUT_PREFIX__ __POSE_00__ ... __POSE_23__`.
A placeholder that is a whole JSON string becomes a number when its value is numeric; unknown placeholders are an error. Bundled workflow: **AnimateDiffSprite v3** (`Workflows/AnimateDiffSprite.json`, 24 pose slots,
regenerated by `tools/build_workflow.py` in the repository). Use your own by assigning a TextAsset in the settings asset. The only ID setting is *Nodes > Output Node Id*.

## 7. Diagnostics and self-test

The window's **Diagnostics** section shows versions (package, Unity, workflow, ComfyUI, AnimateDiff-Evolved, IPAdapter) and checks ComfyUI, GPU, custom nodes, models and the workflow, with the exact file and
folder for anything missing. **Validate with ComfyUI** starts it if needed, checks through its API and stops it again.

**Test Full Pipeline** (window) / `-aiSelfTest 1` (batch): tests Rig mode; then verifies ComfyUI, nodes and models, starts ComfyUI if needed, runs a tiny real AIRedraw generation on a built-in sprite, verifies clip,
frame size and pivot, transparency and motion, deletes its temporary assets, and stops ComfyUI if Unity started it. Typical: 1 s (Rig) + about 60 s (AIRedraw).

## 8. Automation (batch mode)

```
Unity.exe -batchmode -nographics -projectPath <project> -executeMethod AISpriteAnimation.BatchRunner.Run ^
  -aiSource Assets/Art/hero.png -aiPreset Walk -aiFrames 8 -aiFps 12
```

Other arguments: `-aiComfyDir`, `-aiComfyUrl`, `-aiSeed`, `-aiController`, `-aiTimeout <s>`, `-aiCancelAfter <s>`, `-aiDiagnostics live|offline`, `-aiSelfTest 1`,
`-aiOverride "mode=ai,denoise=0.8,..."` (`mode=rig|pose|ai`, also `poseBackend`, `candidates`, `videoSteps`, `videoFrames`, `videoMotion`, `videoWidth`, `videoHeight`, `sdposeModel`, `goodEnough`, `minQuality`, `smoothing`, `smoothPasses`, `stepDegrees`, `checkpoint`, and `poseGuidance`, `poseSize`, `sketchVariation`, `evidenceWeight` for the legacy backend), `-aiWorkflow <file>`, `-aiRigJoints <file> [-aiRigOnly 1]` (build/refresh a sprite's rig from a joints file with lines like `Neck 26 18.5` and `ground 47`), `-aiRigImport <rig.json|folder|auto> [-aiRigOnly 1]` (import generated rig data, see *Generated rig data*; `auto` = `rig/rig.json` next to the sprite; exit code 1 if it does not fit the sprite). In batch mode a missing rig is created automatically. Exit code 0 = success, 1 = failure, 2 = cancelled.

AI Pose + Rig: `-aiMode pose` (or `-aiOverride mode=pose`), `-aiPoseBackend sdpose|legacy|procedural`, then `-aiPoseAction generate` (run the AI, save poses, stop; `-aiPoseDump <folder>` writes the generated video frames and the SDPose keypoints of every candidate),
`build` (default in this mode: saved poses only, never starts ComfyUI), `both` (regenerate, then build), `preview` (render a saved pose asset to `-aiPoseSheet <png>` with or without `-aiPoseBones 0`, export `-aiPoseExportJson <file>`),
`reclean` (re-apply the constraints to the stored AI poses, no ComfyUI), `importjson` / `openpose` with `-aiPoseFile <json>` (import pose JSON / OpenPose keypoint JSON; exit code 3 = rejected by validation).

## 9. Architecture and extending

Unity editor code only (`Editor/`, assembly `AISpriteAnimation.Editor`); no runtime code, no models in the repository.
`RigDefinition` / `SpriteRigAsset` (joints + part map, optional underlay and leg swing scale), `ChargenRigImporter` (generated rig data -> rig asset), `RigAnimator` (poses, `IRigPoseProvider`) and `SpriteRig` (hierarchy renderer) form the Rig method; `SkeletonPoses` draws the OpenPose images for AI Redraw; the AI Pose layers are `IPoseGenerator` with `SDPosePoseGenerator` (AI), `ProceduralPoseGenerator` and `SketchEvidencePoseGenerator` (neither is AI), `AIPoseGenerator` (orchestration: validate, measure the AI contribution, save, stop ComfyUI), `ComfyUIWorkflowRunner`, `SDPoseModel` / `SDPoseModelInstaller`, `SDPoseAnalysis`, `OpenPoseTracking`, `OpenPoseMapper` / `OpenPoseKeypoints`, `MotionCycleExtractor`, `PoseContribution`, `PoseValidator` / `PoseSmoother` / `PoseCleanup` / `PoseCleanupSettings`, `AIPoseAsset`, `AIPoseProvider`, `AIPoseImporter`, `RigKinematics`, `PosePreviewPlayer`, and for the legacy backend `PoseSketch` and `RigPoseFitter`; `SpriteFrameProcessor` does canvas, palette, masking, crop and pivot; `SpriteAnimationImporter` imports and builds clips;
`IAIAnimationBackend` / `ComfyUIAnimationBackend` / `ComfyUIClient` / `ComfyUIProcessManager` are the ComfyUI side. Add a pose style in `SkeletonPoses`; another model or service behind `IAIAnimationBackend`.

## Troubleshooting

| Message | Meaning |
|---|---|
| *ComfyUI could not be started or is not reachable at ...* | AIRedraw only. Followed by the reason and ComfyUI's last output lines. Check the *ComfyUI Folder*. |
| *ComfyUI rejected the workflow* | Lists the failing nodes (missing node pack or model file). Run Diagnostics > Validate with ComfyUI. |
| A part moves with the wrong pixels (Rig) | Open the Sprite Rig Editor: move the joints and paint the parts. |
| Gaps or ghosting where an arm swings away (Rig) | Paint the arm pixels as arm; the vacated spot is filled with the surrounding body colour. |
| Out of GPU memory (AIRedraw) | Use *Generation Size* 512. |
| *SDPose model is required for AI Pose + Rig* | The checkpoint is not in ComfyUI/models/checkpoints. Use *Install / Download Model* in the window, or see `Documentation~/sdpose-model.md`. Nothing falls back to another backend. |
| *SDPose did not find usable walk motion in the generated videos* | None of the generated clips contained a clean, side-view movement (AnimateDiff on SD 1.5 fails on about half of the seeds, more for Attack). Nothing was saved. Generate again, or raise *Pose Candidates*. |
| *There are no saved AI poses ... Click Generate Poses first* | AI Pose + Rig builds from a pose asset; run *Generate Poses* (or *Import JSON...*) once for that sprite and animation type. |
| *The AI pose sequence was rejected by validation* | Too many values of the AI/imported poses were invalid. Nothing was saved. Generate again (another seed), or relax *Reject Repair Fraction*. |
| *AI frames contain N% of the sketch's silhouette motion* | Legacy backend only: the AI hardly moved the sprite, so the saved poses follow the procedural sketch. Use the SDPose backend for AI-generated motion. |

Uploaded sprites remain in `ComfyUI/input/unity_ai_animation/` (the API cannot delete them). License: MIT.
