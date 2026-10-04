---
name: sprite-animation
description: Create, generate, regenerate or rig sprite animations (walk, run, idle, attack, jump, crouch, rotate/turn around, or any 2D character animation) in this Unity project, and do it without asking for approval first. Use whenever the user wants an animation to exist, in any phrasing: a command ("create a walk animation"), a wish ("I would like to add a double jump animation for the player"), a description of how the character should move, or a request to rig/animate a sprite. This project uses the reusable package com.limpo.ai-sprite-animation (Rig method = default, pixel-exact, no AI). Always use that package and extend it when an animation type is missing; never write a new animation system in the game.
---

<!--
TEMPLATE. Copy this folder to <your project>/.claude/skills/sprite-animation/ (or ~/.claude/skills/sprite-animation/ for all projects)
and replace every <PLACEHOLDER> below. Delete this comment. Full guide: docs/claude-code.md in the AI-Sprite-Animation repository.

  <UNITY_EXE>      full path of Unity.exe 6000.6 (Windows) or the Unity binary
  <PROJECT_PATH>   absolute path of the Unity project
  <PACKAGE_REPO>   where the package repository is checked out (only if the package is a local "file:" dependency; else delete the sentence)
  <SPRITE_PATH>    an example sprite of your project, e.g. Assets/Art/Player/player.png
  <CONTROLLER>     the Animator Controller to wire clips into, e.g. Assets/Animations/Player/Player.controller (delete if you never want that)
  <OUTPUT_ROOT>    the "Output Root" of your AIAnimationSettings asset (package default Assets/AIAnimations)
-->

# Sprite animation in this project

This project already has a complete sprite animation system: the Unity package `com.limpo.ai-sprite-animation` (source: <PACKAGE_REPO>).
Your job is to **operate** it, and to fix or extend the **package** when something is missing. Never reinvent it inside the game.

## Autonomy contract

When the user says an animation should exist ("we need a hurt animation", "can you make the player roll?") the skill starts and finishes the work.
No menus of options, no "shall I extend the package?", no waiting for a yes. Decide the sprite, preset, frames, fps and loop yourself, say what you decided in one or two sentences, build it, verify it, report.
Ask only when you cannot know **which character** (several sprites and no context), when the user gave no action at all ("animate this"), or when art is missing (see below).

* A **missing animation type** is not a blocker: add it to the package (checklist "Adding an animation type" in the package's `Documentation~/HANDOVER.md`: pose function, default preset, menu entry, tests, docs), then generate it.
* **Missing art** (e.g. Rotate needs the character's own front view) is the only thing that may stop you, and only after you looked in the project and in the images the user shared. Never draw or AI-generate replacement art unless the user asks. Send ONE message naming the exact file to add.

## Approval loop

A generated clip is a **draft**. Report it as "version N (draft)" and end with one line: "approve, or what looks wrong?". If the user says it looks wrong, regenerate **without asking what to do**: look at the rendered frames at 5-8x zoom, name the concrete defects, change the pose function / preset / rig in the package, regenerate, re-verify, and say what changed. Each round must differ visibly from the last. Do not commit or move on until the user approves.

## Hard rules

1. Do not write C# animation code, a second rig system, a ComfyUI client or a frame-slicing script in the game. Extend the **package**.
2. **Default method = Rig** (the sprite's own pixels move through a bone hierarchy: pixel-exact, about 1 s, no ComfyUI, no GPU). Use *AI Pose + Rig* (beta) or *AI Redraw* (experimental) only when the user explicitly asks for them.
3. Never start ComfyUI for a Rig animation, or for building from poses that are already saved.
4. Never modify the source sprite, replace the Animator Controller or overwrite hand-made clips/states unless the user explicitly asks.
5. Never download a model (SDPose is 1.92 GB) without the user's approval; never present procedural poses as AI motion; never silently fall back to another backend.
6. Keep pixel art pixel art: Point filter, no compression, original palette, hard alpha, stable pivot. Never introduce bilinear filtering or anti-aliasing.
7. Inspect first, then act. Do not create duplicate assets.

## Facts (verify quickly, do not assume)

| Thing | Where |
|---|---|
| Package | `com.limpo.ai-sprite-animation` (docs: its `README.md`, `Documentation~/HANDOVER.md`, `Documentation~/ai-pose-rig.md`) |
| Rig of a sprite | `<Sprite>_Rig.asset` next to the sprite |
| Saved AI poses (only AI Pose + Rig) | `<Sprite>_<Animation>_AIPose.asset` next to the sprite |
| Project settings | `Assets/AISpriteAnimation/AIAnimationSettings.asset` (or wherever this project keeps it): output root, PPU, filter, presets |
| Output | `<OUTPUT_ROOT>/<Sprite>/<Animation>/` with `input.png`, `frame_###.png`, `<Sprite>_<Animation>.anim`; regenerating overwrites in place |
| Presets (frames @ fps) | Idle 8 @ 12 loop, Walk 8 @ 12 loop, Run 8 @ 14 loop, Attack 10 @ 12 one-shot, Jump 10 @ 12, Rotate 18 @ 12, Sit 8 @ 12, Crouch 6 @ 12, CrouchWalk 8 @ 12 loop |
| Unity | `<UNITY_EXE>` |

## Workflow

### 1. Identify
* **Sprite:** the one named by the user, else the current selection or the open file, else the obvious one. Genuinely ambiguous -> ask one short question.
* **Animation:** map the words yourself ("sprint" -> Run, "hit/slash" -> Attack, "leap" -> Jump, "turn around" -> Rotate, "duck" -> Crouch). A new action (double jump, hurt, roll) means extending the package.
* **Frames/fps:** preset defaults unless the user says otherwise (2-24 frames). **Language:** answer in the user's language.

### 2. Inspect
1. Find the sprite and its `.meta` (PPU, filter, pivot, `spriteMode`). Do not change them.
2. Find its rig: `**/<SpriteName>_Rig.asset`; check `sourceAssetPath` inside points at this sprite.
3. List existing clips and the Animator Controller states; note which names exist.
4. Check whether output already exists in `<OUTPUT_ROOT>/<Sprite>/<Animation>/`.
5. **Editor state:** if `<PROJECT_PATH>/Temp/UnityLockfile` exists or Unity has the project open, do **not** start a second Unity on it.

### 3A. The sprite has a rig -> Rig animation

**Route 1, editor closed (run it yourself):**

```powershell
& "<UNITY_EXE>" -batchmode -nographics -projectPath "<PROJECT_PATH>" `
  -executeMethod AISpriteAnimation.BatchRunner.Run `
  -aiSource <SPRITE_PATH> -aiPreset Walk -aiController <CONTROLLER> `
  -logFile "$env:TEMP\sprite-animation.log"
```

Success = exit code 0 and `BATCH SUCCESS: <path>.anim` in the log. Add `-aiFrames N -aiFps N -aiSeed N` only if the user asked; omit `-aiController` if the Animator must stay untouched. On failure read the text after `BATCH FAILED` and report it.

**Route 2, editor open:** Claude cannot drive an open editor, so give the clicks as the answer: select the sprite, right-click > **AI > Generate Animation > Walk** (or *Tools > AI Sprite Animation > Generate Animation*), then ask the user to tell you when it is done and verify.

### 3B. The sprite has no rig
Rig works best for side-view humanoids. If the sprite is not like that (top-down, front view, blob, prop) say so and offer AI Redraw or a manual approach. Otherwise: right-click the sprite > **AI > Sprite Rig Editor** (Create Rig (auto), then drag the joints and paint the parts), or run a batch with `-aiRigJoints <joints.txt> -aiRigOnly 1` for a starting rig. If `rig/rig.json` from the Local Character Generator sits next to the sprite, the package imports it automatically (or `-aiRigImport auto -aiRigOnly 1`).

### 3C. AI Pose + Rig (beta, explicit request only)
The AI decides only how the character moves; every pixel still comes from the original sprite. Check the rig, ComfyUI and the SDPose model first. Generate poses (ComfyUI runs, 50-190 s): the same batch command with `-aiMode pose -aiPoseAction generate -aiPoseBackend sdpose -aiSeed 22`; build from the saved poses (no ComfyUI): `-aiMode pose -aiPoseAction build`. Read the printed AI-contribution level and validation numbers honestly, and look at the frames before calling it good. Do not loosen validation to get a result.

### 3D. AI Redraw (experimental, explicit request only)
Say it is slower, needs ComfyUI and VRAM, and is not pixel-exact; then add `-aiOverride "mode=ai"` to the batch command.

## Verify (every time, report the results)
1. `frame_000.png ...` count equals the requested frames; the `.anim` and its `.meta` exist.
2. Frame import settings: Point filter, the project's PPU, no compression, **one shared `spritePivot`** across all frames.
3. Clip: `m_SampleRate` = fps; loop correct (loop for Idle/Walk/Run/CrouchWalk, one-shot for Attack/Jump/Sit/Crouch/Rotate).
4. Animator (if used): hand-made states still there with their motions; no transitions changed.
5. The source sprite and its `.meta` are untouched (`git status`).
6. Pixels: only original colours, alpha only 0/255, no clipped limbs (`tools/eval_identity.py` in the package repository does this), then **look at the sheet**: same character in every frame, feet on one ground line, no detached parts.
7. Report: created/updated paths, frame count/fps/loop, Animator changes (or "untouched"), results, limitations; label it **version N (draft)** and ask for approval.

## Animator rules
* The package puts a clip into a state named after the animation only if that state is empty or already holds a generated clip; otherwise it creates `Walk (AI)` next to your hand-made `Walk`. It never adds or removes transitions. New transitions or parameters for gameplay are proposed separately and wait for approval.
* Rig logic never goes into gameplay scripts.

## If the package itself must change
Read the package's `Documentation~/HANDOVER.md` first (code map, conventions, what to update). Keep the package game-agnostic; update its `README.md`, `CHANGELOG.md` and version; extend `tools/posetests`. Do not commit unless the user asks.
