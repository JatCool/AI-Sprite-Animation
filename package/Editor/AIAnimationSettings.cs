using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>One animation type (Idle, Walk, ...). Add entries to the settings asset to add presets.</summary>
    [Serializable]
    public class AnimationPreset
    {
        public string name = "Walk";
        [Tooltip("Number of frames in the final animation (sampled from the generated frames).")]
        [Min(2)] public int frames = 8;
        [Min(1)] public int fps = 12;
        public bool loop = true;
        [TextArea(1, 3)] public string prompt = "";
        [TextArea(1, 3)] public string negativePrompt = "";
        [Tooltip("Procedural skeleton animation driving the OpenPose ControlNet: idle, walk, run, attack or none (no pose guidance).")]
        public string poseKind = "walk";
        [Range(0f, 2f)] public float poseStrength = 1.4f;
        [Min(1)] public int steps = 20;
        [Min(0f)] public float cfg = 7f;
        [Tooltip("Lower = closer to the source sprite, higher = more freedom to move.")]
        [Range(0.05f, 1f)] public float denoise = 0.85f;
        [Range(0f, 1.5f)] public float ipAdapterWeight = 1.0f;
        [Tooltip("Strength of the Tile ControlNet that anchors the source sprite's structure and colours.")]
        [Range(0f, 1.5f)] public float controlNetStrength = 0.5f;
        [Tooltip("How long (fraction of the sampling steps) the Tile ControlNet stays active. Longer = colours and structure stay closer to the sprite.")]
        [Range(0.05f, 1f)] public float tileEndPercent = 0.6f;
        [Tooltip("Rig mode: scales all rotations of the animation (1 = as designed; lower for subtler movement).")]
        [Range(0.1f, 1.5f)] public float rigIntensity = 1f;
        [Tooltip("AnimateDiff motion module strength.")]
        [Range(0f, 2f)] public float motionScale = 1f;
    }

    /// <summary>The only place that refers to ComfyUI node IDs. The workflow itself uses __PLACEHOLDER__ tokens.</summary>
    [Serializable]
    public class ComfyNodeMap
    {
        [Tooltip("ID of the PreviewImage/SaveImage node whose images are the animation frames. Empty = use every image output.")]
        public string outputNodeId = "";
    }

    public enum SpriteFacing { Right, Left }

    /// <summary>
    /// How frames are produced.
    /// Rig (recommended for pixel art): the source sprite's own pixels are assigned to body parts (head, hair, body, both arms, both legs, weapon)
    /// and moved by a bone hierarchy. Pixel-exact identity, the sprite's own alpha, fast, no AI model needed.
    /// AIRedraw (experimental): AnimateDiff redraws every frame; slower, GPU-dependent, changes pixel-level details (face, clothes). For stylised sprites.
    /// </summary>
    public enum AnimationMode { Rig, AIRedraw }

    public static class AnimationModeLabels
    {
        public static readonly string[] Labels = { "Rig (Recommended for Pixel Art)", "AI Redraw (Experimental)" };
    }

    /// <summary>
    /// Project-level settings asset (created on first use, one per Unity project). Everything game-specific lives here:
    /// output folder, pixel-art import conventions, animation binding path, presets.
    /// </summary>
    public class AIAnimationSettings : ScriptableObject
    {
        public const string DefaultAssetPath = "Assets/AISpriteAnimation/AIAnimationSettings.asset";

        [Header("Mode")]
        [Tooltip("Rig = move the source sprite's own pixels with the skeleton (exact identity, no ComfyUI needed). AIRedraw = let AnimateDiff redraw each frame (needs ComfyUI; identity is approximate).")]
        public AnimationMode mode = AnimationMode.Rig;

        [Header("Rig (Rig mode)")]
        [Tooltip("Folder for generated rig assets. Empty = next to the source sprite (Hero.png -> Hero_Rig.asset).")]
        public string rigAssetFolder = "";
        [Tooltip("Only used when a rig is created automatically (Auto-create): neck position as a fraction of the character's height from the top. Refine in the Sprite Rig Editor.")]
        [Range(0.05f, 0.6f)] public float rigNeckLine = 0.33f;
        [Tooltip("Auto-create: hip position as a fraction of the character's height from the top.")]
        [Range(0.3f, 0.85f)] public float rigHipLine = 0.65f;
        [Tooltip("Auto-create: half width of the leg column (fraction of height). Pixels further out in the leg band (weapon tip, cape) stay with the body.")]
        [Range(0.03f, 0.4f)] public float rigLegHalfWidth = 0.12f;
        [Tooltip("Auto-create: distance from the arm lines within which pixels count as arm (fraction of height).")]
        [Range(0.01f, 0.15f)] public float rigArmRadius = 0.05f;
        [Tooltip("Auto-create: leg pixels belong to both legs (full-width overlapping legs) instead of being split down the middle.")]
        public bool rigDuplicateLegs = true;

        [Header("Workflow (AIRedraw mode)")]
        [Tooltip("AIRedraw: generated frames are clipped to the posed source sprite's silhouette grown by this many pixels, so stray blobs and background bleed never reach the final frames.")]
        [Range(0, 8)] public int aiMaskRadius = 3;
        [Tooltip("Empty = the workflow shipped with the package.")]
        public TextAsset workflow;
        public ComfyNodeMap nodes = new ComfyNodeMap();

        [Header("Models (file names as shown in ComfyUI)")]
        public string checkpointName = "v1-5-pruned-emaonly.safetensors";
        public string motionModelName = "v3_sd15_mm.ckpt";
        public string tileControlNetName = "control_v11f1e_sd15_tile.pth";
        public string poseControlNetName = "control_v11p_sd15_openpose.pth";
        public string ipAdapterPreset = "PLUS (high strength)";
        public string ipAdapterModelFile = "ip-adapter-plus_sd15.safetensors";
        public string clipVisionFile = "CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors";

        [Header("Generation")]
        [Tooltip("Working resolution sent to ComfyUI (multiple of 8). 768 gives clean results for 48px sprites on a 10 GB GPU (~100 s per animation); 512 is ~3x faster but noisier.")]
        public int generationSize = 768;
        [Tooltip("Empty space added around the sprite on the AI working canvas, per side, as a percentage of the sprite's size. 50 = the canvas is 2x the sprite. Gives swinging limbs room so nothing is clipped.")]
        [Range(10, 100)] public float paddingPercent = 50f;
        [Tooltip("Crop the generated frames to the union of all pixels any frame uses (plus the original sprite rect), so every frame has the same size and no limb is clipped. Off = keep the whole padded canvas.")]
        public bool cropToUsedBounds = true;
        [Tooltip("Extra empty pixels kept around the used bounds when cropping.")]
        [Min(0)] public int cropMarginPixels = 1;
        [Tooltip("Extra frames generated after the animation and then dropped. AnimateDiff v3 is trained on 16-frame windows and its last frames of a shorter clip degrade; the buffer absorbs that so all N delivered frames are clean. Capped by the workflow's 24 frame slots.")]
        [Range(0, 8)] public int tailBufferFrames = 4;
        [Tooltip("AnimateDiff noise: 'constant' re-uses the same noise on every frame (least shimmer); 'default' is independent noise per frame (more variation); 'FreeNoise' and 'repeated_context' are the other options of ComfyUI-AnimateDiff-Evolved.")]
        public string noiseType = "default";
        [Tooltip("Which way the source sprite faces. Pose guidance is mirrored for Left.")]
        public SpriteFacing facing = SpriteFacing.Right;
        [TextArea(1, 3)] public string basePrompt = "pixel art game character sprite, side view, flat colors, plain white background";
        [TextArea(1, 3)] public string baseNegativePrompt = "blurry, deformed, text, extra limbs, different character, 3d, photo";

        [Header("Output")]
        [Tooltip("Generated animations go to <Output Root>/<Sprite>/<Animation>/.")]
        public string outputRoot = "Assets/AIAnimations";
        [Tooltip("Hierarchy path of the SpriteRenderer inside the Animator object. Empty = same object as the Animator.")]
        public string spriteBindingPath = "";

        [Header("Sprite import of generated frames")]
        [Tooltip("On: pixels-per-unit, filter mode, compression, pivot are copied from the source sprite. Off: the values below are used.")]
        public bool copyImportSettingsFromSource = true;
        public float pixelsPerUnit = 100f;
        public FilterMode filterMode = FilterMode.Bilinear;

        [Header("Frame post-processing")]
        [Tooltip("Colour distance (0-441) under which edge-connected background pixels become transparent.")]
        [Range(1, 200)] public float keyTolerance = 70f;
        [Tooltip("How many times to peel off edge pixels that are still close to the background colour (removes light halos).")]
        [Range(0, 4)] public int fringeCleanupPasses = 2;
        [Tooltip("Snap generated colours to the source sprite's palette (when it has <= 256 colours).")]
        public bool snapToSourcePalette = true;
        [Tooltip("Remove opaque specks smaller than this many pixels that are not near the character (0 = off). Small parts attached to or close to the body are kept.")]
        [Min(0)] public int removeSpecklesBelow = 12;

        [Header("Timeouts (seconds)")]
        public int generationTimeoutSeconds = 900;

        [Header("Presets")]
        public List<AnimationPreset> presets = CreateDefaultPresets();

        public AnimationPreset FindPreset(string presetName)
        {
            foreach (var p in presets)
                if (string.Equals(p.name, presetName, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        public string[] PresetNames()
        {
            var names = new string[presets.Count];
            for (int i = 0; i < names.Length; i++) names[i] = presets[i].name;
            return names;
        }

        public static List<AnimationPreset> CreateDefaultPresets() => new List<AnimationPreset>
        {
            new AnimationPreset { name = "Idle", frames = 8, fps = 12, loop = true, poseKind = "idle", poseStrength = 1.8f, denoise = 0.8f,
                controlNetStrength = 0.6f, tileEndPercent = 0.6f, ipAdapterWeight = 1.0f, prompt = "idle, standing, breathing" },
            new AnimationPreset { name = "Walk", frames = 8, fps = 12, loop = true, poseKind = "walk", poseStrength = 2.1f, denoise = 0.92f,
                controlNetStrength = 0.5f, tileEndPercent = 0.6f, ipAdapterWeight = 1.0f, prompt = "walk cycle, walking" },
            new AnimationPreset { name = "Run", frames = 8, fps = 14, loop = true, poseKind = "run", poseStrength = 2.0f, denoise = 0.9f, motionScale = 1.2f,
                controlNetStrength = 0.5f, tileEndPercent = 0.6f, ipAdapterWeight = 1.0f, prompt = "run cycle, running fast" },
            new AnimationPreset { name = "Attack", frames = 10, fps = 12, loop = false, poseKind = "attack", poseStrength = 2.0f, denoise = 0.9f, motionScale = 1.2f,
                controlNetStrength = 0.5f, tileEndPercent = 0.6f, ipAdapterWeight = 1.0f, prompt = "attack, swinging sword" },
        };

        /// <summary>Loads the project's settings asset, creating it (with package defaults) on first use.</summary>
        public static AIAnimationSettings GetOrCreate()
        {
            string[] guids = AssetDatabase.FindAssets("t:AIAnimationSettings");
            if (guids.Length > 0)
                return AssetDatabase.LoadAssetAtPath<AIAnimationSettings>(AssetDatabase.GUIDToAssetPath(guids[0]));

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DefaultAssetPath));
            var settings = CreateInstance<AIAnimationSettings>();
            AssetDatabase.CreateAsset(settings, DefaultAssetPath);
            AssetDatabase.SaveAssets();
            return settings;
        }

        /// <summary>The workflow text: the project's override, or the one shipped in the package.</summary>
        public RigAutoParams AutoRigParams() => new RigAutoParams
        {
            neckLine = rigNeckLine, hipLine = rigHipLine, legHalfWidth = rigLegHalfWidth, armRadius = rigArmRadius, duplicateLegs = rigDuplicateLegs,
        };

        public string LoadWorkflowText()
        {
            if (workflow != null) return workflow.text;
            var shipped = AssetDatabase.LoadAssetAtPath<TextAsset>(PackagePaths.DefaultWorkflow);
            return shipped != null ? shipped.text : null;
        }
    }
}
