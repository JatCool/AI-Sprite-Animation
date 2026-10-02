using System.IO;
using UnityEditor;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>A validated source sprite: asset path, pixel rect inside the texture, pivot and importer.</summary>
    public sealed class SourceSprite
    {
        public string AssetPath;
        public string Name;
        public RectInt Rect;          // pixel rect inside the texture (bottom-left origin)
        public Vector2 Pivot01;       // pivot normalised to the rect
        public TextureImporter Importer;

        public static bool IsValidSelection(Object obj) => obj is Texture2D || obj is Sprite;

        /// <summary>Resolves a Texture2D / Sprite object. Returns false with an error message if unusable.</summary>
        public static bool TryResolve(Object obj, AIAnimationSettings settings, out SourceSprite result, out string error)
        {
            result = null;
            error = null;
            if (obj == null) { error = "No source sprite selected."; return false; }
            if (!IsValidSelection(obj)) { error = $"'{obj.name}' is not a texture or sprite."; return false; }

            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/") || !File.Exists(path))
            {
                error = "The source sprite must be an asset inside this project's Assets folder.";
                return false;
            }
            if (path.StartsWith(settings.outputRoot.TrimEnd('/') + "/"))
            {
                error = "The source is already inside the generated AI animations folder. Pick the original artwork.";
                return false;
            }

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) { error = $"'{path}' is not a texture asset."; return false; }

            RectInt rect;
            Vector2 pivot;
            if (obj is Sprite sprite)
            {
                rect = new RectInt(Mathf.RoundToInt(sprite.rect.x), Mathf.RoundToInt(sprite.rect.y),
                    Mathf.RoundToInt(sprite.rect.width), Mathf.RoundToInt(sprite.rect.height));
                pivot = new Vector2(sprite.pivot.x / sprite.rect.width, sprite.pivot.y / sprite.rect.height);
            }
            else
            {
                if (importer.spriteImportMode == SpriteImportMode.Multiple)
                {
                    error = $"'{path}' is a multi-sprite sheet. Select one of its sliced sprites instead of the whole texture.";
                    return false;
                }
                var tis = new TextureImporterSettings();
                importer.ReadTextureSettings(tis);
                pivot = tis.spriteAlignment == (int)SpriteAlignment.Custom ? tis.spritePivot : AlignmentToPivot((SpriteAlignment)tis.spriteAlignment);
                var tex = (Texture2D)obj;
                // Use the file's real size: Texture2D.width can be reduced by maxTextureSize.
                if (!TryReadPngSize(path, out int w, out int h)) { w = tex.width; h = tex.height; }
                rect = new RectInt(0, 0, w, h);
            }

            if (rect.width < 4 || rect.height < 4) { error = "The sprite is too small (min 4x4 pixels)."; return false; }

            result = new SourceSprite
            {
                AssetPath = path,
                Name = SanitizeName(obj is Sprite ? Path.GetFileNameWithoutExtension(path) + (obj.name != Path.GetFileNameWithoutExtension(path) ? "_" + obj.name : "") : obj.name),
                Rect = rect,
                Pivot01 = pivot,
                Importer = importer,
            };
            return true;
        }

        public static string SanitizeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }

        private static Vector2 AlignmentToPivot(SpriteAlignment a)
        {
            switch (a)
            {
                case SpriteAlignment.TopLeft: return new Vector2(0f, 1f);
                case SpriteAlignment.TopCenter: return new Vector2(0.5f, 1f);
                case SpriteAlignment.TopRight: return new Vector2(1f, 1f);
                case SpriteAlignment.LeftCenter: return new Vector2(0f, 0.5f);
                case SpriteAlignment.RightCenter: return new Vector2(1f, 0.5f);
                case SpriteAlignment.BottomLeft: return new Vector2(0f, 0f);
                case SpriteAlignment.BottomCenter: return new Vector2(0.5f, 0f);
                case SpriteAlignment.BottomRight: return new Vector2(1f, 0f);
                default: return new Vector2(0.5f, 0.5f);
            }
        }

        private static bool TryReadPngSize(string path, out int w, out int h)
        {
            w = h = 0;
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var header = new byte[24];
                    if (fs.Read(header, 0, 24) < 24 || header[1] != 'P' || header[2] != 'N' || header[3] != 'G') return false;
                    w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                    h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                    return w > 0 && h > 0;
                }
            }
            catch { return false; }
        }
    }
}
