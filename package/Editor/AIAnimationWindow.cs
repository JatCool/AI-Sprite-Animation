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
            bool busy = AIAnimationGenerator.IsRunning || pipelineTestRunning;
            scroll = EditorGUILayout.BeginScrollView(scroll);

            using (new EditorGUI.DisabledScope(busy))
            {
                EditorGUILayout.LabelField("Animation", EditorStyles.boldLabel);
                source = EditorGUILayout.ObjectField("Source Sprite", source, typeof(UnityEngine.Object), false);
                if (source != null && !SourceSprite.IsValidSelection(source)) source = null;

                EditorGUI.BeginChangeCheck();
                int newMode = EditorGUILayout.Popup(new GUIContent("Animation Method", "Rig moves the sprite's own pixels with a bone hierarchy: exact identity, instant, no AI. AI Redraw lets AnimateDiff redraw each frame: slower, needs a GPU and ComfyUI, and changes pixel-level details."),
                    (int)settings.mode, AnimationModeLabels.Labels);
                if (EditorGUI.EndChangeCheck())
                {
                    settings.mode = (AnimationMode)newMode;
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

                EditorGUILayout.Space();
                DrawDiagnostics();
                showComfyUI = EditorGUILayout.BeginFoldoutHeaderGroup(showComfyUI, "ComfyUI (AIRedraw mode)");
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
                    if (GUILayout.Button("Generate", GUILayout.Height(32))) Generate();
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
                if (GUILayout.Button(new GUIContent("Test Full Pipeline", "Starts ComfyUI if needed, verifies nodes/models, runs a tiny real generation on a built-in sprite, verifies the frames and Unity import, then stops ComfyUI if Unity started it.")))
                    TestPipeline();
            }
            if (!string.IsNullOrEmpty(connectionResult)) EditorGUILayout.HelpBox(connectionResult, MessageType.None);

            if (!ComfyUIConnectionSettings.TryResolveLaunch(out _, out _, out _, out string launchError))
                EditorGUILayout.HelpBox("Auto-start unavailable: " + launchError + "\n(An already-running ComfyUI is still used.)", MessageType.Warning);
            EditorGUI.indentLevel--;
        }

        private void DrawRigInfo(AIAnimationSettings settings)
        {
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
