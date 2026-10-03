using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// The 1.2.0 backend, kept for comparison and regression testing: a procedural sketch skeleton guides AnimateDiff (OpenPose ControlNet) on the
    /// sprite itself, the AI frames are reduced to silhouettes and the rig is fitted to them. Measured result: the AI barely moves the sprite, so the
    /// poses stay at the sketch. This is NOT AI-generated motion and is labelled so.
    /// </summary>
    public sealed class SketchEvidencePoseGenerator : IPoseGenerator
    {
        private readonly AIAnimationSettings settings;     // the project's settings
        private readonly AIAnimationSettings runSettings;  // working copy at the pose-evidence resolution
        private readonly IAIAnimationBackend backend;

        public string Name => "Sketch + AnimateDiff evidence (legacy)";
        public bool IsAI => false;

        public SketchEvidencePoseGenerator(AIAnimationSettings settings, IAIAnimationBackend backendOverride)
        {
            this.settings = settings;
            runSettings = UnityEngine.Object.Instantiate(settings);
            runSettings.generationSize = settings.aiPoseGenerationSize;
            runSettings.mode = AnimationMode.AIPoseRig;
            backend = backendOverride ?? new ComfyUIAnimationBackend(runSettings);
        }

        public async Task<PoseGeneratorResult> GenerateAsync(PoseGeneratorContext ctx, Action<string, float> progress, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(settings.LoadWorkflowText())) throw new PoseBackendUnavailableException($"No workflow found ({PackagePaths.DefaultWorkflow}).");
            if (settings.aiPoseGenerationSize % 8 != 0 || settings.aiPoseGenerationSize < 256) throw new PoseBackendUnavailableException("AI pose generation size must be a multiple of 8 and at least 256.");
            if (ctx.Frames > ComfyWorkflowBuilder.MaxFrames) throw new PoseBackendUnavailableException($"This backend supports at most {ComfyWorkflowBuilder.MaxFrames} frames.");

            var result = new PoseGeneratorResult { Backend = "Sketch + AnimateDiff evidence (legacy 1.2.0, not AI motion)", IsAI = false };
            var preset = ctx.Preset;
            RigDefinition rigDef = ctx.RigAsset.definition;
            progress?.Invoke("Preparing sketch skeleton...", 0.02f);
            PreparedInput input = SpriteFrameProcessor.PrepareInput(ctx.Source, runSettings);
            bool facingLeft = ctx.FacingLeft;

            int generatedCount = Mathf.Min(ComfyWorkflowBuilder.MaxFrames, ctx.Frames + settings.tailBufferFrames);
            RigPose[] sketch = PoseSketch.Build(preset.poseKind, generatedCount, ctx.Frames, preset.rigIntensity, ctx.Seed, settings.aiPoseSketchVariation);
            List<byte[]> guidanceImages = PoseSketch.Render(rigDef, sketch, input, facingLeft);

            var aiPreset = JsonUtility.FromJson<AnimationPreset>(JsonUtility.ToJson(preset));
            aiPreset.poseStrength = settings.aiPoseGuidance;

            var request = new GenerationRequest
            {
                CharacterName = ctx.Source.Name,
                AnimationName = preset.name,
                InputPng = input.Png,
                Width = input.Width,
                Height = input.Height,
                FrameCount = generatedCount,
                Fps = ctx.Fps,
                Prompt = JoinPrompt(ctx.ExtraPrompt, preset.prompt, settings.basePrompt),
                NegativePrompt = JoinPrompt(ctx.ExtraNegativePrompt, preset.negativePrompt, settings.baseNegativePrompt),
                Seed = ctx.Seed,
                Preset = aiPreset,
                OutputPrefix = $"unity_pose_{ctx.Source.Name}_{preset.name}_{DateTime.Now:HHmmss}",
            };
            request.ExtraImages[ComfyWorkflowBuilder.ReferenceImage] = input.ReferencePng;
            for (int i = 0; i < ComfyWorkflowBuilder.MaxFrames; i++)
                request.ExtraImages[ComfyWorkflowBuilder.PoseSlot(i)] = guidanceImages[Mathf.Min(i, guidanceImages.Count - 1)];

            var comfyClock = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<byte[]> generated = await backend.GenerateFramesAsync(request, progress, ct);
            result.ComfySeconds = comfyClock.Elapsed.TotalSeconds;
            if (generated.Count < generatedCount) throw new InvalidOperationException($"The backend returned {generated.Count} frames, expected {generatedCount}.");

            progress?.Invoke("Reading the motion from the AI frames...", 0.85f);
            var evidence = new List<Color32[]>(ctx.Frames);
            for (int i = 0; i < ctx.Frames; i++) evidence.Add(SpriteFrameProcessor.ProcessFrame(generated[i], input, runSettings).Pixels);
            if (!string.IsNullOrEmpty(ctx.DumpFolder)) DumpDebug(ctx.DumpFolder, input, guidanceImages, generated, evidence);

            Color32[] spritePixels = input.SpritePixels;
            int cells = input.Cells, left = input.LeftCells, bottom = input.BottomCells;
            var sketchFrames = SpriteRig.Render(rigDef, spritePixels, new List<RigPose>(sketch).GetRange(0, ctx.Frames), cells, left, bottom, facingLeft);
            result.EvidenceMotion = SilhouetteMotion(evidence, ctx.Loop);
            result.SketchMotion = SilhouetteMotion(sketchFrames, ctx.Loop);
            float motionRatio = result.SketchMotion > 1e-4f ? result.EvidenceMotion / result.SketchMotion : 1f;
            float effectiveWeight = settings.aiPoseEvidenceWeight * Mathf.Clamp01((motionRatio - 0.15f) / 0.6f);

            SynchronizationContext mainThread = SynchronizationContext.Current ?? new SynchronizationContext();
            var fitOptions = new RigPoseFitOptions
            {
                cancel = ct,
                priorWeight = 0.005f * Mathf.Pow(10000f, 1f - effectiveWeight),   // log scale: weight 1 = 0.005 (evidence decides), 0.5 = 0.5, 0 = 50 (the sketch is kept)
                progress = (done, total) => mainThread.Post(_ => progress?.Invoke($"Fitting the rig to frame {done}/{total}...", 0.86f + 0.08f * done / total), null),
            };
            var fitClock = System.Diagnostics.Stopwatch.StartNew();
            string kind = preset.poseKind;
            bool loop = ctx.Loop;
            RigPoseFitResult fit = await Task.Run(() => RigPoseFitter.Fit(rigDef, spritePixels, evidence, cells, left, bottom, facingLeft, sketch, kind, loop, fitOptions), ct);
            result.AnalysisSeconds = fitClock.Elapsed.TotalSeconds;
            result.FitError = fit.MeanError;
            result.Renders = fit.Renders;
            result.Poses = fit.Poses;
            result.Notes = $"Sketch variation {settings.aiPoseSketchVariation:0.00}, evidence weight {settings.aiPoseEvidenceWeight:0.00} (effective {effectiveWeight:0.00}), {fit.Renders} rig renders. " +
                $"AI frames contain {motionRatio:P0} of the sketch's silhouette motion ({result.EvidenceMotion:P1} vs {result.SketchMotion:P1} per frame); the saved poses are the sketch plus a few degrees of correction.";
            return result;
        }

        public async Task ReleaseAsync(Action<string, float> progress)
        {
            try { await backend.ReleaseAsync(progress); }
            finally { if (runSettings != null) UnityEngine.Object.DestroyImmediate(runSettings); }
        }

        private static float SilhouetteMotion(IReadOnlyList<Color32[]> frames, bool loop)
        {
            if (frames.Count < 2) return 0f;
            double sum = 0; int pairs = 0;
            for (int i = 1; i < frames.Count + (loop ? 1 : 0); i++)
            {
                Color32[] a = frames[i - 1], b = frames[i % frames.Count];
                int diff = 0, opaque = 0;
                for (int k = 0; k < a.Length; k++)
                {
                    bool pa = a[k].a != 0, pb = b[k].a != 0;
                    if (pa != pb) diff++;
                    if (pa) opaque++;
                }
                sum += diff / (double)Math.Max(1, opaque); pairs++;
            }
            return (float)(sum / pairs);
        }

        private static void DumpDebug(string folder, PreparedInput input, List<byte[]> guidance, IReadOnlyList<byte[]> generated, List<Color32[]> evidence)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "input.png"), input.Png);
            for (int i = 0; i < guidance.Count; i++) File.WriteAllBytes(Path.Combine(folder, $"guidance_{i:00}.png"), guidance[i]);
            for (int i = 0; i < generated.Count; i++) File.WriteAllBytes(Path.Combine(folder, $"ai_raw_{i:00}.png"), generated[i]);
            for (int i = 0; i < evidence.Count; i++)
                File.WriteAllBytes(Path.Combine(folder, $"evidence_{i:00}.png"), SpriteFrameProcessor.EncodePng(evidence[i], input.Cells, input.Cells));
        }

        private static string JoinPrompt(params string[] parts)
        {
            var list = new List<string>();
            foreach (string p in parts)
                if (!string.IsNullOrWhiteSpace(p)) list.Add(p.Trim().TrimEnd(','));
            return string.Join(", ", list);
        }
    }
}
