using UnityEditor.PackageManager;

namespace AISpriteAnimation
{
    /// <summary>Resolves the package's own asset paths, wherever the package is installed (Git, local path, embedded).</summary>
    public static class PackagePaths
    {
        public const string PackageName = "com.limpo.ai-sprite-animation";

        public static string Root
        {
            get
            {
                var info = PackageInfo.FindForAssembly(typeof(PackagePaths).Assembly);
                return info != null ? info.assetPath : "Packages/" + PackageName;
            }
        }

        public static string Version
        {
            get
            {
                var info = PackageInfo.FindForAssembly(typeof(PackagePaths).Assembly);
                return info != null ? info.version : "unknown";
            }
        }

        public static string DefaultWorkflow => Root + "/Workflows/AnimateDiffSprite.json";
    }
}
