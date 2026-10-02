using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>Writes frames into the project, imports them as sprites (settings copied from the source) and builds the AnimationClip.</summary>
    public static class SpriteAnimationImporter
    {
        public static string GetOutputFolder(AIAnimationSettings settings, SourceSprite source, string animationName)
            => $"{settings.outputRoot.TrimEnd('/')}/{source.Name}/{SourceSprite.SanitizeName(animationName)}";

        /// <summary>Writes input.png + frame_###.png into <paramref name="folder"/>, overwriting in place so GUIDs and clip references survive regeneration.</summary>
        public static List<Sprite> ImportFrames(string folder, byte[] inputPng, IReadOnlyList<byte[]> framePngs, SourceSprite source, AIAnimationSettings settings, Vector2 pivot01)
        {
            Directory.CreateDirectory(folder);

            var paths = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                File.WriteAllBytes($"{folder}/input.png", inputPng);
                for (int i = 0; i < framePngs.Count; i++)
                {
                    string path = $"{folder}/frame_{i:000}.png";
                    File.WriteAllBytes(path, framePngs[i]);
                    paths.Add(path);
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }

            // Remove frames left over from an earlier, longer generation.
            foreach (string old in Directory.GetFiles(folder, "frame_*.png"))
            {
                string assetPath = old.Replace('\\', '/');
                if (!paths.Contains(assetPath)) AssetDatabase.DeleteAsset(assetPath);
            }

            // input.png is the exact image that was sent to the model (kept for inspection).
            AssetDatabase.ImportAsset($"{folder}/input.png", ImportAssetOptions.ForceSynchronousImport);

            var sprites = new List<Sprite>(paths.Count);
            foreach (string path in paths)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                ApplySpriteSettings(path, source, settings, pivot01);
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null) throw new InvalidOperationException($"Imported frame is not a sprite: {path}");
                sprites.Add(sprite);
            }
            return sprites;
        }

        private static void ApplySpriteSettings(string path, SourceSprite source, AIAnimationSettings settings, Vector2 pivot01)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            var src = source.Importer;

            var ts = new TextureImporterSettings();
            src.ReadTextureSettings(ts);
            ts.textureType = TextureImporterType.Sprite;
            ts.spriteMode = (int)SpriteImportMode.Single;
            ts.spriteAlignment = (int)SpriteAlignment.Custom;
            ts.spritePivot = pivot01;   // same on every frame, derived from the source pivot
            ts.alphaIsTransparency = true;

            importer.textureType = TextureImporterType.Sprite;
            importer.SetTextureSettings(ts);
            bool copy = settings.copyImportSettingsFromSource;
            FilterMode filter = copy ? src.filterMode : settings.filterMode;
            importer.spritePixelsPerUnit = copy ? src.spritePixelsPerUnit : settings.pixelsPerUnit;
            importer.filterMode = filter;
            importer.mipmapEnabled = src.mipmapEnabled;
            importer.wrapMode = src.wrapMode;
            importer.sRGBTexture = src.sRGBTexture;
            importer.alphaIsTransparency = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            // Pixel art (point filtered) must never be compressed.
            importer.textureCompression = filter == FilterMode.Point ? TextureImporterCompression.Uncompressed : (copy ? src.textureCompression : TextureImporterCompression.Uncompressed);
            importer.SaveAndReimport();
        }

        /// <summary>Creates or updates (in place, keeping its GUID) the clip at <paramref name="clipPath"/>.</summary>
        public static AnimationClip CreateOrUpdateClip(string clipPath, IReadOnlyList<Sprite> sprites, int fps, bool loop, string bindingPath)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
            {
                clip = new AnimationClip();
                AssetDatabase.CreateAsset(clip, clipPath);
            }
            else
            {
                foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    AnimationUtility.SetObjectReferenceCurve(clip, b, null);
            }

            clip.frameRate = fps;
            clip.wrapMode = loop ? WrapMode.Loop : WrapMode.Once;

            // One key per frame, plus a closing key so the last frame is held for a full frame duration.
            var keys = new ObjectReferenceKeyframe[sprites.Count + 1];
            for (int i = 0; i < sprites.Count; i++)
                keys[i] = new ObjectReferenceKeyframe { time = i / (float)fps, value = sprites[i] };
            keys[sprites.Count] = new ObjectReferenceKeyframe { time = sprites.Count / (float)fps, value = sprites[sprites.Count - 1] };

            var binding = EditorCurveBinding.PPtrCurve(bindingPath ?? "", typeof(SpriteRenderer), "m_Sprite");
            AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);

            var clipSettings = AnimationUtility.GetAnimationClipSettings(clip);
            clipSettings.loopTime = loop;
            clipSettings.startTime = 0f;
            clipSettings.stopTime = sprites.Count / (float)fps;
            AnimationUtility.SetAnimationClipSettings(clip, clipSettings);

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            return clip;
        }

        /// <summary>
        /// Adds the clip to an existing controller without destroying anything: a state with the animation's name is reused only if it
        /// is empty or already holds a generated clip; if it holds your own motion, a separate "&lt;name&gt; (AI)" state is used instead.
        /// Transitions are never touched.
        /// </summary>
        public static string AssignToController(AnimatorController controller, string stateName, AnimationClip clip, string generatedRoot)
        {
            if (controller.layers.Length == 0) controller.AddLayer("Base Layer");
            var stateMachine = controller.layers[0].stateMachine;

            AnimatorState Find(string n) => stateMachine.states.Select(s => s.state).FirstOrDefault(s => s.name == n);
            bool IsGenerated(Motion m)
            {
                string path = m != null ? AssetDatabase.GetAssetPath(m) : null;
                return !string.IsNullOrEmpty(path) && path.StartsWith(generatedRoot.TrimEnd('/') + "/");
            }

            string target = stateName;
            var state = Find(target);
            if (state != null && state.motion != null && !IsGenerated(state.motion))
            {
                target = stateName + " (AI)";
                state = Find(target);
            }
            if (state == null) state = stateMachine.AddState(target);
            state.motion = clip;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return target;
        }
    }
}
