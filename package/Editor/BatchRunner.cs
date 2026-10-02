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
                    case "mode": s.mode = v.ToLowerInvariant().StartsWith("ai") ? AnimationMode.AIRedraw : AnimationMode.Rig; break;
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
            bool overridden = Arg("-aiOverride") != null || Arg("-aiWorkflow") != null;
            if (overridden) settings = CloneWithOverrides(settings, Arg("-aiOverride") ?? "", Arg("-aiPreset", "Walk"));
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

            var options = new GenerationOptions
            {
                Source = source,
                PresetName = preset.name,
                Frames = int.Parse(Arg("-aiFrames", preset.frames.ToString())),
                Fps = int.Parse(Arg("-aiFps", preset.fps.ToString())),
                Loop = preset.loop,
                Seed = long.Parse(Arg("-aiSeed", "-1")),
                AutoCreateRig = true,
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
                Debug.Log(outcome.Success ? $"[AI Sprite Animation] BATCH SUCCESS: {AssetDatabase.GetAssetPath(outcome.Clip)}"
                                          : $"[AI Sprite Animation] BATCH {(outcome.Cancelled ? "CANCELLED" : "FAILED")}: {outcome.Error}");
                EditorApplication.Exit(outcome.Success ? 0 : outcome.Cancelled ? 2 : 1);
            };
            EditorApplication.update += poll;
        }
    }
}
