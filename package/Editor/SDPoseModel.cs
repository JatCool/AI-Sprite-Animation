using System.IO;

namespace AISpriteAnimation
{
    public enum SDPoseModelStatus
    {
        Installed,
        Missing,
        /// <summary>ComfyUI is remote, or uses extra model paths: only ComfyUI itself can tell.</summary>
        Unknown,
    }

    /// <summary>Facts about the SDPose model (see Documentation~/sdpose-model.md) and a cheap on-disk check for it.</summary>
    public static class SDPoseModel
    {
        public const string DefaultFileName = "sdpose_wholebody_fp16.safetensors";
        public const string SourcePage = "https://huggingface.co/Comfy-Org/SDPose";
        public const string DownloadUrl = "https://huggingface.co/Comfy-Org/SDPose/resolve/main/checkpoints/sdpose_wholebody_fp16.safetensors";
        public const long SizeBytes = 1916645792L;
        public const string Sha256 = "63d01f9a7494560693b24767f4469d59c9d3266b31ff0a253e74d1e611442721";
        public const string License = "MIT (original SDPose-Wholebody by teemosliang; built on Stable Diffusion v2 weights)";

        public const string MissingMessage = "SDPose model is required for AI Pose + Rig. It is not installed in ComfyUI (models/checkpoints). " +
            "Use \"Install / Download Model\" in the AI Sprite Animation window (1.92 GB, one time), or see Documentation~/sdpose-model.md.";

        /// <summary>The folder ComfyUI loads checkpoints from, or null when the ComfyUI folder is not configured / not local.</summary>
        public static string CheckpointFolder()
        {
            string dir = ComfyUIConnectionSettings.ComfyUIDirectory;
            if (!ComfyUIConnectionSettings.IsLocalHost || string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
            return Path.Combine(dir, "models", "checkpoints");
        }

        public static string InstalledPath(string fileName) => CheckpointFolder() is string f ? Path.Combine(f, fileName) : null;

        /// <summary>Is the model file where ComfyUI looks for it? Does not start ComfyUI. A file with the wrong size counts as missing (interrupted copy).</summary>
        public static SDPoseModelStatus Status(AIAnimationSettings settings)
        {
            string folder = CheckpointFolder();
            if (folder == null) return SDPoseModelStatus.Unknown;
            string path = Path.Combine(folder, settings.sdposeModelName);
            if (File.Exists(path) && (settings.sdposeModelName != DefaultFileName || new FileInfo(path).Length == SizeBytes)) return SDPoseModelStatus.Installed;
            // extra_model_paths.yaml may point ComfyUI at other folders: then only ComfyUI knows
            if (File.Exists(Path.Combine(ComfyUIConnectionSettings.ComfyUIDirectory, "extra_model_paths.yaml"))) return SDPoseModelStatus.Unknown;
            return SDPoseModelStatus.Missing;
        }
    }
}
