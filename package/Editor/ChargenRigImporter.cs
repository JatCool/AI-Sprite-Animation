using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Imports rig data written by an external character generator (format <c>chargen-rig/1</c>: <c>rig/rig.json</c> next to the sprite,
    /// with joints, ground line, a per-pixel part map <c>parts.png</c>, an optional hidden-pixel layer <c>underlay.png</c> and an optional
    /// <c>animation_hints.leg_swing_scale</c>) into the sprite's ordinary <see cref="SpriteRigAsset"/>. Nothing else in the package depends
    /// on this file: a sprite without such data gets its rig exactly as before (Rig Editor / auto-built).
    /// </summary>
    public static class ChargenRigImporter
    {
        public const string Format = "chargen-rig/1";
        private const string LogPrefix = "[AI Sprite Animation] ";

        /// <summary>The generated rig description for a sprite (<c>&lt;sprite folder&gt;/rig/rig.json</c>), or null if there is none.</summary>
        public static string FindRigJson(SourceSprite source)
        {
            if (source == null || source.IsSliced) return null;
            string path = Path.Combine(Path.GetDirectoryName(source.AssetPath) ?? "", "rig", "rig.json").Replace("\\", "/");
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// Used where a rig is needed but the sprite has none yet: imports the generated rig data if it exists and fits the sprite.
        /// Returns null (and the caller does what it always did) when there is no data or it does not fit; the reason is logged.
        /// </summary>
        public static SpriteRigAsset TryImportFor(SourceSprite source, AIAnimationSettings settings, bool save = true)
        {
            string json = FindRigJson(source);
            if (json == null) return null;
            try
            {
                var asset = Import(source, settings, json, save);
                Debug.Log($"{LogPrefix}Imported the generated rig data of '{source.AssetPath}' ({json}){(save ? ": " + AssetDatabase.GetAssetPath(asset) : "")}.", asset);
                return asset;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogPrefix}Generated rig data '{json}' was not used: {e.Message} Falling back to the usual rig.");
                return null;
            }
        }

        /// <summary>
        /// Reads <paramref name="rigJsonPath"/> (or a folder containing <c>rig.json</c>) and writes it into the sprite's rig asset: the existing
        /// asset is updated in place (its GUID is kept) or a new one is created. Throws with a readable message if the data does not fit the sprite.
        /// </summary>
        public static SpriteRigAsset Import(SourceSprite source, AIAnimationSettings settings, string rigJsonPath, bool save = true)
        {
            RigDefinition def = Read(source, rigJsonPath);
            SpriteRigAsset asset = SpriteRigAsset.FindFor(source, settings);
            if (asset == null && save) asset = AssetDatabase.LoadAssetAtPath<SpriteRigAsset>(SpriteRigAsset.AssetPathFor(source, settings));   // e.g. made for another size
            bool created = asset == null;
            if (created) asset = ScriptableObject.CreateInstance<SpriteRigAsset>();
            else Undo.RecordObject(asset, "Import Generated Rig");
            asset.sourceAssetPath = source.AssetPath;
            asset.spriteName = "";
            asset.definition = def;
            asset.Commit();
            if (save)
            {
                if (created)
                {
                    string path = SpriteRigAsset.AssetPathFor(source, settings);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    AssetDatabase.CreateAsset(asset, path);
                }
                AssetDatabase.SaveAssets();
            }
            return asset;
        }

        /// <summary>Parses and checks the generated rig data for <paramref name="source"/> and returns it as a rig definition (nothing is saved).</summary>
        public static RigDefinition Read(SourceSprite source, string rigJsonPath)
        {
            if (Directory.Exists(rigJsonPath)) rigJsonPath = Path.Combine(rigJsonPath, "rig.json");
            if (!File.Exists(rigJsonPath)) throw new FileNotFoundException($"'{rigJsonPath}' does not exist.");
            if (source.IsSliced) throw new InvalidOperationException("Generated rig data describes a whole PNG, not a sprite cut from a sheet.");
            var root = MiniJson.Parse(File.ReadAllText(rigJsonPath)) as Dictionary<string, object>;
            if (root == null) throw new FormatException($"'{rigJsonPath}' is not a JSON object.");
            string format = MiniJson.Path(root, "format") as string;
            if (format != Format) throw new FormatException($"Unsupported rig data format '{format}' (expected '{Format}').");

            // The data belongs to one exact sprite: same size, same pixels.
            int w = source.Rect.width, h = source.Rect.height;
            if (!(MiniJson.Path(root, "sprite", "size") is List<object> size) || size.Count != 2 || ToInt(size[0]) != w || ToInt(size[1]) != h)
                throw new InvalidOperationException($"The rig data was made for a sprite of another size (the sprite is {w}x{h}).");
            string expectedHash = MiniJson.Path(root, "sprite", "sha256") as string;
            if (!string.IsNullOrEmpty(expectedHash) && !string.Equals(expectedHash, Sha256(source.AssetPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The sprite has changed since the rig data was made (sha256 differs). Regenerate the rig data for the current sprite.");

            var def = new RigDefinition(w, h);
            foreach (RigJoint joint in Enum.GetValues(typeof(RigJoint)))
            {
                if (!(MiniJson.Path(root, "joints", joint.ToString()) is Dictionary<string, object> j) || !j.ContainsKey("x") || !j.ContainsKey("y"))
                    throw new FormatException($"Joint '{joint}' is missing.");
                def.SetJoint(joint, new Vector2(ToFloat(j["x"]), ToFloat(j["y"])));
            }
            object ground = MiniJson.Path(root, "sprite", "ground_y");
            if (ground == null) throw new FormatException("sprite.ground_y is missing.");
            def.groundY = ToFloat(ground);

            // Part map: one bit per RigPart (R = bits 0-7, G = bits 8-15), in RigPart order. Bit names are checked so a reordering cannot go unnoticed.
            if (MiniJson.Path(root, "parts", "bits") is List<object> bits)
            {
                if (bits.Count != RigDefinition.PartCount) throw new FormatException($"parts.bits lists {bits.Count} parts, the rig has {RigDefinition.PartCount}.");
                for (int i = 0; i < bits.Count; i++)
                    if ((bits[i] as string) != ((RigPart)i).ToString()) throw new FormatException($"parts.bits[{i}] is '{bits[i]}', expected '{(RigPart)i}'.");
            }
            string dir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(rigJsonPath)));   // file paths in rig.json are relative to the sprite's folder
            string partsFile = MiniJson.Path(root, "parts", "file") as string ?? "rig/parts.png";
            Color32[] parts = LoadPng(Path.Combine(dir, partsFile), w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color32 c = parts[(h - 1 - y) * w + x];   // texture rows are bottom-up, the rig is y down
                    def.SetMask(x, y, c.a == 0 ? (ushort)0 : (ushort)(c.r | (c.g << 8)));
                }
            def.Flush();

            // Optional hidden-pixel layer.
            string underlayFile = MiniJson.Path(root, "underlay", "file") as string;
            if (!string.IsNullOrEmpty(underlayFile))
            {
                Color32[] u = LoadPng(Path.Combine(dir, underlayFile), w, h);
                var yDown = new Color32[w * h];
                for (int y = 0; y < h; y++) Array.Copy(u, (h - 1 - y) * w, yDown, y * w, w);
                def.SetUnderlay(yDown);
            }

            // Optional animation hint; anything unusable leaves the default (1 = unchanged legs).
            object legScale = MiniJson.Path(root, "animation_hints", "leg_swing_scale");
            if (legScale != null)
            {
                float k = ToFloat(legScale);
                if (k > 0f && k <= 1f) def.legSwingScale = k;
                else Debug.LogWarning($"{LogPrefix}Ignoring animation_hints.leg_swing_scale = {k} in '{rigJsonPath}' (expected 0 < value <= 1).");
            }
            return def;
        }

        // Exact RGBA from the file (no import settings involved: the PNG may be imported as a filtered/compressed texture).
        private static Color32[] LoadPng(string path, int w, int h)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"'{path}' does not exist.");
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(path))) throw new FormatException($"Could not decode '{path}'.");
                if (tex.width != w || tex.height != h) throw new InvalidOperationException($"'{Path.GetFileName(path)}' is {tex.width}x{tex.height}, the sprite is {w}x{h}.");
                return tex.GetPixels32();
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        private static float ToFloat(object o) => o is double d ? (float)d : float.Parse(Convert.ToString(o, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        private static int ToInt(object o) => Mathf.RoundToInt(ToFloat(o));

        // ---------------------------------------------------------------- menu

        [MenuItem("Assets/AI/Import Generated Rig Data", false, 2011)]
        private static void ImportSelected()
        {
            var settings = AIAnimationSettings.GetOrCreate();
            if (!SourceSprite.TryResolve(Selection.activeObject, settings, out SourceSprite source, out string error)) { EditorUtility.DisplayDialog("Import Generated Rig Data", error, "OK"); return; }
            string json = FindRigJson(source);
            if (json == null) { EditorUtility.DisplayDialog("Import Generated Rig Data", $"There is no generated rig data (rig/rig.json) next to '{source.AssetPath}'.", "OK"); return; }
            bool exists = SpriteRigAsset.FindFor(source, settings) != null;
            if (exists && !EditorUtility.DisplayDialog("Import Generated Rig Data",
                    $"'{source.Name}' already has a rig. Replace its joints, parts, underlay and leg swing scale with the generated data in '{json}'? (Undo restores it.)", "Replace", "Cancel"))
                return;
            try
            {
                var asset = Import(source, settings, json);
                Debug.Log($"{LogPrefix}Imported '{json}' into {AssetDatabase.GetAssetPath(asset)}.", asset);
                Selection.activeObject = asset;
            }
            catch (Exception e) { EditorUtility.DisplayDialog("Import Generated Rig Data", e.Message, "OK"); }
        }

        [MenuItem("Assets/AI/Import Generated Rig Data", true)]
        private static bool ValidateImportSelected() => SourceSprite.IsValidSelection(Selection.activeObject);
    }
}
