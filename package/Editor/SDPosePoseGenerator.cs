using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>Text prompts that make the video model draw a generic person doing the requested animation, seen from the side.</summary>
    public static class SDPosePrompts
    {
        private const string Framing = "side view in profile facing right, entire body visible from head to toe, wide shot, simple white studio background";

        public static string Positive(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "run": return "full body shot of a young man running fast, " + Framing + ", sprinting";
                case "attack": return "full body photo of a fencer lunging forward with a sword, " + Framing;
                // measured (docs/ai-pose-rig.md): "standing still and breathing" gave jittery clips (keypoint noise as large as the motion); this wording gives smooth ones
                case "idle": return "full body shot of a young man standing in profile facing right, subtly breathing, shoulders gently rising and falling, relaxed idle stance, entire body visible from head to toe, wide shot, simple white studio background";
                default: return "full body shot of a young man walking, " + Framing;
            }
        }

        public const string Negative = "close-up, cropped, legs only, feet only, upper body only, front view, back view, multiple people, blurry, text, watermark, deformed, extra limbs";
    }

    /// <summary>
    /// AI-generated motion. AnimateDiff (text to video, no sprite involved) makes a short clip of a generic person doing the animation seen from the side;
    /// SDPose reads the pose of the person in every frame; the keypoints are cleaned (facing, left/right limb labels), mapped onto the character's own rig
    /// (<see cref="OpenPoseMapper"/>) and the best repeatable stretch is cut out as the animation (<see cref="MotionCycleExtractor"/>). Several clips (seeds)
    /// are generated if needed and the best one is kept. The clip's pixels are never used.
    /// </summary>
    public sealed class SDPosePoseGenerator : IPoseGenerator
    {
        private const string VideoNode = "18", KeypointNode = "32";

        private readonly AIAnimationSettings settings;
        private readonly ComfyUIWorkflowRunner runner;

        public string Name => "SDPose";
        public bool IsAI => true;

        public SDPosePoseGenerator(AIAnimationSettings settings)
        {
            this.settings = settings;
            runner = new ComfyUIWorkflowRunner(settings);
        }

        public async Task<PoseGeneratorResult> GenerateAsync(PoseGeneratorContext ctx, Action<string, float> progress, CancellationToken ct)
        {
            string template = settings.LoadSdposeWorkflowText();
            if (string.IsNullOrWhiteSpace(template))
                throw new PoseBackendUnavailableException($"The SDPose workflow is missing. Reinstall the package ({PackagePaths.SdposeWorkflow}) or assign one in the settings asset.");
            ComfyWorkflowBuilder.Build(template, Values(ctx, 0, "validation"), requireInputImage: false);   // fail before ComfyUI is touched if the workflow is broken

            // Cheap check first: do not start ComfyUI just to find out the model is not there.
            if (SDPoseModel.Status(settings) == SDPoseModelStatus.Missing) throw new PoseBackendUnavailableException(SDPoseModel.MissingMessage);

            progress?.Invoke("Starting ComfyUI...", 0.03f);
            await runner.EnsureRunningAsync(progress, ct);
            await CheckNodesAndModelsAsync(ct);

            string kind = ctx.Preset.poseKind;
            var result = new PoseGeneratorResult { Backend = $"SDPose ({settings.sdposeModelName}) + AnimateDiff video", IsAI = true };
            var notes = new StringBuilder();
            SDPoseCandidate best = null;
            int bestIndex = -1;
            long bestSeed = 0;
            string bestKeypoints = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var analysisClock = new System.Diagnostics.Stopwatch();

            int total = Mathf.Max(1, settings.poseCandidates), made = 0;
            for (int c = 0; c < total; c++)
            {
                ct.ThrowIfCancellationRequested();
                long seed = ctx.Seed + c * 1013L;
                float p0 = 0.08f + 0.8f * c / total;
                progress?.Invoke($"Generating motion video {c + 1}/{total} (AnimateDiff)...", p0);

                string workflow = ComfyWorkflowBuilder.Build(template, Values(ctx, seed, "unity_pose"), requireInputImage: false);
                ComfyResult run = await runner.RunAsync(workflow, progress, p0, ct);
                string json = run.TextOf(KeypointNode);
                if (string.IsNullOrEmpty(json)) throw new ComfyUIException("SDPose returned no keypoints (the workflow has no PreviewAny text output).");

                progress?.Invoke($"Reading the pose of every frame (SDPose) {c + 1}/{total}...", p0 + 0.7f / total);
                analysisClock.Start();
                List<OpenPoseFrame> keypoints = OpenPoseJson.Parse(json);
                SDPoseCandidate cand = SDPoseAnalysis.Analyze(ctx.RigAsset.definition, keypoints, kind, ctx.Loop, ctx.Frames, ctx.FacingLeft);
                analysisClock.Stop();
                made++;
                notes.AppendLine($"  candidate {c + 1} (seed {seed}): {cand.Verdict}");

                if (!string.IsNullOrEmpty(ctx.DumpFolder)) await DumpAsync(ctx.DumpFolder, c, json, run, ct);
                if (cand.Extraction != null && cand.Extraction.Ok && (best == null || cand.Quality > best.Quality)) { best = cand; bestIndex = c; bestSeed = seed; bestKeypoints = json; }
                if (best != null && best.Quality >= settings.poseGoodEnoughQuality) break;
            }

            result.ComfySeconds = clock.Elapsed.TotalSeconds - analysisClock.Elapsed.TotalSeconds;
            result.AnalysisSeconds = analysisClock.Elapsed.TotalSeconds;
            if (best == null || best.Quality < settings.poseMinQuality)
                throw new PoseGenerationFailedException($"SDPose did not find usable {kind} motion in the generated videos (best quality {(best != null ? best.Quality : 0f):0.00}, required {settings.poseMinQuality:0.00}). " +
                    "Nothing was saved. Generate again (another seed is tried automatically), or raise 'Candidates'.\n" + notes.ToString().TrimEnd());

            result.Poses = best.Extraction.Poses;
            result.Quality = best.Quality;
            result.Candidates = made;
            result.Notes = $"Chosen: candidate {bestIndex + 1}, seed {bestSeed}, quality {best.Quality:0.00}. {best.Extraction.Notes}\n{notes.ToString().TrimEnd()}";
            if (!string.IsNullOrEmpty(ctx.DumpFolder)) File.WriteAllText(Path.Combine(ctx.DumpFolder, "chosen_keypoints.json"), bestKeypoints);
            return result;
        }

        public Task ReleaseAsync(Action<string, float> progress) => runner.ReleaseAsync(progress);

        private Dictionary<string, ComfyWorkflowBuilder.Value> Values(PoseGeneratorContext ctx, long seed, string outputPrefix) =>
            WorkflowValues(settings, ctx.Preset.poseKind, ctx.ExtraPrompt, ctx.ExtraNegativePrompt, seed);

        /// <summary>Every placeholder of the SDPose workflow. Also used by the diagnostics to validate the workflow.</summary>
        public static Dictionary<string, ComfyWorkflowBuilder.Value> WorkflowValues(AIAnimationSettings settings, string kind, string extraPrompt, string extraNegativePrompt, long seed)
        {
            string extra = string.IsNullOrWhiteSpace(extraPrompt) ? "" : ", " + extraPrompt.Trim().TrimEnd(',');
            return new Dictionary<string, ComfyWorkflowBuilder.Value>
            {
                [ComfyWorkflowBuilder.Checkpoint] = ComfyWorkflowBuilder.Value.Str(settings.checkpointName),
                [ComfyWorkflowBuilder.MotionModel] = ComfyWorkflowBuilder.Value.Str(settings.motionModelName),
                [ComfyWorkflowBuilder.SdposeModel] = ComfyWorkflowBuilder.Value.Str(settings.sdposeModelName),
                [ComfyWorkflowBuilder.Prompt] = ComfyWorkflowBuilder.Value.Str(SDPosePrompts.Positive(kind) + extra),
                [ComfyWorkflowBuilder.NegativePrompt] = ComfyWorkflowBuilder.Value.Str(SDPosePrompts.Negative + (string.IsNullOrWhiteSpace(extraNegativePrompt) ? "" : ", " + extraNegativePrompt.Trim().TrimEnd(','))),
                [ComfyWorkflowBuilder.Width] = ComfyWorkflowBuilder.Value.Num(settings.poseVideoWidth),
                [ComfyWorkflowBuilder.Height] = ComfyWorkflowBuilder.Value.Num(settings.poseVideoHeight),
                [ComfyWorkflowBuilder.FrameCount] = ComfyWorkflowBuilder.Value.Num(settings.poseVideoFrames),
                [ComfyWorkflowBuilder.Seed] = ComfyWorkflowBuilder.Value.Num(seed),
                [ComfyWorkflowBuilder.Steps] = ComfyWorkflowBuilder.Value.Num(settings.poseVideoSteps),
                [ComfyWorkflowBuilder.Cfg] = ComfyWorkflowBuilder.Value.Num(7f),
                [ComfyWorkflowBuilder.MotionScale] = ComfyWorkflowBuilder.Value.Num(settings.poseVideoMotionScale),
                [ComfyWorkflowBuilder.NoiseType] = ComfyWorkflowBuilder.Value.Str(settings.noiseType),
            };
        }

        // The authoritative check: what the running ComfyUI actually offers.
        private async Task CheckNodesAndModelsAsync(CancellationToken ct)
        {
            HashSet<string> checkpoints = await runner.GetComboOptionsAsync("CheckpointLoaderSimple", "ckpt_name", ct);
            if (checkpoints != null && !checkpoints.Contains(settings.sdposeModelName)) throw new PoseBackendUnavailableException(SDPoseModel.MissingMessage);
            if (checkpoints != null && !checkpoints.Contains(settings.checkpointName))
                throw new PoseBackendUnavailableException($"The Stable Diffusion checkpoint '{settings.checkpointName}' that AnimateDiff needs is not installed in ComfyUI.");
            await EnsureNodeAsync("SDPoseKeypointExtractor", "this ComfyUI is too old (SDPose needs ComfyUI 0.38 or newer)", ct);
            await EnsureNodeAsync("PreviewAny", "this ComfyUI has no 'Preview as Text' node", ct);
            await EnsureNodeAsync("ADE_UseEvolvedSampling", "the AnimateDiff-Evolved node pack is not installed (see README, ComfyUI setup)", ct);
        }

        private async Task EnsureNodeAsync(string nodeClass, string reason, CancellationToken ct)
        {
            if (!await runner.NodeExistsAsync(nodeClass, ct)) throw new PoseBackendUnavailableException($"The ComfyUI node '{nodeClass}' is missing: {reason}.");
        }

        private async Task DumpAsync(string folder, int candidate, string json, ComfyResult run, CancellationToken ct)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"keypoints_{candidate}.json"), json);
            var frames = run.ImagesOf(VideoNode);
            for (int i = 0; i < frames.Count; i++)
                File.WriteAllBytes(Path.Combine(folder, $"video_{candidate}_{i:00}.png"), await runner.DownloadAsync(frames[i], ct));
        }
    }
}
