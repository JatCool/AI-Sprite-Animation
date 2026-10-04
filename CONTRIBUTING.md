# Contributing

Thanks for helping. Issues and pull requests are welcome; contributions are accepted under the repository's [MIT License](LICENSE).

**Read [`package/Documentation~/HANDOVER.md`](package/Documentation~/HANDOVER.md) first.** It explains the context, the decisions behind the design, the code map,
the conventions (coordinates, angle signs, pixel-art rules), what to update and when, and how everything was tested.

## Layout

| Path | What |
|---|---|
| `package/` | the Unity package `com.limpo.ai-sprite-animation` (editor code only, `Editor/`, assembly `AISpriteAnimation.Editor`) |
| `tools/` | development tools, not part of the package (console tests, rig lab, workflow generator, identity metrics) |
| `docs/` | repository-level guides (Claude Code, licenses) |
| `integrations/claude-code/` | the Claude Code skill template |

## Develop and test

The rig, pose and constraint layers are pure math on arrays on purpose, so they can be tested in seconds **without opening Unity**:

```bash
dotnet build -c Release -o tools/posetests/bin tools/posetests
tools/posetests/bin/posetests.exe <src.raw> tools/riglab/example_player_joints.txt
```

`src.raw` is a sprite in the small raw format written by `tools/riglab/riglab.py` (int32 width, int32 height, then RGBA rows bottom-up).
The two console projects reference `UnityEngine.CoreModule.dll` from a Unity 6000.6 install (edit the `HintPath` in `tools/*/*.csproj` if yours is elsewhere), and several
scripts in `tools/` hold machine-specific paths at the top (see [`tools/README.md`](tools/README.md)). Everything that needs the editor is checked by opening a
project that references the package (`"com.limpo.ai-sprite-animation": "file:../../AI-Sprite-Animation/package"`) and, for automation, by the batch mode (`-aiSelfTest 1`).

## Guidelines

* **The package stays game-agnostic**: no game names, paths or namespaces. Settings that differ per project belong in the settings asset.
* **Pixel art is sacred**: nearest-neighbour or majority vote only, hard 0/255 alpha, colours only from the source palette. Never add smoothing, anti-aliasing or interpolation to the Rig path.
* **Never commit models** (`*.safetensors`, `*.ckpt`, `*.pth`, ...); the `.gitignore` blocks them. Downloads happen only when the user asks for them.
* **No silent fallbacks** between pose backends, and procedural motion is never labelled as AI motion.
* **Unity metas:** every package file needs its `.meta` committed (checklist in HANDOVER section 11).
* One class per file, PascalCase public / camelCase private, tuning values on the settings asset and not hard-coded.
* Add or extend console tests for pose/rig logic (`tools/posetests`), and look at rendered frames at 5-8x zoom for anything visual.

## A change that users can see

1. Update `package/README.md` (behaviour), `package/CHANGELOG.md` and the version in `package/package.json` ([SemVer](https://semver.org/)); update `HANDOVER.md` if architecture, conventions, test status or limitations change.
2. Run the console tests and the checks in HANDOVER section 11.
3. A release is a version bump, a changelog entry and a tag (`vX.Y.Z`); consumers update by changing the tag in their `manifest.json` URL.

Adding an animation type has its own checklist: HANDOVER section 8, "Adding an animation type".
