# Using AI Sprite Animation with Claude Code

You can use the package completely by hand (right-click a sprite > *AI > Generate Animation*), but it is also built so that
[Claude Code](https://claude.com/claude-code) can operate it for you: *"create a walk animation for the player"*, *"I would like
a double jump animation"*, *"the crouch looks ugly, make it deeper"*. This page explains how that works, how to install the
ready-made skill, how to write or adapt a skill yourself, and what to put into `CLAUDE.md`.

Contents: [What Claude does](#what-claude-does-for-you) - [Install the skill](#install-the-ready-made-skill) -
[CLAUDE.md](#claudemd-for-your-game) - [Example prompts](#example-prompts) - [How Claude drives Unity](#how-claude-drives-unity) -
[Create your own skill](#create-or-adapt-a-skill) - [Extend the package with Claude](#extending-the-package-with-claude) -
[Rules worth keeping](#rules-worth-keeping) - [Related generator](#related-the-local-character-generator)

## What Claude does for you

The package is **editor code with a command line**, which is exactly what an agent can use:

| Entry point | Used by |
|---|---|
| Right-click a sprite > *AI > Generate Animation > Walk*, *Tools > AI Sprite Animation* windows | you, in the open editor |
| `Unity -batchmode -executeMethod AISpriteAnimation.BatchRunner.Run -aiSource ... -aiPreset Walk` | Claude, when the editor is closed |
| Plain files: `<Sprite>_Rig.asset`, `<Sprite>_<Animation>_AIPose.asset`, `frame_###.png`, `.anim`, `.meta` | Claude, to inspect and verify |
| The package source (`Editor/`, `Documentation~/HANDOVER.md`) | Claude, to add an animation type or fix a defect |

With the skill installed, a request such as *"add a walk animation for `hero.png`"* makes Claude:

1. find the sprite and its rig (`hero_Rig.asset`), read the Animator Controller, and check whether the Unity editor has the project open;
2. run the **Rig** method (pixel-exact, about a second, no ComfyUI, no GPU), either headless or by giving you the exact clicks if your editor is open;
3. verify the result: frame count, one shared pivot, Point filter, original palette only, hard alpha, feet on one ground line, hand-made Animator states untouched;
4. report it as **version N (draft)** and ask for your verdict. If you say it looks wrong, it regenerates a visibly different version until you approve.

If an animation type does not exist yet (double jump, hurt, roll ...) Claude extends the **package** following the checklist in
[`HANDOVER.md`](../package/Documentation~/HANDOVER.md) ("Adding an animation type"), builds it and tells you what it assumed. It does not ask for permission first;
you judge the finished result.

## Install the ready-made skill

A *skill* is a Markdown file with instructions that Claude Code loads when your request matches its description.
A project-neutral template ships in this repository:
[`integrations/claude-code/sprite-animation/SKILL.md`](../integrations/claude-code/sprite-animation/SKILL.md).

1. **Install the package** in your Unity project ([README](../README.md#installation)).
2. **Copy the skill folder** to one of:

   | Location | Scope |
   |---|---|
   | `<your Unity project>/.claude/skills/sprite-animation/SKILL.md` | this project only (commit it, your team gets it too) |
   | `~/.claude/skills/sprite-animation/SKILL.md` | every project on your machine |

   ```bash
   mkdir -p <your-project>/.claude/skills
   cp -r integrations/claude-code/sprite-animation <your-project>/.claude/skills/
   ```
3. **Fill in the placeholders** (they are listed in the comment at the top of the file, delete the comment afterwards):

   | Placeholder | Example |
   |---|---|
   | `<UNITY_EXE>` | `C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe` |
   | `<PROJECT_PATH>` | `C:\Games\MyGame` |
   | `<PACKAGE_REPO>` | where you cloned this repository (only if you use a local `file:` dependency, otherwise delete the sentence) |
   | `<SPRITE_PATH>` | `Assets/Art/Hero/hero.png` |
   | `<CONTROLLER>` | `Assets/Animations/Hero/Hero.controller` |
   | `<OUTPUT_ROOT>` | the *Output Root* of your settings asset (default `Assets/AIAnimations`) |
4. **Start Claude Code in the project folder** and ask for an animation (examples below). Claude lists the skill by its name and description; typing `/sprite-animation` invokes it explicitly.

## CLAUDE.md for your game

`CLAUDE.md` in the project root is read at the start of every session. The skill holds the *procedure*; `CLAUDE.md` holds the *facts about your game* so Claude does not have to rediscover them. A minimal block:

```markdown
## Sprite animation
Sprite animation is done by the Unity package `com.limpo.ai-sprite-animation`. Do NOT write animation systems in this game: use the
`sprite-animation` skill (`.claude/skills/sprite-animation/SKILL.md`); extend or fix the package if something is missing.

- Method: **Rig** (the sprite's own pixels, pixel-exact, no ComfyUI). AI Pose + Rig and AI Redraw only when I ask for them.
- Pixel art: 16 PPU, Point filter, no compression. Never change the source sprite or its .meta.
- Rigs live next to the sprites (`<Sprite>_Rig.asset`). Settings: `Assets/Settings/AISpriteAnimation/AIAnimationSettings.asset`.
- Generated clips: `Assets/Animations/Generated/<Sprite>/<Animation>/`. Hand-made clips and Animator states stay untouched
  unless I say "replace".
- A generated animation is a draft until I approve it. If I say it looks wrong, regenerate a visibly better version.
```

Keep it short and true: only write what you verified, and update it when the way your game uses the package changes.

## Example prompts

* `create a walk animation for hero.png`
* `I would like to add a double jump animation for the hero` (a wish is enough: Claude adds the animation type to the package)
* `the crouch looks too shallow, make it deeper and slower` (a regeneration round)
* `rig enemy_goblin.png so it can be animated` (starts the rig workflow, tells you what to adjust in the Sprite Rig Editor)
* `let the AI generate how the hero walks` (AI Pose + Rig: Claude checks the model and ComfyUI, then generates poses and reports the AI contribution honestly)
* `rotate the character so it faces the camera for one second` (needs the character's own front sprite; Claude names the file if it is missing)
* Any language works; the skill answers in yours.

## How Claude drives Unity

* **Editor closed:** Claude starts `Unity.exe -batchmode -nographics ... -executeMethod AISpriteAnimation.BatchRunner.Run ...` itself and reads the log (`BATCH SUCCESS: ...`, exit code 0). All batch arguments are in the [package README](../package/README.md#8-automation-batch-mode).
* **Editor open:** Unity allows **one instance per project** (`Temp/UnityLockfile`). Claude then does the file work and gives you the exact clicks (right-click the sprite > *AI > Generate Animation > Walk*), and verifies after you say it is done. Claude can not click inside your open editor.
* **AI Pose + Rig** adds `-aiMode pose -aiPoseAction generate|build|preview|reclean`. Generating poses needs a local ComfyUI and the SDPose model; building from saved poses never starts ComfyUI.
* **Where results go:** `<Output Root>/<Sprite>/<Animation>/`, so Claude can verify frames, `.meta` pivots and the clip from plain files.

## Create or adapt a skill

A skill is a folder with one required file, `SKILL.md`: a short YAML header, then free-form instructions.

```markdown
---
name: sprite-animation
description: Create or regenerate sprite animations (walk, run, jump, ...) in this Unity project. Use whenever the user wants an animation to exist, in any phrasing.
---

# Sprite animation in this project
...instructions Claude should follow...
```

What matters, learned while building the skill that drives this package:

1. **The `description` decides when the skill loads.** Claude reads only `name` and `description` up front and loads the body when a request matches. List the trigger phrasings you expect: commands (*"create a walk animation"*), wishes (*"I would like a double jump"*), rig requests, other languages. A vague description means the skill never fires; a description that is too wide fires on unrelated requests.
2. **State the autonomy you want.** If you want Claude to start working immediately, say so ("never ask for approval before building; the user judges the draft"), and name the few cases where it must ask (several characters, missing art).
3. **Give facts, not hopes.** File locations, exact batch command lines, which preset means what, which assets are hand-made and must not be overwritten. Mark what Claude must verify instead of assuming.
4. **Write hard rules as short numbered statements** (no ComfyUI for a Rig animation, never download a model, never touch the source sprite, never overwrite hand-made states). They are what keeps an autonomous run safe.
5. **Add a verification checklist** that Claude runs every time and reports. It turns "it ran" into "it is correct".
6. **Add an approval loop** for anything visual. Automated checks prove a clip is technically sound, not that it looks good; make the skill report a draft and iterate on your feedback.
7. **Keep game-specific knowledge in the project's skill/`CLAUDE.md`, package knowledge in the package.** The template here is project-neutral on purpose.
8. **Test it:** ask for an animation in a fresh Claude Code session and watch the first run. Where Claude guesses, add the missing fact to the skill. Then try a deliberately underspecified request (*"animate this"*) and a wish-style one and check the behaviour matches the contract.

You can also let Claude write the first version: *"read `integrations/claude-code/sprite-animation/SKILL.md` and `package/README.md`, then create a skill for my project at `.claude/skills/...` with my paths"*, and then edit it.

### Add your own commands to the skill

| You want | Change |
|---|---|
| a new trigger phrase ("make the enemy flinch") | extend the `description:` line in the header |
| a new animation word -> preset mapping | add it to *Identify* in the skill |
| another verification | add a numbered item to *Verify* |
| a different Animator policy | edit *Animator rules* (and keep the hard rule about hand-made states) |
| another project | copy the folder, change the facts table and the batch command paths |

## Extending the package with Claude

The normal way to get a new animation is to ask for it. Claude adds, in the package: a pose function in `RigAnimator`, a default preset in `AIAnimationSettings` (registered in `AddedAfterFirstRelease` so existing projects receive it), a menu entry, console tests in `tools/posetests`, and the docs, then renders and inspects the frames. The checklist it follows is in [`HANDOVER.md`](../package/Documentation~/HANDOVER.md) (section 8, "Adding an animation type"). If you work on the package itself, point Claude at `package/Documentation~/HANDOVER.md` first.

## Rules worth keeping

These are written into the template skill; keep them when you adapt it:

* The default method is **Rig**: pixel-exact and local. AI methods only on explicit request.
* Never start ComfyUI for something that does not need it; never download the 1.92 GB SDPose model without your approval.
* Procedural motion is never labelled AI motion, and there is no silent fallback between pose backends.
* Source sprites and hand-made animations are never modified.
* A generated animation is a draft until you approve it.

## Related: the Local Character Generator

[JatCool/local-character-generator](https://github.com/JatCool/local-character-generator) generates the *static* pixel-art character from a text description (also with a Claude Code skill) and can write the rig data this package imports automatically (`rig/rig.json`, see *Generated rig data* in the [package README](../package/README.md#generated-rig-data-170-optional)). The two projects are independent: this package never depends on the generator, and the generator never changes this package.
