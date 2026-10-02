using System.IO;
using UnityEditor;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// The rig of one sprite, saved as an asset (e.g. <c>Hero_Rig.asset</c> next to <c>Hero.png</c>): joint positions, the part each pixel belongs to,
    /// and the ground line. Created automatically or edited in the Sprite Rig Editor (Tools > AI Sprite Animation > Sprite Rig Editor).
    /// Every animation of that character reuses it.
    /// </summary>
    public class SpriteRigAsset : ScriptableObject
    {
        [Tooltip("The sprite this rig was made for (asset path). A rig is only valid for a sprite of the same size.")]
        public string sourceAssetPath;
        [Tooltip("Name of the sliced sprite inside the texture; empty for a single sprite.")]
        public string spriteName;
        public RigDefinition definition = new RigDefinition();

        public static string AssetPathFor(SourceSprite source, AIAnimationSettings settings)
        {
            string folder = string.IsNullOrWhiteSpace(settings.rigAssetFolder) ? Path.GetDirectoryName(source.AssetPath).Replace("\\", "/") : settings.rigAssetFolder.TrimEnd('/');
            return $"{folder}/{source.Name}_Rig.asset";
        }

        /// <summary>The rig saved for this sprite, or null.</summary>
        public static SpriteRigAsset FindFor(SourceSprite source, AIAnimationSettings settings)
        {
            // expected location first, then any rig asset that points at this sprite
            var direct = AssetDatabase.LoadAssetAtPath<SpriteRigAsset>(AssetPathFor(source, settings));
            if (direct != null && direct.Matches(source)) return direct;
            foreach (string guid in AssetDatabase.FindAssets("t:SpriteRigAsset"))
            {
                var rig = AssetDatabase.LoadAssetAtPath<SpriteRigAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (rig != null && rig.Matches(source)) return rig;
            }
            return null;
        }

        public bool Matches(SourceSprite source) =>
            sourceAssetPath == source.AssetPath && (spriteName ?? "") == (source.IsSliced ? source.SpriteName : "")
            && definition != null && definition.IsValidFor(source.Rect.width, source.Rect.height);

        /// <summary>Builds a starting rig from the sprite's shape and saves it. Refine it in the Sprite Rig Editor.</summary>
        public static SpriteRigAsset CreateAuto(SourceSprite source, AIAnimationSettings settings, bool save = true)
        {
            var asset = CreateInstance<SpriteRigAsset>();
            asset.sourceAssetPath = source.AssetPath;
            asset.spriteName = source.IsSliced ? source.SpriteName : "";
            Color32[] px = SpriteFrameProcessor.LoadSpritePixels(source, out int sw, out int sh);
            asset.definition = RigAutoBuilder.Build(px, sw, sh, settings.AutoRigParams());
            if (save)
            {
                string path = AssetPathFor(source, settings);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AssetDatabase.CreateAsset(asset, path);
                AssetDatabase.SaveAssets();
            }
            return asset;
        }

        /// <summary>Marks the asset changed after the editor modified <see cref="definition"/>.</summary>
        public void Commit()
        {
            definition.Flush();
            EditorUtility.SetDirty(this);
        }
    }
}
