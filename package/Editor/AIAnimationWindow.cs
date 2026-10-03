using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace AISpriteAnimation
{
    public class AIAnimationWindow : EditorWindow
    {
        [SerializeField] private UnityEngine.Object source;
        [SerializeField] private int presetIndex = 1;
        [SerializeField] private int frames = 8;
        [SerializeField] private int fps = 10;
        [SerializeField] private bool loop = true;
        [SerializeField] private string prompt = "";
        [SerializeField] private string negativePrompt = "";
        [SerializeField] private long seed = -1;
        [SerializeField] private AnimatorController controller;
        [SerializeField] private bool showComfyUI = true;
        [SerializeField] private Vector2 scroll;

        private AIAnimationSettings settings;
        private CancellationTokenSource cts;
        private string status = "";
        private float progress;
        private bool lastWasError;
        private string connectionResult = "";
        private List<DiagnosticItem> diagnostics;
        private bool diagnosticsRunning;
        private bool showDiagnostics = true;
        private bool pipelineTestRunning;
        private bool showPoseCleanup, showSdposeAdvanced, showValidationReport;
        private bool modelInstalling;
        private SerializedObject settingsSO;

        public static void Open()
        {
            var window = GetWindow<AIAnimationWindow>("AI Sprite Animation");
            window.minSize = new Vector2(380, 520);
            if (SourceSprite.IsValidSelection(Selection.activeObject)) window.source = Selection.activeObject;
            window.Show();
        }

        private void OnEnable()
        {
            settings = AIAnimationSettings.GetOrCreate();
            if (source == null && SourceSprite.IsValidSelection(Selection.activeObject)) source = Selection.activeObject;
            RunDiagnostics(false);
        }

        private void OnGUI()
        {
            if (settings == null) settings = AIAnimationSettings.GetOrCreate();
            bool busy = AIAnimationGenerator.IsRunning || pipelineTestRunning || modelInstalling;
            scroll = EditorGUILayout.BeginScrollView(scroll);

            using (new EditorGUI.DisabledScope(busy))
            {
                EditorGUILayout.LabelField("Animation", EditorStyles.boldLabel);
                source = EditorGUILayout.ObjectField("Source Sprite", source, typeof(UnityEngine.Object), false);
                if (source != null && !SourceSprite.IsValidSelection(source)) source = null;

                EditorGUI.BeginChangeCheck();
                int newMode = EditorGUILayout.Popup(new GUIContent("Animation Method", "Rig moves the sprite's own pixels with a bone hierarchy: exact identity, instant, no AI. AI Pose + Rig lets the AI decide only how the character moves; the pixels still come from the rig. AI Redraw lets AnimateDiff redraw each frame: slower, needs a GPU and ComfyUI, and changes pixel-level details."),
                    AnimationModeLabels.IndexOf(settings.mode), AnimationModeLabels.Labels);
                if (EditorGUI.EndChangeCheck())
                {
                    settings.mode = AnimationModeLabels.Order[newMode];
                    EditorUtility.SetDirty(settings);
                    RunDiagnostics(false);
                }
                DrawRigInfo(settings);

                string[] names = settings.PresetNames();
                if (names.Length == 0) EditorGUILayout.HelpBox("No presets in the settings asset.", MessageType.Warning);
                else
                {
                    presetIndex = Mathf.Clamp(presetIndex, 0, names.Length - 1);
                    EditorGUI.BeginChangeCheck();
                    presetIndex = EditorGUILayout.Popup("Animation Type", presetIndex, names);
                    if (EditorGUI.EndChangeCheck()) ApplyPreset(settings.presets[presetIndex]);
                }

                frames = EditorGUILayout.IntSlider("Frames", frames, 2, ComfyWorkflowBuilder.MaxFrames);
                fps = EditorGUILayout.IntSlider("FPS", fps, 1, 60);
                loop = EditorGUILayout.Toggle("Loop", loop);
                EditorGUILayout.LabelField("Prompt (added to the preset prompt)");
                prompt = EditorGUILayout.TextArea(prompt, GUILayout.MinHeight(36));
                EditorGUILayout.LabelField("Negative Prompt (added to the preset negatives)");
                negativePrompt = EditorGUILayout.TextArea(negativePrompt, GUILayout.MinHeight(36));
                seed = EditorGUILayout.LongField(new GUIContent("Seed", "-1 = random each run. Reuse a seed to reproduce a result."), seed);
                controller = (AnimatorController)EditorGUILayout.ObjectField(
                    new GUIContent("Animator Controller", "Optional. A state named like the animation type is created/updated. Nothing is removed."),
                    controller, typeof(AnimatorController), false);

                if (settings.mode == AnimationMode.AIPoseRig) DrawPoseSection();

                EditorGUILayout.Space();
                DrawDiagnostics();
                showComfyUI = EditorGUILayout.BeginFoldoutHeaderGroup(showComfyUI, "ComfyUI (AI Pose + Rig, AI Redraw)");
                if (showComfyUI) DrawComfyUISettings();
                EditorGUILayout.EndFoldoutHeaderGroup();

                if (GUILayout.Button("Select Settings Asset (presets, models, workflow)"))
                {
                    Selection.activeObject = settings;
                    EditorGUIUtility.PingObject(settings);
                }
            }

            EditorGUILayout.Space();
            if (busy)
            {
                var rect = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(rect, progress, status);
                if (GUILayout.Button("Cancel")) cts?.Cancel();
            }
            else
            {
                using (new EditorGUI.DisabledScope(source == null))
                {
                    // AI Pose + Rig has its own three steps (Generate Poses / Preview Poses / Build Animation) in the Pose Generation box.
                    if (settings.mode != AnimationMode.AIPoseRig && GUILayout.Button("Generate", GUILayout.Height(32))) Generate();
                }
                if (!string.IsNullOrEmpty(status))
                    EditorGUILayout.HelpBox(status, lastWasError ? MessageType.Error : MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawComfyUISettings()
        {
            EditorGUI.indentLevel++;
            string dir = EditorGUILayout.TextField(new GUIContent("ComfyUI Folder", "Folder containing main.py"), ComfyUIConnectionSettings.ComfyUIDirectory);
            if (dir != ComfyUIConnectionSettings.ComfyUIDirectory) ComfyUIConnectionSettings.ComfyUIDirectory = dir;
            if (GUILayout.Button("Browse for ComfyUI folder..."))
            {
                string picked = EditorUtility.OpenFolderPanel("ComfyUI folder (contains main.py)", dir, "");
                if (!string.IsNullOrEmpty(picked)) ComfyUIConnectionSettings.ComfyUIDirectory = picked;
            }

            string py = EditorGUILayout.TextField(new GUIContent("Python / Launcher", "Empty = auto-detect (python_embeded, venv), then 'python' from PATH"), ComfyUIConnectionSettings.PythonPath);
            if (py != ComfyUIConnectionSettings.PythonPath) ComfyUIConnectionSettings.PythonPath = py;

            string args = EditorGUILayout.TextField(new GUIContent("Launch Args", "{host} and {port} are substituted"), ComfyUIConnectionSettings.LaunchArgs);
            if (args != ComfyUIConnectionSettings.LaunchArgs) ComfyUIConnectionSettings.LaunchArgs = args;

            string url = EditorGUILayout.TextField("ComfyUI URL", ComfyUIConnectionSettings.Url);
            if (url != ComfyUIConnectionSettings.Url && Uri.TryCreate(url, UriKind.Absolute, out _)) ComfyUIConnectionSettings.Url = url;

            int port = EditorGUILayout.IntField("ComfyUI Port", ComfyUIConnectionSettings.Port);
            if (port != ComfyUIConnectionSettings.Port) ComfyUIConnectionSettings.Port = port;

            int startup = EditorGUILayout.IntField("Startup Timeout (s)", ComfyUIConnectionSettings.StartupTimeoutSeconds);
            if (startup != ComfyUIConnectionSettings.StartupTimeoutSeconds) ComfyUIConnectionSettings.StartupTimeoutSeconds = startup;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Test Connection")) TestConnection();
                if (GUILayout.Button(new GUIContent("Test Full Pipeline", "Starts ComfyUI if needed, verifies nodes/models, runs a tiny real AI Redraw generation and a tiny AI Pose + Rig generation (poses, then a rebuild from the saved poses) on a built-in sprite, verifies the frames and Unity import, then stops ComfyUI if Unity started it.")))
                    TestPipeline();
            }
            if (!string.IsNullOrEmpty(connectionResult)) EditorGUILayout.HelpBox(connectionResult, MessageType.None);

            if (!ComfyUIConnectionSettings.TryResolveLaunch(out _, out _, out _, out string launchError))
                EditorGUILayout.HelpBox("Auto-start unavailable: " + launchError + "\n(An already-running ComfyUI is still used.)", MessageType.Warning);
            EditorGUI.indentLevel--;
        }

        private void DrawRigInfo(AIAnimationSettings settings)
        {
            if (settings.mode == AnimationMode.AIPoseRig)
                EditorGUILayout.HelpBox("AI Pose + Rig is in beta. The AI only decides how the character moves; every pixel of the animation still comes from the original sprite through its rig. " +
                    "Poses are generated once and saved; rebuilding the animation from saved poses is fast and does not start ComfyUI.", MessageType.Info);
            if (settings.mode == AnimationMode.AIRedraw)
            {
                EditorGUILayout.HelpBox("AI Redraw is experimental: slower, GPU-dependent, and it changes pixel-level details (face, clothing) from frame to frame. Best for stylised sprites where exact pixels do not matter. For pixel art use Rig.", MessageType.Warning);
                return;
            }
            if (source != null && SourceSprite.TryResolve(source, settings, out SourceSprite src, out _))
            {
                var rig = SpriteRigAsset.FindFor(src, settings);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel("Rig");
                    if (rig != null)
                    {
                        EditorGUILayout.ObjectField(rig, typeof(SpriteRigAsset), false);
                        if (GUILayout.Button("Edit Rig", GUILayout.Width(70))) SpriteRigEditorWindow.Open(source);
                    }
                    else
                    {
                        EditorGUILayout.LabelField("none yet", EditorStyles.miniLabel);
                        if (GUILayout.Button("Open Rig Editor", GUILayout.Width(110))) SpriteRigEditorWindow.Open(source);
                        if (GUILayout.Button("Auto-create", GUILayout.Width(90))) SpriteRigAsset.CreateAuto(src, settings);
                    }
                }
                if (rig == null)
                    EditorGUILayout.HelpBox("This sprite has no rig. The rig says where the neck, arms, legs, weapon and ground are. Generate will ask you to set it up.", MessageType.Info);
            }
        }

        // ---------------------------------------------------------------- AI Pose + Rig

        private AnimationPreset CurrentPreset() =>
            settings.presets.Count > 0 ? settings.presets[Mathf.Clamp(presetIndex, 0, settings.presets.Count - 1)] : null;

        private void DrawPoseSection()
        {
            var preset = CurrentPreset();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Pose Generation", EditorStyles.boldLabel);
            AIPoseAsset saved = null;
            if (source != null && preset != null && SourceSprite.TryResolve(source, settings, out SourceSprite src, out _))
                saved = AIPoseAsset.FindFor(src, settings, preset.name);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Saved poses");
                if (saved != null) EditorGUILayout.ObjectField(saved, typeof(AIPoseAsset), false);
                else EditorGUILayout.LabelField("none yet - click Generate Poses", EditorStyles.miniLabel);
            }
            if (saved != null)
            {
                string when = DateTime.TryParse(saved.createdUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t) ? t.ToLocalTime().ToString("g") : "?";
                EditorGUILayout.LabelField($"{saved.FrameCount} frames @ {saved.fps} FPS, seed {saved.seed}, made {when}", EditorStyles.miniLabel);
                EditorGUILayout.HelpBox(saved.MotionLabel + (string.IsNullOrEmpty(saved.contributionSummary) ? "" : "\n" + saved.contributionSummary),
                    saved.isAiMotion && saved.contributionLevel != "Negligible" ? MessageType.Info : MessageType.Warning);
                if (saved.quality != null && saved.quality.valid)
                {
                    var q = saved.quality;
                    showValidationReport = EditorGUILayout.Foldout(showValidationReport,
                        $"Validation report: {q.framesCorrected}/{q.frames} frames corrected ({q.correctedPercent:0}%), {q.framesRejected} rejected; max foot step {q.maxFootStep:0.0} px", true);
                    if (showValidationReport)
                        EditorGUILayout.HelpBox(
                            $"These are the FINAL poses (validated, constrained, smoothed); the animation is built from them as they are.\n" +
                            $"AI vs procedural: mean {q.meanDeviationDegrees:0.0} deg, max joint deviation {q.maxDeviationDegrees:0.0} deg ({q.maxDeviationWhere})\n" +
                            $"Max foot displacement {q.maxFootStep:0.0} px/frame (AI source {q.sourceMaxFootStep:0.0}, limit {q.footStepLimit:0.0}); max root displacement {q.maxRootStep:0.0} px/frame (AI source {q.sourceMaxRootStep:0.0})\n" +
                            $"Largest joint change by validation/constraints {q.maxChangeFromSourceDegrees:0} deg ({q.maxChangeWhere}); largest single-frame spike {q.maxSpikeDegrees:0} deg (AI source {q.sourceMaxSpikeDegrees:0})\n" +
                            string.Join("\n", q.corrections.ConvertAll(c => $"- {c.name}: {c.values} value(s), {c.frames} frame(s)")), MessageType.None);
                }
            }

            // Pose backend: SDPose is the only one that produces AI motion; the others are labelled as what they are.
            EditorGUI.BeginChangeCheck();
            int backendIndex = EditorGUILayout.Popup(new GUIContent("Pose Backend", "SDPose: AnimateDiff makes a video of a person doing the animation, SDPose reads the pose of every frame, the rig adopts the motion. " +
                "The other two backends are not AI motion."), PoseBackendLabels.IndexOf(settings.poseBackend), PoseBackendLabels.Labels);
            if (EditorGUI.EndChangeCheck()) { settings.poseBackend = PoseBackendLabels.Order[backendIndex]; EditorUtility.SetDirty(settings); }
            bool modelMissing = false;
            if (settings.poseBackend == PoseBackend.SDPose) modelMissing = DrawSdposeStatus();
            else EditorGUILayout.HelpBox(settings.poseBackend == PoseBackend.ProceduralFallback
                ? "Procedural fallback: the poses are formulas with seeded variation, NOT AI-generated motion. No ComfyUI is used."
                : "Legacy 1.2.0 backend: AnimateDiff only nudges a procedural sketch by a few degrees. This is NOT AI-generated motion.", MessageType.Warning);

            settingsSO = settingsSO ?? new SerializedObject(settings);
            settingsSO.Update();
            if (settings.poseBackend == PoseBackend.SDPose)
            {
                EditorGUILayout.PropertyField(settingsSO.FindProperty("poseCandidates"));
                showSdposeAdvanced = EditorGUILayout.Foldout(showSdposeAdvanced, "SDPose video settings", true);
                if (showSdposeAdvanced)
                    foreach (string prop in new[] { "sdposeModelName", "poseVideoWidth", "poseVideoHeight", "poseVideoFrames", "poseVideoSteps", "poseVideoMotionScale", "poseGoodEnoughQuality", "poseMinQuality" })
                        EditorGUILayout.PropertyField(settingsSO.FindProperty(prop));
            }
            else if (settings.poseBackend == PoseBackend.SketchEvidenceLegacy)
            {
                EditorGUILayout.PropertyField(settingsSO.FindProperty("aiPoseGuidance"));
                EditorGUILayout.PropertyField(settingsSO.FindProperty("aiPoseSketchVariation"));
                EditorGUILayout.PropertyField(settingsSO.FindProperty("aiPoseEvidenceWeight"));
            }
            else EditorGUILayout.PropertyField(settingsSO.FindProperty("aiPoseSketchVariation"));
            showPoseCleanup = EditorGUILayout.Foldout(showPoseCleanup, "Pose clean-up (validation, smoothing)", true);
            if (showPoseCleanup) EditorGUILayout.PropertyField(settingsSO.FindProperty("poseCleanup"), true);
            settingsSO.ApplyModifiedProperties();

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(source == null || preset == null || modelMissing))
                {
                    if (GUILayout.Button(new GUIContent("Generate Poses", "Runs the AI once (starts ComfyUI if needed, stops it afterwards), measures the motion, validates it and saves it as a pose asset. Does not create the animation."), GUILayout.Height(28)))
                        GeneratePoses();
                }
                using (new EditorGUI.DisabledScope(source == null || preset == null))
                {
                    if (GUILayout.Button(new GUIContent("Import JSON...", "Import poses made elsewhere: pose JSON, or OpenPose keypoint JSON (SDPose/DWPose output, text-to-motion projected to a side view). Same validation and smoothing as generated poses."), GUILayout.Height(28), GUILayout.Width(110)))
                        ImportPoses();
                }
                using (new EditorGUI.DisabledScope(saved == null || saved.sourceFrames.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent("Re-apply Constraints", "Runs validation, the constraint stage and smoothing again on the AI poses as they came out of the backend, with the current rig and clean-up settings, and stores the result as the final poses. Does not use the AI or ComfyUI."), GUILayout.Height(28), GUILayout.Width(130)))
                        ReapplyConstraints(saved);
                }
                using (new EditorGUI.DisabledScope(saved == null))
                {
                    if (GUILayout.Button(new GUIContent("Preview Poses", "Plays the saved poses on the original sprite in the Sprite Rig Editor before anything is committed."), GUILayout.Height(28)))
                        SpriteRigEditorWindow.OpenPosePreview(source, saved);
                    if (GUILayout.Button(new GUIContent("Build Animation", "Renders the saved poses with the rig, imports the frames and creates the AnimationClip. Does not use the AI or ComfyUI."), GUILayout.Height(28)))
                        BuildAnimation();
                }
            }
        }

        // Returns true when SDPose is known to be missing (Generate Poses is then disabled; nothing falls back to another backend).
        private bool DrawSdposeStatus()
        {
            SDPoseModelStatus status = SDPoseModel.Status(settings);
            if (status == SDPoseModelStatus.Installed)
            {
                EditorGUILayout.HelpBox($"SDPose model installed: {settings.sdposeModelName}", MessageType.None);
                return false;
            }
            if (status == SDPoseModelStatus.Unknown)
            {
                EditorGUILayout.HelpBox("ComfyUI is remote or uses extra model paths: the SDPose model is checked in ComfyUI when you generate.", MessageType.Info);
                return false;
            }
            EditorGUILayout.HelpBox(SDPoseModel.MissingMessage, MessageType.Error);
            if (GUILayout.Button(new GUIContent("Install / Download Model", "Downloads sdpose_wholebody_fp16.safetensors (1.92 GB) from Hugging Face into ComfyUI/models/checkpoints after you confirm. Resumes if interrupted; verified with SHA-256."), GUILayout.Height(26)))
                InstallModel();
            return true;
        }

        private async void InstallModel()
        {
            if (!EditorUtility.DisplayDialog("SDPose model", SDPoseModelInstaller.ConfirmationText, "Download", "Cancel")) return;
            modelInstalling = true; cts = new CancellationTokenSource();
            status = "Starting download..."; progress = 0f; lastWasError = false;
            try
            {
                string path = await SDPoseModelInstaller.InstallAsync((m, p) => { status = m; progress = p; Repaint(); }, cts.Token);
                status = "SDPose model installed: " + path;
            }
            catch (OperationCanceledException) { status = "Download cancelled (the partial file is kept; run it again to resume)."; }
            catch (Exception e) { status = e.Message; lastWasError = true; }
            finally { modelInstalling = false; cts?.Dispose(); cts = null; Repaint(); }
        }

        private void ReapplyConstraints(AIPoseAsset asset)
        {
            try
            {
                if (!SourceSprite.TryResolve(source, settings, out SourceSprite src, out string error)) throw new InvalidOperationException(error);
                var rigAsset = SpriteRigAsset.FindFor(src, settings) ?? throw new InvalidOperationException("The sprite has no rig yet.");
                var preset = CurrentPreset();
                var reference = ProceduralRigPoses.Instance.GetPoses(preset.poseKind, asset.sourceFrames.Count, asset.sourceFrames.Count, preset.rigIntensity);
                var result = asset.Reclean(rigAsset.definition, settings.poseCleanup, reference);
                status = $"Constraints re-applied: {result.Report.CorrectedFrameCount}/{result.Report.FrameCount} frames corrected, max foot step {result.Report.FinalMetrics.MaxFootStep:0.0} px. Build the animation again to use them."; lastWasError = false;
            }
            catch (PoseRejectedException e) { status = "Re-apply rejected: " + e.Message; lastWasError = true; }
            catch (Exception e) { status = "Re-apply failed: " + e.Message; lastWasError = true; }
            Repaint();
        }

        private void ImportPoses()
        {
            string path = EditorUtility.OpenFilePanel("Import poses (pose JSON or OpenPose keypoint JSON)", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!SourceSprite.TryResolve(source, settings, out SourceSprite src, out string error)) throw new InvalidOperationException(error);
                var rigAsset = SpriteRigAsset.FindFor(src, settings) ?? throw new InvalidOperationException("The sprite has no rig yet. Create one in the Sprite Rig Editor first.");
                var asset = AIPoseImporter.ImportFile(path, src, settings, rigAsset, CurrentPreset(), fps);
                status = $"Imported {asset.FrameCount} frames: {AssetDatabase.GetAssetPath(asset)}"; lastWasError = false;
                Selection.activeObject = asset;
            }
            catch (PoseRejectedException e) { status = "Import rejected: " + e.Message; lastWasError = true; }
            catch (Exception e) { status = "Import failed: " + e.Message; lastWasError = true; }
            Repaint();
        }

        private async void GeneratePoses()
        {
            var options = new PoseGenerationOptions
            {
                Source = source,
                PresetName = CurrentPreset().name,
                Frames = frames,
                Fps = fps,
                Loop = loop,
                ExtraPrompt = prompt,
                ExtraNegativePrompt = negativePrompt,
                Seed = seed,
            };
            cts = new CancellationTokenSource();
            status = "Starting..."; progress = 0f; lastWasError = false;
            void OnProgress(string message, float value) { status = message; progress = value; Repaint(); }

            PoseGenerationOutcome outcome = await AIPoseGenerator.GenerateAsync(options, OnProgress, cts.Token);
            cts.Dispose(); cts = null;

            if (outcome.Success)
            {
                status = $"Poses saved: {AssetDatabase.GetAssetPath(outcome.Asset)} ({outcome.GenerationSeconds:0}s). {outcome.Contribution?.Summary} Preview them, then Build Animation.";
                Debug.Log("[AI Sprite Animation] " + status + "\n" + outcome.Report, outcome.Asset);
            }
            else { status = outcome.Error; lastWasError = !outcome.Cancelled; if (!outcome.Cancelled) Debug.LogError("[AI Sprite Animation] " + outcome.Error); }
            RunDiagnostics(false);
            Repaint();
        }

        private async void BuildAnimation()
        {
            var options = new GenerationOptions
            {
                Source = source,
                PresetName = CurrentPreset().name,
                Frames = frames,
                Fps = fps,
                Loop = loop,
                Controller = controller,
                PoseSource = PoseSource.Saved,
            };
            cts = new CancellationTokenSource();
            status = "Building..."; progress = 0f; lastWasError = false;
            GenerationOutcome outcome = await AIAnimationGenerator.GenerateAsync(options, (m, p) => { status = m; progress = p; Repaint(); }, cts.Token);
            cts.Dispose(); cts = null;
            if (outcome.Success) status = $"Done: {AssetDatabase.GetAssetPath(outcome.Clip)} (built from saved poses in {outcome.BuildSeconds:0.0}s, ComfyUI not used)";
            else { status = outcome.Error; lastWasError = !outcome.Cancelled; }
            AIAnimationMenu.Report(outcome);
            Repaint();
        }

        private void DrawDiagnostics()
        {
            showDiagnostics = EditorGUILayout.BeginFoldoutHeaderGroup(showDiagnostics, "Diagnostics");
            if (showDiagnostics)
            {
                if (diagnosticsRunning) EditorGUILayout.LabelField("Checking...");
                else if (diagnostics != null)
                {
                    string group = null;
                    foreach (var item in diagnostics)
                    {
                        if (item.Group != group) { group = item.Group; EditorGUILayout.LabelField(group + ":", EditorStyles.boldLabel); }
                        string icon = item.Status == DiagnosticStatus.Ok ? "✓" : item.Status == DiagnosticStatus.Error ? "✗" : item.Status == DiagnosticStatus.Warning ? "!" : "i";
                        var style = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = true };
                        string color = item.Status == DiagnosticStatus.Ok ? "#4caf50" : item.Status == DiagnosticStatus.Error ? "#f44336" : item.Status == DiagnosticStatus.Warning ? "#ff9800" : "#9e9e9e";
                        EditorGUILayout.LabelField($"  <color={color}>{icon}</color> {item.Label}", style);
                        if (!string.IsNullOrEmpty(item.Detail))
                            EditorGUILayout.LabelField("      " + item.Detail, new GUIStyle(EditorStyles.miniLabel) { wordWrap = true });
                    }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Refresh")) RunDiagnostics(false);
                    if (GUILayout.Button(new GUIContent("Validate with ComfyUI", "Starts ComfyUI if needed, checks nodes/models/workflow through its API, then stops it again if Unity started it."))) RunDiagnostics(true);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private async void RunDiagnostics(bool startIfNeeded)
        {
            if (diagnosticsRunning || AIAnimationGenerator.IsRunning) return;
            diagnosticsRunning = true;
            Repaint();
            try { diagnostics = await ComfyUIDiagnostics.RunAsync(AIAnimationSettings.GetOrCreate(), startIfNeeded, CancellationToken.None); }
            catch (System.Exception e) { diagnostics = new List<DiagnosticItem> { new DiagnosticItem("Diagnostics", DiagnosticStatus.Error, "Failed", e.Message) }; }
            finally { diagnosticsRunning = false; Repaint(); }
        }

        private void ApplyPreset(AnimationPreset preset)
        {
            frames = Mathf.Clamp(preset.frames, 2, ComfyWorkflowBuilder.MaxFrames);
            fps = preset.fps;
            loop = preset.loop;
        }

        private async void TestPipeline()
        {
            if (pipelineTestRunning || AIAnimationGenerator.IsRunning) return;
            pipelineTestRunning = true;
            cts = new CancellationTokenSource();
            progress = 0f; status = "Starting pipeline test...";
            showDiagnostics = true;
            diagnostics = null;
            Repaint();
            try
            {
                diagnostics = await PipelineSelfTest.RunAsync((m, p) => { status = m; progress = p; Repaint(); }, cts.Token);
            }
            catch (System.Exception e) { diagnostics = new List<DiagnosticItem> { new DiagnosticItem("Pipeline test", DiagnosticStatus.Error, "Crashed", e.Message) }; }
            finally { pipelineTestRunning = false; cts.Dispose(); cts = null; status = ""; Repaint(); }
        }

        private async void TestConnection()
        {
            connectionResult = "Checking...";
            using (var client = new ComfyUIClient(ComfyUIConnectionSettings.Url))
            {
                bool ready = await client.IsReadyAsync(CancellationToken.None, 3000);
                connectionResult = ready ? "ComfyUI is running and reachable." : $"Not reachable at {ComfyUIConnectionSettings.Url} (it will be started automatically when you generate, if configured).";
            }
            Repaint();
        }

        private async void Generate()
        {
            var options = new GenerationOptions
            {
                Source = source,
                PresetName = settings.presets.Count > 0 ? settings.presets[Mathf.Clamp(presetIndex, 0, settings.presets.Count - 1)].name : "",
                Frames = frames,
                Fps = fps,
                Loop = loop,
                ExtraPrompt = prompt,
                ExtraNegativePrompt = negativePrompt,
                Seed = seed,
                Controller = controller,
            };

            cts = new CancellationTokenSource();
            status = "Starting...";
            progress = 0f;
            lastWasError = false;
            void OnProgress(string message, float value)
            {
                status = message;
                progress = value;
                Repaint();
            }

            GenerationOutcome outcome = await AIAnimationGenerator.GenerateAsync(options, OnProgress, cts.Token);
            cts.Dispose();
            cts = null;

            if (outcome.Success) status = $"Done: {AssetDatabase.GetAssetPath(outcome.Clip)}";
            else { status = outcome.Error; lastWasError = !outcome.Cancelled; }
            AIAnimationMenu.Report(outcome);
            RunDiagnostics(false);
            Repaint();
        }

        private void OnDestroy() => cts?.Cancel();
    }
}
