using System.IO;
using UnityEditor;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Finds the front-view sprite of a character for the Rotate animation. The package never invents a front view: it uses the character's own art.
    /// Order: the project setting <see cref="AIAnimationSettings.frontSpritePath"/>, then a sprite next to the side sprite named
    /// "base_front", "base_south", "base_down" or "name_front" (base = the name without a trailing _east/_west/_side/_right/_left), .png.
    /// </summary>
    public static class FrontSprite
    {
        private static readonly string[] SideSuffixes = { "_east", "_west", "_side", "_right", "_left" };
        private static readonly string[] FrontSuffixes = { "_front", "_south", "_down" };

        public static string FindPath(SourceSprite source, AIAnimationSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.frontSpritePath))
                return File.Exists(settings.frontSpritePath) ? settings.frontSpritePath : null;
            string folder = Path.GetDirectoryName(source.AssetPath).Replace("\\", "/");
            string name = Path.GetFileNameWithoutExtension(source.AssetPath), baseName = name;
            foreach (string suffix in SideSuffixes)
                if (name.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase)) { baseName = name.Substring(0, name.Length - suffix.Length); break; }
            foreach (string suffix in FrontSuffixes)
                foreach (string stem in new[] { baseName, name })
                {
                    string candidate = $"{folder}/{stem}{suffix}.png";
                    if (File.Exists(candidate)) return candidate;
                }
            return null;
        }

        public const string MissingMessage =
            "Rotate shows the character from the front, and the package does not invent art: add the character's front-view sprite next to the side sprite " +
            "(for player_east.png: player_front.png, same canvas size and ground line, Point filter, Sprite mode Single) or set 'Front Sprite Path' in the AI Sprite Animation settings.";

        public static bool TryLoad(SourceSprite source, AIAnimationSettings settings, out Color32[] pixels, out int width, out int height, out string error)
        {
            pixels = null; width = height = 0; error = null;
            string path = FindPath(source, settings);
            if (path == null) { error = MissingMessage; return false; }
            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (!SourceSprite.TryResolve(obj, settings, out SourceSprite front, out string resolveError)) { error = $"Front sprite '{path}': {resolveError}"; return false; }
            pixels = SpriteFrameProcessor.LoadSpritePixels(front, out width, out height);
            return true;
        }
    }
}
