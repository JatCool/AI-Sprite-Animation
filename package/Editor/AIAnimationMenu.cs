using System.Threading;
using UnityEditor;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>Menu entries: right-click a sprite > AI > Generate Animation > [type], and Tools > AI Sprite Animation.</summary>
    public static class AIAnimationMenu
    {
        private const string Title = "AI Sprite Animation";

        [MenuItem("Assets/AI/Generate Animation/Idle", false, 2000)] private static void Idle() => RunPreset("Idle");
        [MenuItem("Assets/AI/Generate Animation/Walk", false, 2001)] private static void Walk() => RunPreset("Walk");
        [MenuItem("Assets/AI/Generate Animation/Run", false, 2002)] private static void Run() => RunPreset("Run");
        [MenuItem("Assets/AI/Generate Animation/Attack", false, 2003)] private static void Attack() => RunPreset("Attack");

        [MenuItem("Assets/AI/Generate Animation/Idle", true)]
        [MenuItem("Assets/AI/Generate Animation/Walk", true)]
        [MenuItem("Assets/AI/Generate Animation/Run", true)]
        [MenuItem("Assets/AI/Generate Animation/Attack", true)]
        private static bool ValidateSelection() => !AIAnimationGenerator.IsRunning && SourceSprite.IsValidSelection(Selection.activeObject);

        [MenuItem("Tools/AI Sprite Animation/Generate Animation")]
        private static void OpenWindow() => AIAnimationWindow.Open();

        [MenuItem("Assets/AI/Sprite Rig Editor", false, 2010)]
        private static void OpenRigEditorFromAssets() => SpriteRigEditorWindow.Open(Selection.activeObject);

        [MenuItem("Assets/AI/Sprite Rig Editor", true)]
        private static bool ValidateRigEditor() => SourceSprite.IsValidSelection(Selection.activeObject);

        [MenuItem("Tools/AI Sprite Animation/Select Settings Asset")]
        private static void SelectSettings()
        {
            var settings = AIAnimationSettings.GetOrCreate();
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }

        [MenuItem("Tools/AI Sprite Animation/Stop ComfyUI Started By Unity")]
        private static void StopComfyUI()
        {
            ComfyUIProcessManager.ShutdownAll();
            ComfyUIProcessManager.CleanupOrphanFromPidFile();
            Debug.Log("[AI Animation] Stopped any ComfyUI process started by Unity.");
        }

        private static void RunPreset(string presetName)
        {
            var preset = AIAnimationSettings.GetOrCreate().FindPreset(presetName);
            if (preset == null)
            {
                EditorUtility.DisplayDialog(Title, $"Preset '{presetName}' does not exist in the settings asset.", "OK");
                return;
            }
            RunWithProgressBar(new GenerationOptions
            {
                Source = Selection.activeObject,
                PresetName = preset.name,
                Frames = preset.frames,
                Fps = preset.fps,
                Loop = preset.loop,
            });
        }

        private static async void RunWithProgressBar(GenerationOptions options)
        {
            using (var cts = new CancellationTokenSource())
            {
                void OnProgress(string message, float value)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(Title, message, value)) cts.Cancel();
                }

                GenerationOutcome outcome;
                try { outcome = await AIAnimationGenerator.GenerateAsync(options, OnProgress, cts.Token); }
                finally { EditorUtility.ClearProgressBar(); }

                Report(outcome);
            }
        }

        public static void Report(GenerationOutcome outcome)
        {
            if (outcome.Success)
            {
                Debug.Log($"[AI Animation] Created {AssetDatabase.GetAssetPath(outcome.Clip)}", outcome.Clip);
                Selection.activeObject = outcome.Clip;
                EditorGUIUtility.PingObject(outcome.Clip);
            }
            else if (outcome.Cancelled) Debug.LogWarning("[AI Animation] Generation cancelled.");
            else
            {
                Debug.LogError("[AI Animation] Generation failed: " + outcome.Error);
                EditorUtility.DisplayDialog(Title, "Generation failed:\n\n" + outcome.Error, "OK");
            }
        }
    }
}
