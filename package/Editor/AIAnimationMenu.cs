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
        [MenuItem("Assets/AI/Generate Animation/Rotate", false, 2004)] private static void Rotate() => RunPreset("Rotate");
        [MenuItem("Assets/AI/Generate Animation/Jump", false, 2005)] private static void Jump() => RunPreset("Jump");
        [MenuItem("Assets/AI/Generate Animation/Sit", false, 2006)] private static void Sit() => RunPreset("Sit");
        [MenuItem("Assets/AI/Generate Animation/Crouch", false, 2007)] private static void Crouch() => RunPreset("Crouch");
        [MenuItem("Assets/AI/Generate Animation/CrouchWalk", false, 2008)] private static void CrouchWalk() => RunPreset("CrouchWalk");
        [MenuItem("Assets/AI/Generate Animation/WakeUp", false, 2009)] private static void WakeUp() => RunPreset("WakeUp");
        [MenuItem("Assets/AI/Generate Animation/Ignite", false, 2010)] private static void Ignite() => RunPreset("Ignite");
        [MenuItem("Assets/AI/Generate Animation/LowerHand", false, 2010)] private static void LowerHand() => RunPreset("LowerHand");
        [MenuItem("Assets/AI/Generate Animation/Dash", false, 2010)] private static void Dash() => RunPreset("Dash");
        [MenuItem("Assets/AI/Generate Animation/Hurt", false, 2010)] private static void Hurt() => RunPreset("Hurt");
        [MenuItem("Assets/AI/Generate Animation/LeanOnColumn", false, 2010)] private static void LeanOnColumn() => RunPreset("LeanOnColumn");
        [MenuItem("Assets/AI/Generate Animation/ReachColumn", false, 2010)] private static void ReachColumn() => RunPreset("ReachColumn");
        [MenuItem("Assets/AI/Generate Animation/ClimbDown", false, 2010)] private static void ClimbDown() => RunPreset("ClimbDown");
        [MenuItem("Assets/AI/Generate Animation/StaggerShock", false, 2010)] private static void StaggerShock() => RunPreset("StaggerShock");
        [MenuItem("Assets/AI/Generate Animation/Spit", false, 2010)] private static void Spit() => RunPreset("Spit");
        [MenuItem("Assets/AI/Generate Animation/RearUp", false, 2010)] private static void RearUp() => RunPreset("RearUp");
        [MenuItem("Assets/AI/Generate Animation/Trot", false, 2010)] private static void Trot() => RunPreset("Trot");
        [MenuItem("Assets/AI/Generate Animation/Recoil", false, 2010)] private static void Recoil() => RunPreset("Recoil");
        [MenuItem("Assets/AI/Generate Animation/Fall", false, 2010)] private static void Fall() => RunPreset("Fall");

        [MenuItem("Assets/AI/Generate Animation/Idle", true)]
        [MenuItem("Assets/AI/Generate Animation/Walk", true)]
        [MenuItem("Assets/AI/Generate Animation/Run", true)]
        [MenuItem("Assets/AI/Generate Animation/Attack", true)]
        [MenuItem("Assets/AI/Generate Animation/Rotate", true)]
        [MenuItem("Assets/AI/Generate Animation/Jump", true)]
        [MenuItem("Assets/AI/Generate Animation/Sit", true)]
        [MenuItem("Assets/AI/Generate Animation/Crouch", true)]
        [MenuItem("Assets/AI/Generate Animation/CrouchWalk", true)]
        [MenuItem("Assets/AI/Generate Animation/WakeUp", true)]
        [MenuItem("Assets/AI/Generate Animation/Ignite", true)]
        [MenuItem("Assets/AI/Generate Animation/LowerHand", true)]
        [MenuItem("Assets/AI/Generate Animation/Dash", true)]
        [MenuItem("Assets/AI/Generate Animation/Hurt", true)]
        [MenuItem("Assets/AI/Generate Animation/LeanOnColumn", true)]
        [MenuItem("Assets/AI/Generate Animation/ReachColumn", true)]
        [MenuItem("Assets/AI/Generate Animation/ClimbDown", true)]
        [MenuItem("Assets/AI/Generate Animation/StaggerShock", true)]
        [MenuItem("Assets/AI/Generate Animation/Spit", true)]
        [MenuItem("Assets/AI/Generate Animation/RearUp", true)]
        [MenuItem("Assets/AI/Generate Animation/Trot", true)]
        [MenuItem("Assets/AI/Generate Animation/Recoil", true)]
        [MenuItem("Assets/AI/Generate Animation/Fall", true)]
        private static bool ValidateSelection() => !AIAnimationGenerator.IsRunning && SourceSprite.IsValidSelection(Selection.activeObject);

        [MenuItem("Tools/AI Sprite Animation/Generate Animation")]
        private static void OpenWindow() => AIAnimationWindow.Open();

        [MenuItem("Assets/AI/Sprite Rig Editor", false, 2011)]
        private static void OpenRigEditorFromAssets() => SpriteRigEditorWindow.Open(Selection.activeObject);

        [MenuItem("Assets/AI/Sprite Rig Editor", true)]
        private static bool ValidateRigEditor() => SourceSprite.IsValidSelection(Selection.activeObject);

        /// <summary>Builds the selected sprite's rig from "&lt;Sprite&gt;_joints.txt" next to it (format: -aiRigJoints): for views the auto rig cannot read, such as a back view.</summary>
        [MenuItem("Assets/AI/Rig From Joints File", false, 2012)]
        private static void RigFromJointsFile()
        {
            var settings = AIAnimationSettings.GetOrCreate();
            if (!SourceSprite.TryResolve(Selection.activeObject, settings, out SourceSprite source, out string error)) { EditorUtility.DisplayDialog(Title, error, "OK"); return; }
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            string joints = System.IO.Path.ChangeExtension(path, null) + "_joints.txt";
            if (!System.IO.File.Exists(joints)) { EditorUtility.DisplayDialog(Title, "No joints file: " + joints, "OK"); return; }
            Debug.Log("[AI Sprite Animation] Rig written: " + AssetDatabase.GetAssetPath(BatchRunner.RigFromJoints(source, settings, joints)));
        }

        [MenuItem("Assets/AI/Rig From Joints File", true)]
        private static bool ValidateRigFromJoints() => SourceSprite.IsValidSelection(Selection.activeObject);

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
                PoseSource = PoseSource.GenerateIfMissing,
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
