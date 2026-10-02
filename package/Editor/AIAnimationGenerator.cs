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
    }

    /// <summary>
    /// Orchestrates one run: validate -> prepare padded canvas + reference + pose images -> backend -> pixel-art post-processing ->
    /// import -> clip -> controller -> release backend. The backend is released (ComfyUI stopped if Unity started it) only after the
    /// clip exists, and always, even on failure.
    /// </summary>
    public static class AIAnimationGenerator
    {
        public static bool IsRunning { get; private set; }

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

            IsRunning = true;
            EditorApplication.LockReloadAssemblies(); // a script reload mid-run would abort the run and kill ComfyUI
            bool useAI = settings.mode == AnimationMode.AIRedraw;
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

                var rigOptions = new RigOptions
                {
                    swing = preset.rigSwing,
                    moveForwardArm = preset.rigMoveArm,
                    bodyHalfWidth = settings.rigBodyHalfWidth,
                    legHalfWidth = settings.rigLegHalfWidth,
                    neckLine = settings.rigNeckLine,
                    hipLine = settings.rigHipLine,
                    armGain = settings.rigArmGain,
                };
                // The posed source sprite: the final frames in Rig mode, and the source-derived silhouette that masks the AI frames in AIRedraw mode.
                progress?.Invoke("Posing the sprite...", 0.05f);
                var rigFrames = SpriteRig.Animate(input.SpritePixels, input.SrcWidth, input.SrcHeight, preset.poseKind, options.Frames, options.Frames,
                    input.Cells, input.LeftCells, input.BottomCells, settings.facing == SpriteFacing.Left, rigOptions);

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
