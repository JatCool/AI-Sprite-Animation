# AI Sprite Animation

Generate 2D sprite animations from a single sprite, inside the Unity editor.
Select a sprite, choose *Idle / Walk / Run / Attack*, and get imported frames and an `AnimationClip`
that look like **the same character** moving, in hard-edged pixel art, with the sprite's own transparency.

Two animation methods:

| Method | How frames are made | Identity | Needs ComfyUI | Time (RTX 3080) |
|---|---|---|---|---|
| **Rig (Recommended for Pixel Art)** - default | The sprite's own pixels are assigned to parts (head, hair, body, both arms, both legs, weapon) and moved through a bone hierarchy | **Pixel-exact**: same palette, details, face, weapon | No | about 1 s |
| **AI Redraw (Experimental)** | [ComfyUI](https://github.com/Comfy-Org/ComfyUI) + AnimateDiff + IPAdapter + OpenPose ControlNet redraw every frame | Approximate (silhouette yes, pixel detail no): slower, GPU-dependent, may alter face and clothing; for stylised sprites | Yes (started and stopped automatically) | about 90-120 s |

Why Rig is the default: `Documentation~/benchmarks.md` benchmarks AnimateDiff (IPAdapter strengths, FreeNoise, FreeInit, context windows) against the rig on
the same sprite and seed. No diffusion variant kept a 48x48 character's pixels (torso pixel match 0.10-0.29); the rig only moves original pixels, so palette and details are identical by construction.
The skeleton, not a text prompt, controls the movement in both modes.

```
Rig:       sprite + rig asset -> bone hierarchy moves the parts -> grounding -> majority-vote downsample -> crop + pivot -> sprites -> AnimationClip
AIRedraw:  sprite -> padded canvas + reference + pose images -> ComfyUI (AnimateDiff) -> frames -> palette snap + source silhouette mask -> same tail
```

## 1. Install the package

Unity **6000.6** or newer. Developed and tested on **6000.6.0f1** only; older versions are not claimed to work (the minimum in `package.json` equals the tested version).

**From Git** (Unity: *Window > Package Manager > + > Install package from git URL*):

```
https://github.com/<YOUR-GITHUB-USER>/AI-Sprite-Animation.git?path=/package#v1.0.0
```

or in `Packages/manifest.json`:

```json
"com.limpo.ai-sprite-animation": "https://github.com/<YOUR-GITHUB-USER>/AI-Sprite-Animation.git?path=/package#v1.0.0"
```

**From a local folder** (development; relative to the project's `Packages/` folder):

```json
"com.limpo.ai-sprite-animation": "file:../../AI-Sprite-Animation/package"
```

**Updating:** change the tag in the URL (`#v1.0.1`, `#v1.1.0`, ...) or remove the `#...` part to follow the default branch, then let Unity resolve the package.
Versions follow [SemVer](https://semver.org/) (see `CHANGELOG.md`): patch = fixes, minor = backwards-compatible features, major = breaking changes to settings or workflow.
Your project's settings asset keeps working across patch/minor updates; new fields get package defaults.

After installing you get **Tools > AI Sprite Animation > Generate Animation** (window) and
**right-click a sprite > AI > Generate Animation > Idle / Walk / Run / Attack**.
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

* **Frame count**: exactly the requested number of frames, 2 to 24 (AIRedraw also generates a few extra frames and drops them, see below). Defaults: Idle 8 @ 12 fps, Walk 8 @ 12, Run 8 @ 14, Attack 10 @ 12.
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

Idle, Walk, Run and Attack are procedural pose sequences (rotations per part plus root movement):

* **Walk / Run**: alternating legs with knee flex, counter-swinging arms with elbow bend, forward lean, hair lag; the weapon partly cancels arm swing so a held sword stays steady. Run has a flight phase (intentional hop).
* **Attack**: anticipation (lean back, weapon raised) -> swing -> follow-through -> recovery, with a step forward. One-shot; both endpoints are sampled.
* **Idle**: breathing, weight shift, hair and weapon settle.
* Presets scale all rotations with *Rig Intensity*. Frame count is free (2-24); loops sample exactly one cycle.

**Grounding:** every frame the lowest foot pixel is placed on the rig's ground line, so feet never drift; the body height follows from the legs (and `hop` for runs/jumps).
Only intentional vertical movement (`hop`) leaves the ground line. Translations are snapped to whole pixels.

**Pixel art:** parts are composed at 4x and reduced by majority vote per pixel, so the output contains only colours that exist in the source, with hard edges, no blending
and no anti-aliasing. Where an arm swings away from the torso, the vacated spot is filled with the surrounding body colour. Frames are cropped to the union of all used pixels
with the source pivot mapped identically into every frame.

### Extending: AI Pose + Rig (future)

Poses come through `IRigPoseProvider` (`RigPose[] GetPoses(animation, frameCount, cycle, intensity)`); the procedural provider is one implementation. A future mode can implement the interface
with poses estimated from an AI generation (e.g. AnimateDiff + OpenPose output): the rig renderer applies them to the original pixels unchanged.

## 4. AIRedraw mode: ComfyUI setup

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

### ComfyUI process lifecycle (AIRedraw only)

1. **Detect**: `GET /system_stats`. If ComfyUI answers, Unity uses it and **never stops it**.
2. **Start** (otherwise): launch the configured process hidden and mark it *owned by Unity*; wait until the API answers (default timeout 180 s; if the process exits, its last output lines are shown).
3. **One generation session**: upload, submit, poll `/history`, download frames; no restarts between frames.
4. **Import** frames, build the clip, update the Animator.
5. **Stop** only after that, and only if owned: Ctrl+C (graceful), then `taskkill /T /F` on that exact process tree after a grace period (10 s).
6. The same shutdown runs on failure, timeout, cancel, editor quit and script reload (reloads are blocked during a run).
7. **Crash safety**: PID, start time and owning Unity process are stored in `Library/AISpriteAnimation/comfyui.pid`. On the next editor start, if the owning editor is gone, exactly that
   process tree is killed. Other `python.exe` processes are never touched; Unity's asset-import workers ignore the file.

**Tools > AI Sprite Animation > Stop ComfyUI Started By Unity** is a manual safety net.

## 5. Configuration

Machine-specific (EditorPrefs; the window's *ComfyUI* section): ComfyUI Folder, Python/Launcher (empty = auto-detect `python_embeded`, `.venv`, `venv`, then `python`),
Launch Args (default `main.py --listen {host} --port {port}`), ComfyUI URL (default `http://127.0.0.1:8188`) and Port, Startup Timeout. A non-local URL is used as-is (never started).

Project-specific (`AIAnimationSettings` asset, commit it):

| Setting | Meaning |
|---|---|
| Mode (*Animation Method* in the window) | Rig (Recommended for Pixel Art, default) or AI Redraw (Experimental) |
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

## 6. Diagnostics and self-test

The window's **Diagnostics** section shows versions (package, Unity, workflow, ComfyUI, AnimateDiff-Evolved, IPAdapter) and checks ComfyUI, GPU, custom nodes, models and the workflow, with the exact file and
folder for anything missing. **Validate with ComfyUI** starts it if needed, checks through its API and stops it again.

**Test Full Pipeline** (window) / `-aiSelfTest 1` (batch): tests Rig mode; then verifies ComfyUI, nodes and models, starts ComfyUI if needed, runs a tiny real AIRedraw generation on a built-in sprite, verifies clip,
frame size and pivot, transparency and motion, deletes its temporary assets, and stops ComfyUI if Unity started it. Typical: 1 s (Rig) + about 60 s (AIRedraw).

## 7. Automation (batch mode)

```
Unity.exe -batchmode -nographics -projectPath <project> -executeMethod AISpriteAnimation.BatchRunner.Run ^
  -aiSource Assets/Art/hero.png -aiPreset Walk -aiFrames 8 -aiFps 12
```

Other arguments: `-aiComfyDir`, `-aiComfyUrl`, `-aiSeed`, `-aiController`, `-aiTimeout <s>`, `-aiCancelAfter <s>`, `-aiDiagnostics live|offline`, `-aiSelfTest 1`,
`-aiOverride "mode=ai,denoise=0.8,..."`, `-aiWorkflow <file>`, `-aiRigJoints <file> [-aiRigOnly 1]` (build/refresh a sprite's rig from a joints file with lines like `Neck 26 18.5` and `ground 47`). In batch mode a missing rig is created automatically. Exit code 0 = success, 1 = failure, 2 = cancelled.

## 8. Architecture and extending

Unity editor code only (`Editor/`, assembly `AISpriteAnimation.Editor`); no runtime code, no models in the repository.
`RigDefinition` / `SpriteRigAsset` (joints + part map), `RigAnimator` (poses, `IRigPoseProvider`) and `SpriteRig` (hierarchy renderer) form the Rig method; `SkeletonPoses` draws the OpenPose images for AI Redraw; `SpriteFrameProcessor` does canvas, palette, masking, crop and pivot; `SpriteAnimationImporter` imports and builds clips;
`IAIAnimationBackend` / `ComfyUIAnimationBackend` / `ComfyUIClient` / `ComfyUIProcessManager` are the ComfyUI side. Add a pose style in `SkeletonPoses`; another model or service behind `IAIAnimationBackend`.

## Troubleshooting

| Message | Meaning |
|---|---|
| *ComfyUI could not be started or is not reachable at ...* | AIRedraw only. Followed by the reason and ComfyUI's last output lines. Check the *ComfyUI Folder*. |
| *ComfyUI rejected the workflow* | Lists the failing nodes (missing node pack or model file). Run Diagnostics > Validate with ComfyUI. |
| A part moves with the wrong pixels (Rig) | Open the Sprite Rig Editor: move the joints and paint the parts. |
| Gaps or ghosting where an arm swings away (Rig) | Paint the arm pixels as arm; the vacated spot is filled with the surrounding body colour. |
| Out of GPU memory (AIRedraw) | Use *Generation Size* 512. |

Uploaded sprites remain in `ComfyUI/input/unity_ai_animation/` (the API cannot delete them). License: MIT.
