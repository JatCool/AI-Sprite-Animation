using System;
using System.Threading;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Command-line entry point for automation / CI:
    /// Unity -batchmode -projectPath P -executeMethod AISpriteAnimation.BatchRunner.Run -aiSource Assets/Art/hero.png -aiPreset Walk
    /// Machine setup: -aiComfyDir <folder with main.py> [-aiComfyPython exe] [-aiComfyUrl url] [-aiConfigureOnly 1].
    /// Optional: -aiFrames N -aiFps N -aiSeed N -aiController Assets/x.controller -aiTimeout seconds -aiCancelAfter seconds.
    /// Rig: -aiRigJoints joints.txt | -aiRigImport rig.json|folder|auto (generated rig data, see ChargenRigImporter); add -aiRigOnly 1 to stop after the rig.
    /// The editor exits with code 0 on success, 1 on failure, 2 if cancelled.
    /// </summary>
    public static class BatchRunner
    {
        // In-memory overrides for experiments: -aiOverride "denoise=0.8,poseStrength=1.4,ipAdapterWeight=1,noiseType=constant,paddingPercent=50"
        private static AIAnimationSettings CloneWithOverrides(AIAnimationSettings source, string spec, string presetName)
        {
            var s = UnityEngine.Object.Instantiate(source);
            var p = s.FindPreset(presetName);
            foreach (string pair in spec.Split(','))
            {
                string[] kv = pair.Split('=');
                if (kv.Length != 2) continue;
                string k = kv[0].Trim(), v = kv[1].Trim();
                float f = 0; float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f);
                switch (k)
                {
                    case "generationSize": s.generationSize = (int)f; break;
                    case "paddingPercent": s.paddingPercent = f; break;
                    case "noiseType": s.noiseType = v; break;
                    case "mode": s.mode = ParseMode(v); break;
                    case "poseGuidance": s.aiPoseGuidance = f; break;
                    case "poseSize": s.aiPoseGenerationSize = (int)f; break;
                    case "sketchVariation": s.aiPoseSketchVariation = f; break;
                    case "evidenceWeight": s.aiPoseEvidenceWeight = f; break;
                    case "poseBackend": s.poseBackend = ParseBackend(v); break;
                    case "candidates": s.poseCandidates = (int)f; break;
                    case "videoSteps": s.poseVideoSteps = (int)f; break;
                    case "videoFrames": s.poseVideoFrames = (int)f; break;
                    case "videoMotion": s.poseVideoMotionScale = f; break;
                    case "videoWidth": s.poseVideoWidth = (int)f; break;
                    case "videoHeight": s.poseVideoHeight = (int)f; break;
                    case "sdposeModel": s.sdposeModelName = v; break;
                    case "goodEnough": s.poseGoodEnoughQuality = f; break;
                    case "minQuality": s.poseMinQuality = f; break;
                    case "checkpoint": s.checkpointName = v; break;
                    case "smoothing": s.poseCleanup.smoothing = f; s.poseCleanup.smoothingEnabled = f > 0f; break;
                    case "smoothPasses": s.poseCleanup.passes = (int)f; break;
                    case "stepDegrees": s.poseCleanup.stepDegrees = f; break;
                    case "preserveDegrees": s.poseCleanup.preserveDegrees = f; break;
                    case "rigIntensity": p.rigIntensity = f; break;
                    case "tailBufferFrames": s.tailBufferFrames = (int)f; break;
                    case "keyTolerance": s.keyTolerance = f; break;
                    case "cropMarginPixels": s.cropMarginPixels = (int)f; break;
                    case "denoise": p.denoise = f; break;
                    case "poseStrength": p.poseStrength = f; break;
                    case "ipAdapterWeight": p.ipAdapterWeight = f; break;
                    case "controlNetStrength": p.controlNetStrength = f; break;
                    case "tileEnd": p.tileEndPercent = f; break;
                    case "motionScale": p.motionScale = f; break;
                    case "steps": p.steps = (int)f; break;
                    case "cfg": p.cfg = f; break;
                    case "prompt": p.prompt = v; break;
                    default: Debug.LogWarning("[AI Sprite Animation] Unknown override: " + k); break;
                }
            }
            return s;
        }

        // sdpose | legacy | procedural
        private static PoseBackend ParseBackend(string v)
        {
            v = (v ?? "").ToLowerInvariant();
            if (v.StartsWith("leg") || v.StartsWith("sketch")) return PoseBackend.SketchEvidenceLegacy;
            if (v.StartsWith("proc")) return PoseBackend.ProceduralFallback;
            return PoseBackend.SDPose;
        }

        // rig | pose (AI Pose + Rig) | redraw (AI Redraw)
        private static AnimationMode ParseMode(string v)
        {
            v = (v ?? "").ToLowerInvariant();
            if (v.StartsWith("pose") || v.StartsWith("aipose")) return AnimationMode.AIPoseRig;
            if (v.StartsWith("ai") || v.StartsWith("redraw")) return AnimationMode.AIRedraw;
            return AnimationMode.Rig;
        }

        public static void Run()
        {
            // A script reload while the async run is pending would silently drop it; the process exits when done anyway.
            EditorApplication.LockReloadAssemblies();

            string Arg(string name, string fallback = null)
            {
                string[] a = Environment.GetCommandLineArgs();
                for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
                return fallback;
            }

            // Machine-level ComfyUI configuration (stored in EditorPrefs, like the editor window does).
            if (Arg("-aiComfyDir") != null) ComfyUIConnectionSettings.ComfyUIDirectory = Arg("-aiComfyDir");
            if (Arg("-aiComfyPython") != null) ComfyUIConnectionSettings.PythonPath = Arg("-aiComfyPython");
            if (Arg("-aiComfyUrl") != null) ComfyUIConnectionSettings.Url = Arg("-aiComfyUrl");
            if (Arg("-aiConfigureOnly") != null) { EditorApplication.Exit(0); return; }

            var settings = AIAnimationSettings.GetOrCreate();
            bool overridden = Arg("-aiOverride") != null || Arg("-aiWorkflow") != null || Arg("-aiMode") != null;
            if (overridden) settings = CloneWithOverrides(settings, Arg("-aiOverride") ?? "", Arg("-aiPreset", "Walk"));
            if (Arg("-aiMode") != null) settings.mode = ParseMode(Arg("-aiMode"));
            if (Arg("-aiWorkflow") != null) settings.workflow = new TextAsset(System.IO.File.ReadAllText(Arg("-aiWorkflow")));
            if (Arg("-aiSelfTest") != null)
            {
                var test = PipelineSelfTest.RunAsync((m, p) => { if (!m.StartsWith("Generating animation")) Debug.Log($"[AI Sprite Animation] {p:P0} {m}"); }, CancellationToken.None);
                EditorApplication.CallbackFunction waitTest = null;
                waitTest = () =>
                {
                    if (!test.IsCompleted) return;
                    EditorApplication.update -= waitTest;
                    bool errors = test.IsFaulted;
                    if (test.IsFaulted) Debug.LogError("[AI Sprite Animation] Self-test crashed: " + test.Exception);
                    else foreach (var item in test.Result)
                    {
                        errors |= item.Status == DiagnosticStatus.Error;
                        Debug.Log($"[AI Sprite Animation] SELFTEST [{item.Group}] {item.Status}: {item.Label}{(string.IsNullOrEmpty(item.Detail) ? "" : " -- " + item.Detail)}");
                    }
                    EditorApplication.Exit(errors ? 1 : 0);
                };
                EditorApplication.update += waitTest;
                return;
            }
            if (Arg("-aiDiagnostics") != null)
            {
                // -aiDiagnostics live  -> start ComfyUI if needed and validate through its API; anything else -> offline checks only.
                var diag = ComfyUIDiagnostics.RunAsync(settings, Arg("-aiDiagnostics") == "live", CancellationToken.None);
                EditorApplication.CallbackFunction wait = null;
                wait = () =>
                {
                    if (!diag.IsCompleted) return;
                    EditorApplication.update -= wait;
                    if (diag.IsFaulted) { Debug.LogError("[AI Sprite Animation] Diagnostics failed: " + diag.Exception); EditorApplication.Exit(1); return; }
                    bool errors = false;
                    foreach (var item in diag.Result)
                    {
                        errors |= item.Status == DiagnosticStatus.Error;
                        Debug.Log($"[AI Sprite Animation] DIAG [{item.Group}] {item.Status}: {item.Label}{(string.IsNullOrEmpty(item.Detail) ? "" : " -- " + item.Detail)}");
                    }
                    EditorApplication.Exit(errors ? 1 : 0);
                };
                EditorApplication.update += wait;
                return;
            }
            string sourcePath = Arg("-aiSource");
            var source = sourcePath != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(sourcePath) : null;
            var preset = settings.FindPreset(Arg("-aiPreset", "Walk"));
            if (source == null || preset == null)
            {
                Debug.LogError($"[AI Sprite Animation] Batch: need -aiSource <asset path> (found: {source != null}) and a valid -aiPreset (found: {preset != null}).");
                EditorApplication.Exit(1);
                return;
            }
            if (Arg("-aiTimeout") != null) settings.generationTimeoutSeconds = int.Parse(Arg("-aiTimeout"));

            // Optional: build/refresh the rig of this sprite from a joints file ("JointName x y" per line, "ground y"), then continue or stop.
            if (Arg("-aiRigJoints") != null)
            {
                if (!SourceSprite.TryResolve(source, settings, out SourceSprite rigSource, out string rigError)) { Debug.LogError("[AI Sprite Animation] " + rigError); EditorApplication.Exit(1); return; }
                var rigAsset = SpriteRigAsset.FindFor(rigSource, settings) ?? SpriteRigAsset.CreateAuto(rigSource, settings);
                foreach (string line in System.IO.File.ReadAllLines(Arg("-aiRigJoints")))
                {
                    string[] t = line.Split(new[] { ' ', '	' }, StringSplitOptions.RemoveEmptyEntries);
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (t.Length == 3 && Enum.TryParse(t[0], out RigJoint joint)) rigAsset.definition.SetJoint(joint, new Vector2(float.Parse(t[1], inv), float.Parse(t[2], inv)));
                    else if (t.Length == 2 && t[0] == "ground") rigAsset.definition.groundY = float.Parse(t[1], inv);
                }
                Color32[] rigPixels = SpriteFrameProcessor.LoadSpritePixels(rigSource, out int rsw, out int rsh);
                RigAutoBuilder.AssignParts(rigAsset.definition, rigPixels, rsw, rsh, settings.AutoRigParams());
                rigAsset.Commit();
                AssetDatabase.SaveAssets();
                Debug.Log("[AI Sprite Animation] Rig written: " + AssetDatabase.GetAssetPath(rigAsset));
                if (Arg("-aiRigOnly") != null) { EditorApplication.Exit(0); return; }
            }

            // Optional: import generated rig data (chargen-rig/1: rig.json + parts.png [+ underlay.png]) into this sprite's rig asset, then continue or stop.
            // -aiRigImport <rig.json or its folder>; "auto" = rig/rig.json next to the sprite.
            if (Arg("-aiRigImport") != null)
            {
                if (!SourceSprite.TryResolve(source, settings, out SourceSprite importSource, out string importError)) { Debug.LogError("[AI Sprite Animation] " + importError); EditorApplication.Exit(1); return; }
                string rigJson = Arg("-aiRigImport") == "auto" ? ChargenRigImporter.FindRigJson(importSource) : Arg("-aiRigImport");
                try
                {
                    if (rigJson == null) throw new System.IO.FileNotFoundException($"No rig/rig.json next to '{importSource.AssetPath}'.");
                    var imported = ChargenRigImporter.Import(importSource, settings, rigJson);
                    Debug.Log($"[AI Sprite Animation] Rig imported from '{rigJson}': {AssetDatabase.GetAssetPath(imported)} (underlay: {imported.definition.HasUnderlay}, leg swing scale: {imported.definition.EffectiveLegSwingScale})");
                }
                catch (Exception e) { Debug.LogError("[AI Sprite Animation] Rig import failed: " + e.Message); EditorApplication.Exit(1); return; }
                if (Arg("-aiRigOnly") != null) { EditorApplication.Exit(0); return; }
            }

            // AI Pose + Rig: -aiPoseAction generate (run the AI, save poses, stop) | build (saved poses only, no ComfyUI; default) | both (regenerate, then build)
            //                -aiPoseDump <folder> writes the guidance images and the AI evidence frames for inspection.
            string poseAction = Arg("-aiPoseAction", settings.mode == AnimationMode.AIPoseRig ? "build" : null);
            if (poseAction != null)
            {
                int toolCode = BatchPoseTools.TryRun(poseAction, a => Arg(a), settings, source, preset, int.Parse(Arg("-aiFrames", preset.frames.ToString())), int.Parse(Arg("-aiFps", preset.fps.ToString())));
                if (toolCode >= 0) { EditorApplication.Exit(toolCode); return; }
            }
            if (poseAction == "generate")
            {
                var poseCts = new CancellationTokenSource();
                if (Arg("-aiCancelAfter") != null) poseCts.CancelAfter(TimeSpan.FromSeconds(double.Parse(Arg("-aiCancelAfter"))));
                string lastPose = null;
                var poseTask = AIPoseGenerator.GenerateAsync(new PoseGenerationOptions
                {
                    Source = source, PresetName = preset.name,
                    Frames = int.Parse(Arg("-aiFrames", preset.frames.ToString())), Fps = int.Parse(Arg("-aiFps", preset.fps.ToString())), Loop = preset.loop,
                    Seed = long.Parse(Arg("-aiSeed", "-1")), AutoCreateRig = true,
                    SettingsOverride = overridden ? settings : null, DumpFolder = Arg("-aiPoseDump"),
                    Backend = Arg("-aiPoseBackend") != null ? ParseBackend(Arg("-aiPoseBackend")) : (PoseBackend?)null,
                }, (m, p) =>
                {
                    if (m.StartsWith("Generating animation") || m == lastPose) return;
                    lastPose = m;
                    Debug.Log($"[AI Sprite Animation] {p:P0} {m}");
                }, poseCts.Token);
                EditorApplication.CallbackFunction pollPose = null;
                pollPose = () =>
                {
                    if (!poseTask.IsCompleted) return;
                    EditorApplication.update -= pollPose;
                    var po = poseTask.Result;
                    if (po.Success)
                        Debug.Log($"[AI Sprite Animation] BATCH POSE SUCCESS: {AssetDatabase.GetAssetPath(po.Asset)} | {po.Asset.FrameCount} frames | backend: {po.Backend} (AI motion: {po.IsAI}) | total {po.GenerationSeconds:0.0}s (ComfyUI {po.ComfySeconds:0.0}s, analysis {po.AnalysisSeconds:0.0}s) | candidates {po.Candidates}, quality {po.Quality:0.00} | {po.Contribution?.Summary} {po.Report}");
                    else Debug.Log($"[AI Sprite Animation] BATCH POSE {(po.Cancelled ? "CANCELLED" : "FAILED")}: {po.Error}");
                    EditorApplication.Exit(po.Success ? 0 : po.Cancelled ? 2 : 1);
                };
                EditorApplication.update += pollPose;
                return;
            }

            var options = new GenerationOptions
            {
                Source = source,
                PresetName = preset.name,
                Frames = int.Parse(Arg("-aiFrames", preset.frames.ToString())),
                Fps = int.Parse(Arg("-aiFps", preset.fps.ToString())),
                Loop = preset.loop,
                Seed = long.Parse(Arg("-aiSeed", "-1")),
                AutoCreateRig = true,
                PoseSource = poseAction == "both" ? PoseSource.Regenerate : PoseSource.Saved,
                SettingsOverride = overridden ? settings : null,
                Controller = Arg("-aiController") != null ? AssetDatabase.LoadAssetAtPath<AnimatorController>(Arg("-aiController")) : null,
            };

            var cts = new CancellationTokenSource();
            if (Arg("-aiCancelAfter") != null) cts.CancelAfter(TimeSpan.FromSeconds(double.Parse(Arg("-aiCancelAfter"))));

            string last = null;
            var task = AIAnimationGenerator.GenerateAsync(options, (m, p) =>
            {
                if (m.StartsWith("Generating animation") || m == last) return; // don't spam the log every second
                last = m;
                Debug.Log($"[AI Sprite Animation] {p:P0} {m}");
            }, cts.Token);

            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (!task.IsCompleted) return;
                EditorApplication.update -= poll;
                var outcome = task.Result;
                if (outcome.Success && outcome.PoseAsset != null)
                    Debug.Log($"[AI Sprite Animation] AI POSE BUILD: poses from {AssetDatabase.GetAssetPath(outcome.PoseAsset)} ({(outcome.PosesGeneratedNow ? "generated now" : "saved, ComfyUI not used")}); build {outcome.BuildSeconds:0.0}s\n{outcome.PoseReport}");
                Debug.Log(outcome.Success ? $"[AI Sprite Animation] BATCH SUCCESS: {AssetDatabase.GetAssetPath(outcome.Clip)}"
                                          : $"[AI Sprite Animation] BATCH {(outcome.Cancelled ? "CANCELLED" : "FAILED")}: {outcome.Error}");
                EditorApplication.Exit(outcome.Success ? 0 : outcome.Cancelled ? 2 : 1);
            };
            EditorApplication.update += poll;
        }
    }
}
