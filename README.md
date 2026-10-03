# AI-Sprite-Animation

Unity package **`com.limpo.ai-sprite-animation`**: generate 2D sprite animations (Idle / Walk / Run / Attack) from one sprite, inside the Unity editor.
Default **Rig (Recommended for Pixel Art)** keeps the character pixel-exact; **AI Pose + Rig (Beta)** lets AI generate only the *motion* (AnimateDiff video + SDPose pose estimation through a local ComfyUI, saved as reusable pose data) while the rig still draws the original pixels;
**AI Redraw (Experimental)** redraws frames with AnimateDiff. No cloud services, no models in this repository.

**Taking over this project? Read [`package/Documentation~/HANDOVER.md`](package/Documentation~/HANDOVER.md) first.** The package lives in [`package/`](package/). Full documentation: [`package/README.md`](package/README.md). Benchmarks: [`package/Documentation~/benchmarks.md`](package/Documentation~/benchmarks.md).

## Install (Unity 6000.6+)

Package Manager > **+** > *Install package from git URL*:

```
https://github.com/<YOUR-GITHUB-USER>/AI-Sprite-Animation.git?path=/package#v1.0.0
```

Local development: `"com.limpo.ai-sprite-animation": "file:../../AI-Sprite-Animation/package"` in `Packages/manifest.json`.

Then: **Tools > AI Sprite Animation > Generate Animation**, or right-click a sprite > **AI > Generate Animation > Walk**.

## Releasing

```
# bump "version" in package/package.json and add an entry to package/CHANGELOG.md, then:
git commit -am "Release 1.0.1"
git tag v1.0.1
git push origin main --tags
```

Consumers update by changing the tag in their `manifest.json` URL.

## Layout

```
package/            Unity package (Editor code, Workflows/, Documentation~/, package.json, README, CHANGELOG)
tools/              build_workflow.py (regenerates the workflow's pose slots), posetests/ (console tests of the pose layers), riglab/, eval_identity.py
```

License: MIT.
