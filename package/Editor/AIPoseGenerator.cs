using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AISpriteAnimation
{
    public sealed class PoseGenerationOptions
    {
        public Object Source;
        public string PresetName = "Walk";
        public int Frames = 8;
        public int Fps = 12;
        public bool Loop = true;
        public string ExtraPrompt = "";
        public string ExtraNegativePrompt = "";
        public long Seed = -1;                     // < 0 = random
        public bool AutoCreateRig;
        public AIAnimationSettings SettingsOverride;
        /// <summary>Overrides the backend chosen in the settings.</summary>
        public PoseBackend? Backend;
        /// <summary>Debug: folder for intermediate files (generated video frames, keypoints, ...).</summary>
        public string DumpFolder;
    }

    public sealed class PoseGenerationOutcome
    {
        public bool Success, Cancelled;
        /// <summary>The selected backend cannot work (model missing, node missing, ...). Nothing else was tried in its place.</summary>
        public bool Unavailable;
        public string Error;
        public AIPoseAsset Asset;
        public PoseReport Report;
        public PoseContribution Contribution;
        public string Backend = "";
        public bool IsAI;
        public string Notes = "";
        public double GenerationSeconds, ComfySeconds, AnalysisSeconds;
        public float Quality, FitError;
        public int Candidates, FitRenders;
        public float EvidenceMotion, SketchMotion;
    }

    /// <summary>
    /// AI Pose + Rig, step 1: produces the motion and saves it as an <see cref="AIPoseAsset"/>. The configured <see cref="IPoseGenerator"/> makes the raw poses
    /// (SDPose: AI-generated), this class validates and smooths them, measures how much of the result is the AI's own motion, saves, and stops ComfyUI
    /// again if Unity started it. A backend that cannot run is reported, never silently replaced by another one.
    /// </summary>
    public static class AIPoseGenerator
    {
        public static IPoseGenerator CreateGenerator(PoseBackend backend, AIAnimationSettings settings, IAIAnimationBackend backendOverride = null)
        {
            switch (backend)
            {
                case PoseBackend.SDPose: return new SDPosePoseGenerator(settings);
                case PoseBackend.SketchEvidenceLegacy: return new SketchEvidencePoseGenerator(settings, backendOverride);
                default: return new ProceduralPoseGenerator(settings);
            }
        }

        public static async Task<PoseGenerationOutcome> GenerateAsync(PoseGenerationOptions options, Action<string, float> progress, CancellationToken ct,
            IAIAnimationBackend backendOverride = null)
        {
            if (AIAnimationGenerator.IsRunning) return Fail("A generation is already running.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) return Fail("Exit Play mode before generating poses.");

            var settings = options.SettingsOverride != null ? options.SettingsOverride : AIAnimationSettings.GetOrCreate();
            if (!SourceSprite.TryResolve(options.Source, settings, out SourceSprite source, out string error)) return Fail(error);
            var preset = settings.FindPreset(options.PresetName);
            if (preset == null) return Fail($"Animation preset '{options.PresetName}' not found in the settings asset.");
            if (options.Frames < 2 || options.Frames > ComfyWorkflowBuilder.MaxFrames) return Fail($"Frame count must be between 2 and {ComfyWorkflowBuilder.MaxFrames}.");
            if (options.Fps < 1) return Fail("FPS must be at least 1.");

            SpriteRigAsset rigAsset = SpriteRigAsset.FindFor(source, settings);
            if (rigAsset == null)
            {
                if (options.AutoCreateRig || Application.isBatchMode)
                {
                    rigAsset = SpriteRigAsset.CreateAuto(source, settings);
                    Debug.Log($"[AI Sprite Animation] Created a starting rig for '{source.Name}': {AssetDatabase.GetAssetPath(rigAsset)}. Refine it in the Sprite Rig Editor.", rigAsset);
                }
                else
                {
                    SpriteRigEditorWindow.Open(options.Source);
                    return new PoseGenerationOutcome { Cancelled = true, Error = "AI Pose + Rig needs a rig. Set it up in the Sprite Rig Editor, then generate the poses again." };
                }
            }

            PoseBackend backendKind = options.Backend ?? settings.poseBackend;
            AIAnimationGenerator.SetRunning(true);
            EditorApplication.LockReloadAssemblies();   // a script reload mid-run would abort the run and leave ComfyUI running
            IPoseGenerator generator = CreateGenerator(backendKind, settings, backendOverride);
            var outcome = new PoseGenerationOutcome { Backend = generator.Name, IsAI = generator.IsAI };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            AIPoseAsset transientAsset = null;

            try
            {
                long seed = options.Seed >= 0 ? options.Seed : (long)new System.Random().Next(0, int.MaxValue);
                var context = new PoseGeneratorContext
                {
                    Source = source, RigAsset = rigAsset, Preset = preset, Settings = settings,
                    Frames = options.Frames, Fps = options.Fps, Loop = options.Loop, Seed = seed,
                    FacingLeft = settings.facing == SpriteFacing.Left,
                    ExtraPrompt = options.ExtraPrompt, ExtraNegativePrompt = options.ExtraNegativePrompt,
                    DumpFolder = options.DumpFolder, BackendOverride = backendOverride,
                };

                PoseGeneratorResult generated = await generator.GenerateAsync(context, progress, ct);
                if (generated.Poses == null || generated.Poses.Length != options.Frames)
                    throw new PoseGenerationFailedException($"The pose generator returned {generated.Poses?.Length ?? 0} poses, expected {options.Frames}.");
                outcome.Backend = generated.Backend; outcome.IsAI = generated.IsAI; outcome.Notes = generated.Notes;
                outcome.ComfySeconds = generated.ComfySeconds; outcome.AnalysisSeconds = generated.AnalysisSeconds;
                outcome.Quality = generated.Quality; outcome.Candidates = generated.Candidates; outcome.FitError = generated.FitError; outcome.FitRenders = generated.Renders;
                outcome.EvidenceMotion = generated.EvidenceMotion; outcome.SketchMotion = generated.SketchMotion;

                // Validate, repair and smooth before anything is saved. A rejected sequence throws and nothing is written.
                progress?.Invoke("Validating poses...", 0.95f);
                RigDefinition rigDef = rigAsset.definition;
                AIPoseAsset asset = transientAsset = AIPoseAsset.NewTransient(source, preset.name);
                asset.loop = options.Loop;
                asset.fps = options.Fps;
                asset.SetSource(generated.Poses);

                // The procedural animation of the same type: reference for the AI-contribution measurement and the AI-vs-procedural numbers of the report.
                RigPose[] reference = ProceduralRigPoses.Instance.GetPoses(preset.poseKind, options.Frames, options.Frames, preset.rigIntensity);
                // Validation + constraints + smoothing: what is saved in the asset is the FINAL result of this, not the raw AI poses.
                PoseCleanup.Result cleaned = asset.BuildFinal(rigDef, settings.poseCleanup, reference);

                // How much of the saved motion is the AI's own: the difference to the procedural animation of the same type.
                PoseContribution contribution = PoseContribution.Compute(cleaned.Poses, reference, options.Loop);
                outcome.Contribution = contribution;
                if (generated.IsAI && contribution.Level == ContributionLevel.Negligible)
                    cleaned.Report.Warnings.Add("The AI contribution is negligible: the saved motion is practically the procedural animation. " + contribution.Summary);
                else if (!generated.IsAI)
                    cleaned.Report.Warnings.Add($"NOT AI-generated motion ({generated.Backend}).");

                asset.rigAssetPath = AssetDatabase.GetAssetPath(rigAsset);
                asset.rigSignature = AIPoseAsset.SignatureOf(rigDef);
                asset.backend = generated.Backend;
                asset.motionSource = backendKind.ToString();
                asset.isAiMotion = generated.IsAI;
                asset.contributionDegrees = contribution.MeanDeviationDegrees;
                asset.contributionLevel = generated.IsAI ? contribution.Level.ToString() : "NotAI";
                asset.contributionSummary = contribution.Summary;
                asset.createdUtc = DateTime.UtcNow.ToString("o");
                asset.seed = seed;
                asset.prompt = backendKind == PoseBackend.SDPose ? SDPosePrompts.Positive(preset.poseKind) : preset.prompt;
                asset.guidanceStrength = backendKind == PoseBackend.SketchEvidenceLegacy ? settings.aiPoseGuidance : 0f;
                asset.fitError = generated.FitError;
                asset.generationSeconds = (float)clock.Elapsed.TotalSeconds;
                asset.notes = generated.Notes;
                asset.cleanupReport = cleaned.Report.ToString() + "\n  " + contribution.Summary;
                asset = asset.SaveAs(source, settings);   // only now is anything written to the project

                outcome.Asset = asset;
                outcome.Report = cleaned.Report;
                outcome.Success = true;
            }
            catch (OperationCanceledException)
            {
                outcome.Cancelled = true;
                outcome.Error = "Cancelled.";
            }
            catch (PoseBackendUnavailableException e)
            {
                outcome.Unavailable = true;
                outcome.Error = e.Message;
            }
            catch (PoseGenerationFailedException e)
            {
                outcome.Error = e.Message;
            }
            catch (PoseRejectedException e)
            {
                outcome.Report = e.Report;
                outcome.Error = e.Message;
            }
            catch (Exception e)
            {
                outcome.Error = e is ComfyUIException ? e.Message : $"{e.GetType().Name}: {e.Message}";
                if (!(e is ComfyUIException)) Debug.LogException(e);
            }
            finally
            {
                // ComfyUI is stopped here when Unity started it, with or without success.
                try { await generator.ReleaseAsync(progress); }
                catch (Exception e) { Debug.LogError("[AI Sprite Animation] Error while releasing the pose generator: " + e.Message); }
                if (transientAsset != null && !EditorUtility.IsPersistent(transientAsset)) Object.DestroyImmediate(transientAsset);   // rejected / failed run: nothing was saved
                AssetDatabase.Refresh();
                EditorApplication.UnlockReloadAssemblies();
                AIAnimationGenerator.SetRunning(false);
                outcome.GenerationSeconds = clock.Elapsed.TotalSeconds;
            }

            if (outcome.Success) progress?.Invoke("Poses saved.", 1f);
            return outcome;
        }

        private static PoseGenerationOutcome Fail(string message) => new PoseGenerationOutcome { Error = message };
    }
}
