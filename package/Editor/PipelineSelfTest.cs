using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// "Test Full Pipeline": verifies ComfyUI, nodes, models and the workflow, starts ComfyUI if needed, runs a tiny real generation on a
    /// built-in test sprite, verifies the frames and the Unity import, removes the test assets, and stops ComfyUI if Unity started it.
    /// </summary>
    public static class PipelineSelfTest
    {
        private const string TestFolder = "Assets/__AISpriteAnimationSelfTest";
        private const string G = "Pipeline test";

        public static async Task<List<DiagnosticItem>> RunAsync(Action<string, float> progress, CancellationToken ct)
        {
            var items = new List<DiagnosticItem>();
            var settings = AIAnimationSettings.GetOrCreate();
            var clock = System.Diagnostics.Stopwatch.StartNew();

            if (AIAnimationGenerator.IsRunning)
            {
                items.Add(new DiagnosticItem(G, DiagnosticStatus.Error, "A generation is already running."));
                return items;
            }

            // Rig mode needs no ComfyUI: test it first so it is reported even if ComfyUI is not installed.
            progress?.Invoke("Testing Rig mode...", 0.02f);
            items.AddRange(await RunTinyGenerationAsync(settings, AnimationMode.Rig, progress, ct));

            using (var client = new ComfyUIClient(ComfyUIConnectionSettings.Url))
            {
                var manager = new ComfyUIProcessManager(client);
                try
                {
                    // 1-4: verify ComfyUI, start it if needed, wait until ready.
                    progress?.Invoke("Checking ComfyUI...", 0.05f);
                    try
                    {
                        await manager.EnsureRunningAsync(m => progress?.Invoke(m, 0.1f), ct);
                        items.Add(new DiagnosticItem(G, DiagnosticStatus.Ok, manager.StartedByUnity ? "ComfyUI started by Unity" : "Using the running ComfyUI",
                            ComfyUIConnectionSettings.Url));
                    }
                    catch (ComfyUIException e)
                    {
                        items.Add(new DiagnosticItem(G, DiagnosticStatus.Error, "ComfyUI is not available", e.Message));
                        return items;
                    }

                    // 2-3: nodes, models, workflow (live, through the API).
                    progress?.Invoke("Verifying nodes, models and workflow...", 0.2f);
                    var live = new List<DiagnosticItem>();
                    await ComfyUIDiagnostics.CheckLiveAsync(client, settings, live, ct);
                    bool setupOk = true;
                    foreach (var d in live)
                        if (d.Status == DiagnosticStatus.Error) { setupOk = false; items.Add(d); }
                    items.Add(new DiagnosticItem(G, setupOk ? DiagnosticStatus.Ok : DiagnosticStatus.Error,
                        setupOk ? "Required nodes, models and workflow verified" : "Setup problems found (listed above); skipping the generation test"));
                    if (!setupOk) return items;

                    // 5-8: tiny generation on a built-in sprite, verify frames and Unity import.
                    progress?.Invoke("Running a tiny generation...", 0.3f);
                    items.AddRange(await RunTinyGenerationAsync(settings, AnimationMode.AIRedraw, progress, ct));

                    // AI Pose + Rig: generate poses once with the same running ComfyUI, then rebuild from the saved poses.
                    progress?.Invoke("Running a tiny AI Pose + Rig generation...", 0.65f);
                    items.AddRange(await RunTinyGenerationAsync(settings, AnimationMode.AIPoseRig, progress, ct));
                }
                catch (OperationCanceledException)
                {
                    items.Add(new DiagnosticItem(G, DiagnosticStatus.Warning, "Cancelled"));
                }
                catch (Exception e)
                {
                    items.Add(new DiagnosticItem(G, DiagnosticStatus.Error, "Unexpected error", e.GetType().Name + ": " + e.Message));
                }
                finally
                {
                    // 9: only a ComfyUI that Unity started is stopped.
                    if (manager.StartedByUnity)
                    {
                        progress?.Invoke("Stopping ComfyUI...", 0.97f);
                        await manager.ShutdownAsync();
                        items.Add(new DiagnosticItem(G, DiagnosticStatus.Ok, "ComfyUI stopped (Unity started it)"));
                    }
                    else items.Add(new DiagnosticItem(G, DiagnosticStatus.Info, "ComfyUI left running (Unity did not start it)"));
                    DeleteTestAssets();
                }
            }

            bool ok = !items.Exists(i => i.Status == DiagnosticStatus.Error);
            items.Add(new DiagnosticItem(G, ok ? DiagnosticStatus.Ok : DiagnosticStatus.Error, ok ? $"PIPELINE OK ({clock.Elapsed.TotalSeconds:0}s)" : "PIPELINE FAILED"));
            progress?.Invoke("Done.", 1f);
            return items;
        }

        private static async Task<List<DiagnosticItem>> RunTinyGenerationAsync(AIAnimationSettings projectSettings, AnimationMode mode, Action<string, float> progress, CancellationToken ct)
        {
            string G = "Pipeline test: " + mode;
            var items = new List<DiagnosticItem>();
            DeleteTestAssets();
            Directory.CreateDirectory(TestFolder);
            string spritePath = TestFolder + "/selftest_sprite.png";
            File.WriteAllBytes(spritePath, BuildTestSprite());
            AssetDatabase.ImportAsset(spritePath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(spritePath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.spritePixelsPerUnit = 16;
            importer.SaveAndReimport();

            // In-memory copy of the settings: small canvas, few steps, 4 frames, output inside the temporary folder.
            var settings = UnityEngine.Object.Instantiate(projectSettings);
            settings.generationSize = 512;
            settings.outputRoot = TestFolder + "/out";
            settings.tailBufferFrames = 2;
            settings.mode = mode;
            var preset = settings.FindPreset("Walk") ?? settings.presets[0];
            preset.steps = 8;
            if (mode == AnimationMode.AIPoseRig)
            {
                // The SDPose model is a separate 1.9 GB download: without it AI Pose + Rig cannot be tested (reported as a warning, never as a silent pass).
                if (SDPoseModel.Status(settings) == SDPoseModelStatus.Missing)
                {
                    items.Add(new DiagnosticItem(G, DiagnosticStatus.Warning, "AI Pose + Rig was not tested: " + SDPoseModel.MissingMessage));
                    DeleteTestAssets();
                    return items;
                }
                settings.poseBackend = PoseBackend.SDPose;
                settings.poseCandidates = 4;
            }

            var outcome = await AIAnimationGenerator.GenerateAsync(new GenerationOptions
            {
                Source = AssetDatabase.LoadAssetAtPath<Texture2D>(spritePath),
                PresetName = preset.name,
                Frames = 4,
                Fps = 8,
                Loop = true,
                Seed = 1,
                AutoCreateRig = true,
                SettingsOverride = settings,
                PoseSource = mode == AnimationMode.AIPoseRig ? PoseSource.GenerateIfMissing : PoseSource.Saved,
            }, progress, ct);

            if (!outcome.Success)
            {
                items.Add(new DiagnosticItem(G, DiagnosticStatus.Error, "Generation failed", outcome.Error));
                return items;
            }
            items.Add(new DiagnosticItem(G, DiagnosticStatus.Ok, $"Generated {outcome.FrameCount} frames", $"{outcome.GenerationSeconds:0}s"));

            if (mode == AnimationMode.AIPoseRig)
            {
                bool saved = outcome.PoseAsset != null && outcome.PosesGeneratedNow && outcome.PoseAsset.FrameCount == 4;
                items.Add(new DiagnosticItem(G, saved ? DiagnosticStatus.Ok : DiagnosticStatus.Error, "AI poses generated, validated and saved as a pose asset",
                    outcome.PoseAsset != null ? $"{AssetDatabase.GetAssetPath(outcome.PoseAsset)}, generation {outcome.PoseGenerationSeconds:0}s" : ""));

                // Rebuild from the saved poses: must work without the AI.
                var rebuilt = await AIAnimationGenerator.GenerateAsync(new GenerationOptions
                {
                    Source = AssetDatabase.LoadAssetAtPath<Texture2D>(spritePath), PresetName = preset.name, Frames = 4, Fps = 8, Loop = true,
                    SettingsOverride = settings, PoseSource = PoseSource.Saved,
                }, progress, ct);
                bool again = rebuilt.Success && !rebuilt.PosesGeneratedNow && rebuilt.FrameCount == 4;
                items.Add(new DiagnosticItem(G, again ? DiagnosticStatus.Ok : DiagnosticStatus.Error, "Rebuilt the animation from the saved poses (no AI run)",
                    again ? $"{rebuilt.BuildSeconds:0.0}s" : rebuilt.Error));
            }

            // Verify the Unity import: clip, sprites, size, transparency, distinct frames, consistent pivot.
            var clip = outcome.Clip;
            var bindings = clip != null ? AnimationUtility.GetObjectReferenceCurveBindings(clip) : new EditorCurveBinding[0];
            var keys = bindings.Length > 0 ? AnimationUtility.GetObjectReferenceCurve(clip, bindings[0]) : null;
            if (keys == null || keys.Length < 5) { items.Add(new DiagnosticItem(G, DiagnosticStatus.Error, "AnimationClip has no sprite curve")); return items; }
            items.Add(new DiagnosticItem(G, DiagnosticStatus.Ok, "AnimationClip created", $"{keys.Length - 1} frames at {clip.frameRate} fps"));

            var sprites = new List<Sprite>();
            foreach (var k in keys) if (k.value is Sprite sp && !sprites.Contains(sp)) sprites.Add(sp);
            bool pivotsEqual = true, anyTransparent = false, anyOpaque = false;
            var first = sprites[0];
            foreach (var sp in sprites)
            {
                if (Vector2.Distance(sp.pivot / sp.rect.size, first.pivot / first.rect.size) > 0.0001f || sp.rect.size != first.rect.size) pivotsEqual = false;
                var pixels = ReadPixels(sp);
                foreach (var c in pixels) { if (c.a == 0) anyTransparent = true; else anyOpaque = true; }
            }
            bool distinct = false;
            if (sprites.Count > 1)
            {
                var a = ReadPixels(sprites[0]); var b = ReadPixels(sprites[sprites.Count - 1]);
                for (int i = 0; i < a.Length && !distinct; i++) distinct = a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a;
            }
            items.Add(new DiagnosticItem(G, pivotsEqual ? DiagnosticStatus.Ok : DiagnosticStatus.Error, "Frames have the same size and pivot", $"{first.rect.width}x{first.rect.height}px"));
            items.Add(new DiagnosticItem(G, anyTransparent && anyOpaque ? DiagnosticStatus.Ok : DiagnosticStatus.Error, "Frames have a transparent background and a visible character"));
            items.Add(new DiagnosticItem(G, distinct ? DiagnosticStatus.Ok : DiagnosticStatus.Warning, distinct ? "Frames differ (the character moves)" : "First and last frame are identical"));
            return items;
        }

        // Sprite textures are not CPU-readable after import; decode the PNG file itself.
        private static Color32[] ReadPixels(Sprite sprite)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try { tex.LoadImage(File.ReadAllBytes(AssetDatabase.GetAssetPath(sprite))); return tex.GetPixels32(); }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        private static void DeleteTestAssets()
        {
            try
            {
                if (AssetDatabase.IsValidFolder(TestFolder)) AssetDatabase.DeleteAsset(TestFolder);
                else if (Directory.Exists(TestFolder)) Directory.Delete(TestFolder, true);
                if (File.Exists(TestFolder + ".meta")) File.Delete(TestFolder + ".meta");
                AssetDatabase.Refresh();
            }
            catch { /* best effort */ }
        }

        // A small 24x40 pixel-art humanoid (head, hair, torso, arm, legs) so the test needs no project assets.
        private static byte[] BuildTestSprite()
        {
            const int w = 24, h = 40;
            var px = new Color32[w * h];
            void Rect(int x0, int y0, int x1, int y1, Color32 c)
            {
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++) px[y * w + x] = c;
            }
            var skin = new Color32(240, 190, 150, 255);
            var hair = new Color32(90, 50, 30, 255);
            var shirt = new Color32(40, 90, 200, 255);
            var pants = new Color32(50, 50, 70, 255);
            var boots = new Color32(110, 70, 40, 255);
            Rect(9, 28, 14, 35, skin);      // head
            Rect(8, 34, 15, 37, hair);      // hair
            Rect(7, 15, 16, 27, shirt);     // torso
            Rect(4, 17, 6, 26, shirt);      // arm
            Rect(4, 15, 6, 16, skin);       // hand
            Rect(8, 5, 11, 14, pants);      // legs
            Rect(12, 5, 15, 14, pants);
            Rect(7, 2, 11, 4, boots);       // boots
            Rect(12, 2, 16, 4, boots);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply(false);
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            return png;
        }
    }
}
