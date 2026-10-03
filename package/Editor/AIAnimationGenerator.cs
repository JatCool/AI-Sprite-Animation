using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AISpriteAnimation
{
    /// <summary>AI Pose + Rig: where the poses of a build come from.</summary>
    public enum PoseSource
    {
        /// <summary>Use the saved pose asset only. Never starts ComfyUI; fails if there is none.</summary>
        Saved,
        /// <summary>Use the saved pose asset, or run the AI once if there is none yet.</summary>
        GenerateIfMissing,
        /// <summary>Run the AI again and replace the saved poses, then build.</summary>
        Regenerate,
    }

    public sealed class GenerationOptions
    {
        public Object Source;
        public string PresetName = "Walk";
        public int Frames = 8;                       // frames in the final animation (exactly this many are generated)
        public int Fps = 12;
        public bool Loop = true;
        public string ExtraPrompt = "";
        public string ExtraNegativePrompt = "";
        public long Seed = -1;                       // < 0 = random
        public AnimatorController Controller;        // optional
        /// <summary>Create a rig automatically when the sprite has none (otherwise the user is asked; batch mode always auto-creates).</summary>
        public bool AutoCreateRig;
        /// <summary>AI Pose + Rig only: use saved poses (default), generate them when missing, or regenerate.</summary>
        public PoseSource PoseSource = PoseSource.Saved;

        /// <summary>Use these settings instead of the project's settings asset (in-memory only; used by the self-test and batch overrides).</summary>
        public AIAnimationSettings SettingsOverride;
    }

    public sealed class GenerationOutcome
    {
        public bool Success, Cancelled;
        public string Error;
        public AnimationClip Clip;
        public string Folder;
        public int FrameWidth, FrameHeight, FrameCount;
        public double GenerationSeconds;
        /// <summary>AI Pose + Rig: the pose asset the animation was built from, how the poses were obtained, and what validation did.</summary>
        public AIPoseAsset PoseAsset;
        public bool PosesGeneratedNow;
        public PoseReport PoseReport;
        public double PoseGenerationSeconds, BuildSeconds;
    }

    /// <summary>
    /// Orchestrates one run: validate -> prepare padded canvas + reference + pose images -> backend -> pixel-art post-processing ->
    /// import -> clip -> controller -> release backend. The backend is released (ComfyUI stopped if Unity started it) only after the
    /// clip exists, and always, even on failure.
    /// </summary>
    public static class AIAnimationGenerator
    {
        public static bool IsRunning { get; private set; }

        internal static void SetRunning(bool running) => IsRunning = running;

        public static async Task<GenerationOutcome> GenerateAsync(GenerationOptions options, Action<string, float> progress, CancellationToken ct,
            IAIAnimationBackend backendOverride = null)
        {
            if (IsRunning) return Fail("A generation is already running.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) return Fail("Exit Play mode before generating animations.");

            var settings = options.SettingsOverride != null ? options.SettingsOverride : AIAnimationSettings.GetOrCreate();
            if (!SourceSprite.TryResolve(options.Source, settings, out SourceSprite source, out string error)) return Fail(error);

            string ext = Path.GetExtension(source.AssetPath).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") return Fail($"Unsupported source format '{ext}'. Use a PNG or JPG sprite.");

            var preset = settings.FindPreset(options.PresetName);
            if (preset == null) return Fail($"Animation preset '{options.PresetName}' not found in the settings asset.");
            if (string.IsNullOrWhiteSpace(settings.LoadWorkflowText())) return Fail($"No workflow found. Assign one in the settings asset or reinstall the package ({PackagePaths.DefaultWorkflow}).");
            if (options.Frames < 2 || options.Frames > ComfyWorkflowBuilder.MaxFrames) return Fail($"Frame count must be between 2 and {ComfyWorkflowBuilder.MaxFrames} (the bundled workflow has {ComfyWorkflowBuilder.MaxFrames} pose slots).");
            if (options.Fps < 1) return Fail("FPS must be at least 1.");
            if (settings.generationSize % 8 != 0 || settings.generationSize < 256) return Fail("Generation size must be a multiple of 8 and at least 256.");

            if (preset.turnThroughFront && settings.mode != AnimationMode.Rig)
                return Fail($"'{preset.name}' turns the character through its front view using the character's own side and front sprites (pixel-exact) and is a Rig animation: set the Animation Method to Rig.");
            if ((string.Equals(preset.poseKind, "jump", StringComparison.OrdinalIgnoreCase) || string.Equals(preset.poseKind, "sit", StringComparison.OrdinalIgnoreCase) || string.Equals(preset.poseKind, "crouch", StringComparison.OrdinalIgnoreCase) || string.Equals(preset.poseKind, "crouchwalk", StringComparison.OrdinalIgnoreCase)) && settings.mode != AnimationMode.Rig)
                return Fail($"'{preset.name}' is a Rig animation (the AI methods have no {preset.poseKind} skeleton or motion prompt): set the Animation Method to Rig.");
            Color32[] frontPixels = null; int frontW = 0, frontH = 0;
            if (preset.turnThroughFront && !FrontSprite.TryLoad(source, settings, out frontPixels, out frontW, out frontH, out string frontError)) return Fail(frontError);

            // Rig mode needs a rig for this sprite. AIRedraw only needs a silhouette guide, which is built in memory.
            bool useAI = settings.mode == AnimationMode.AIRedraw;
            SpriteRigAsset rigAsset = SpriteRigAsset.FindFor(source, settings);
            if (rigAsset == null)
            {
                if (useAI) rigAsset = SpriteRigAsset.CreateAuto(source, settings, save: false);
                else if (options.AutoCreateRig || Application.isBatchMode)
                {
                    rigAsset = SpriteRigAsset.CreateAuto(source, settings);
                    Debug.Log($"[AI Sprite Animation] Created a starting rig for '{source.Name}': {AssetDatabase.GetAssetPath(rigAsset)}. Refine it in Tools > AI Sprite Animation > Sprite Rig Editor.", rigAsset);
                }
                else
                {
                    int choice = EditorUtility.DisplayDialogComplex("No rig for this sprite",
                        $"'{source.Name}' has no rig yet. The rig tells the animator where the neck, arms, legs, weapon and ground are.\n\n" +
                        "Open the Sprite Rig Editor to set it up (recommended), or create a starting rig automatically and refine it later.",
                        "Open Rig Editor", "Cancel", "Auto-create");
                    if (choice == 0)
                    {
                        SpriteRigEditorWindow.Open(options.Source);
                        return new GenerationOutcome { Cancelled = true, Error = "Set up the rig in the Sprite Rig Editor, then generate again." };
                    }
                    if (choice == 1) return new GenerationOutcome { Cancelled = true, Error = "Cancelled: the sprite has no rig." };
                    rigAsset = SpriteRigAsset.CreateAuto(source, settings);
                }
            }

            // AI Pose + Rig: the motion comes from saved AI poses. The AI is only started when asked for (or when nothing is saved yet and the caller allows it).
            AIPoseAsset poseAsset = null;
            bool posesGeneratedNow = false;
            double poseSeconds = 0;
            if (settings.mode == AnimationMode.AIPoseRig)
            {
                poseAsset = AIPoseAsset.FindFor(source, settings, preset.name);
                if (options.PoseSource == PoseSource.Regenerate || (poseAsset == null && options.PoseSource == PoseSource.GenerateIfMissing))
                {
                    var poseOutcome = await AIPoseGenerator.GenerateAsync(new PoseGenerationOptions
                    {
                        Source = options.Source, PresetName = preset.name, Frames = options.Frames, Fps = options.Fps, Loop = options.Loop,
                        ExtraPrompt = options.ExtraPrompt, ExtraNegativePrompt = options.ExtraNegativePrompt, Seed = options.Seed,
                        AutoCreateRig = options.AutoCreateRig, SettingsOverride = options.SettingsOverride,
                    }, progress, ct);
                    if (!poseOutcome.Success) return new GenerationOutcome { Error = poseOutcome.Error, Cancelled = poseOutcome.Cancelled, PoseReport = poseOutcome.Report };
                    poseAsset = poseOutcome.Asset;
                    posesGeneratedNow = true;
                    poseSeconds = poseOutcome.GenerationSeconds;
                }
                if (poseAsset == null)
                    return Fail($"There are no saved AI poses for '{source.Name}' / {preset.name}. Click \"Generate Poses\" first (this is the only step that needs ComfyUI).");
            }

            IsRunning = true;
            EditorApplication.LockReloadAssemblies(); // a script reload mid-run would abort the run and kill ComfyUI
            IAIAnimationBackend backend = useAI ? (backendOverride ?? new ComfyUIAnimationBackend(settings)) : null;
            string tempDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp", "AISpriteAnimation", Guid.NewGuid().ToString("N"));
            var outcome = new GenerationOutcome();
            var clock = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                progress?.Invoke("Preparing sprite...", 0.02f);
                Directory.CreateDirectory(tempDir);
                PreparedInput input = SpriteFrameProcessor.PrepareInput(source, settings);
                File.WriteAllBytes(Path.Combine(tempDir, "input.png"), input.Png);

                // The posed source sprite: the final frames in Rig mode, and the source-derived silhouette that masks the AI frames in AIRedraw mode.
                progress?.Invoke("Posing the sprite...", 0.05f);
                IRigPoseProvider poseProvider = poseAsset != null
                    ? new AIPoseProvider(poseAsset, rigAsset.definition, settings.poseCleanup)
                    : (IRigPoseProvider)ProceduralRigPoses.Instance;
                RigPose[] rigPoses = poseProvider.GetPoses(preset.poseKind, options.Frames, options.Frames, preset.rigIntensity);
                if (poseProvider is AIPoseProvider aiProvider)
                {
                    outcome.PoseReport = aiProvider.LastReport;
                    outcome.PoseAsset = poseAsset;
                    outcome.PosesGeneratedNow = posesGeneratedNow;
                    outcome.PoseGenerationSeconds = poseSeconds;
                }
                var rigFrames = SpriteRig.Render(rigAsset.definition, input.SpritePixels, rigPoses, input.Cells, input.LeftCells, input.BottomCells,
                    settings.facing == SpriteFacing.Left);
                // Rotate: side view -> front view (held) -> the needed side. The side sprite is rendered in its rest pose, the front view is the character's own art
                // on the same grid and ground line; every pixel is one of the character's own.
                if (preset.turnThroughFront)
                {
                    Color32[] frontGrid = TurnThroughFront.PlaceOnGrid(frontPixels, frontW, frontH, input.Cells, settings.facing == SpriteFacing.Left ? input.Cells - input.LeftCells - source.Rect.width : input.LeftCells, input.BottomCells, source.Rect.width);
                    rigFrames = TurnThroughFront.Build(rigFrames[0], frontGrid, input.Cells, options.Fps, preset.frontHoldSeconds, preset.turnSquashFrames);
                }

                List<FrameBuffer> buffers;
                if (!useAI)
                {
                    // Rig mode: pixels come from the source sprite itself; no AI model, no background to remove.
                    buffers = new List<FrameBuffer>(rigFrames.Count);
                    foreach (var f in rigFrames) buffers.Add(new FrameBuffer { Pixels = f, Width = input.Cells, Height = input.Cells });
                }
                else
                {
                    // The model generates a few extra frames after the animation; they are dropped below (see AIAnimationSettings.tailBufferFrames).
                    int generatedCount = Mathf.Min(ComfyWorkflowBuilder.MaxFrames, options.Frames + settings.tailBufferFrames);

                    var request = new GenerationRequest
                    {
                        CharacterName = source.Name,
                        AnimationName = preset.name,
                        InputPng = File.ReadAllBytes(Path.Combine(tempDir, "input.png")),
                        Width = input.Width,
                        Height = input.Height,
                        FrameCount = generatedCount,
                        Fps = options.Fps,
                        Prompt = JoinPrompt(options.ExtraPrompt, preset.prompt, settings.basePrompt),
                        NegativePrompt = JoinPrompt(options.ExtraNegativePrompt, preset.negativePrompt, settings.baseNegativePrompt),
                        Seed = options.Seed >= 0 ? options.Seed : (long)new System.Random().Next(0, int.MaxValue),
                        Preset = preset,
                        OutputPrefix = $"unity_{source.Name}_{preset.name}_{DateTime.Now:HHmmss}",
                    };
                    request.ExtraImages[ComfyWorkflowBuilder.ReferenceImage] = input.ReferencePng;

                    // One pose image per generated frame (the cycle spans exactly Frames steps, so loops close seamlessly).
                    // Unused slots of the fixed-size workflow repeat the last pose; the workflow only uses the first FrameCount images.
                    var poses = PoseSequenceGenerator.Generate(preset.poseKind, generatedCount, options.Frames, input, settings.facing == SpriteFacing.Left);
                    for (int i = 0; i < ComfyWorkflowBuilder.MaxFrames; i++)
                        request.ExtraImages[ComfyWorkflowBuilder.PoseSlot(i)] = poses[Mathf.Min(i, poses.Count - 1)];

                    var generated = await backend.GenerateFramesAsync(request, progress, ct);
                    if (generated.Count < generatedCount)
                        throw new InvalidOperationException($"The backend returned {generated.Count} frames, expected {generatedCount}.");

                    progress?.Invoke("Processing frames...", 0.9f);
                    buffers = new List<FrameBuffer>(options.Frames);
                    for (int i = 0; i < options.Frames; i++)
                    {
                        var frame = SpriteFrameProcessor.ProcessFrame(generated[i], input, settings);
                        SpriteFrameProcessor.MaskToSilhouette(frame, rigFrames[i], settings.aiMaskRadius);   // source-derived mask, not the generated background
                        buffers.Add(frame);
                    }

                }
                FinalFrames final = SpriteFrameProcessor.Finalize(buffers, input, settings);

                progress?.Invoke("Importing sprites...", 0.93f);
                string folder = SpriteAnimationImporter.GetOutputFolder(settings, source, preset.name);
                var sprites = SpriteAnimationImporter.ImportFrames(folder, input.Png, final.Pngs, source, settings, final.Pivot01);

                progress?.Invoke("Creating AnimationClip...", 0.95f);
                string clipPath = $"{folder}/{source.Name}_{SourceSprite.SanitizeName(preset.name)}.anim";
                outcome.Clip = SpriteAnimationImporter.CreateOrUpdateClip(clipPath, sprites, options.Fps, options.Loop, settings.spriteBindingPath);
                outcome.Folder = folder;
                outcome.FrameWidth = final.Width; outcome.FrameHeight = final.Height; outcome.FrameCount = sprites.Count;

                if (options.Controller != null)
                    SpriteAnimationImporter.AssignToController(options.Controller, preset.name, outcome.Clip, settings.outputRoot);

                outcome.Success = true;
            }
            catch (OperationCanceledException)
            {
                outcome.Cancelled = true;
                outcome.Error = "Cancelled.";
            }
            catch (PoseRejectedException e)
            {
                outcome.PoseReport = e.Report;
                outcome.Error = e.Message + " Generate the poses again (another seed) or relax the pose clean-up settings.";
            }
            catch (Exception e)
            {
                outcome.Error = e is ComfyUIException ? e.Message : $"{e.GetType().Name}: {e.Message}";
                if (!(e is ComfyUIException)) Debug.LogException(e);
            }
            finally
            {
                try { if (backend != null) await backend.ReleaseAsync(progress); }
                catch (Exception e) { Debug.LogError("[AI Sprite Animation] Error while releasing backend: " + e.Message); }
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* temp only */ }
                AssetDatabase.Refresh();
                EditorApplication.UnlockReloadAssemblies();
                IsRunning = false;
                outcome.GenerationSeconds = clock.Elapsed.TotalSeconds;
                outcome.BuildSeconds = outcome.GenerationSeconds;
            }

            if (outcome.Success) progress?.Invoke("Done.", 1f);
            return outcome;
        }

        private static string JoinPrompt(params string[] parts)
        {
            var list = new List<string>();
            foreach (string p in parts)
                if (!string.IsNullOrWhiteSpace(p)) list.Add(p.Trim().TrimEnd(','));
            return string.Join(", ", list);
        }

        private static GenerationOutcome Fail(string message) => new GenerationOutcome { Error = message };
    }
}
