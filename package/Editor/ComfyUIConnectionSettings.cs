using System;
using System.IO;
using UnityEditor;

namespace AISpriteAnimation
{
    /// <summary>
    /// Machine-specific ComfyUI settings, stored in EditorPrefs (not committed to version control).
    /// </summary>
    public static class ComfyUIConnectionSettings
    {
        private const string Prefix = "AISpriteAnimation.";
        public const string DefaultUrl = "http://127.0.0.1:8188";
        public const string DefaultArgs = "main.py --listen {host} --port {port}";

        public static string Url
        {
            get => EditorPrefs.GetString(Prefix + "Url", DefaultUrl);
            set => EditorPrefs.SetString(Prefix + "Url", string.IsNullOrWhiteSpace(value) ? DefaultUrl : value.Trim().TrimEnd('/'));
        }

        public static string ComfyUIDirectory
        {
            get => EditorPrefs.GetString(Prefix + "Directory", "");
            set => EditorPrefs.SetString(Prefix + "Directory", value ?? "");
        }

        /// <summary>Optional python (or launcher .exe/.bat). Empty = auto-detect inside the ComfyUI directory, then "python".</summary>
        public static string PythonPath
        {
            get => EditorPrefs.GetString(Prefix + "Python", "");
            set => EditorPrefs.SetString(Prefix + "Python", value ?? "");
        }

        /// <summary>Arguments passed to the executable. {host} and {port} are substituted.</summary>
        public static string LaunchArgs
        {
            get => EditorPrefs.GetString(Prefix + "Args", DefaultArgs);
            set => EditorPrefs.SetString(Prefix + "Args", string.IsNullOrWhiteSpace(value) ? DefaultArgs : value.Trim());
        }

        public static int StartupTimeoutSeconds
        {
            get => EditorPrefs.GetInt(Prefix + "StartupTimeout", 180);
            set => EditorPrefs.SetInt(Prefix + "StartupTimeout", Math.Max(10, value));
        }

        public static int ShutdownGraceSeconds
        {
            get => EditorPrefs.GetInt(Prefix + "ShutdownGrace", 10);
            set => EditorPrefs.SetInt(Prefix + "ShutdownGrace", Math.Max(1, value));
        }

        public static Uri BaseUri => new Uri(Url.TrimEnd('/') + "/");

        public static string Host => BaseUri.Host;

        public static int Port
        {
            get => BaseUri.Port;
            set
            {
                var b = new UriBuilder(BaseUri) { Port = Math.Max(1, Math.Min(65535, value)) };
                Url = b.Uri.GetLeftPart(UriPartial.Authority);
            }
        }

        public static bool IsLocalHost
        {
            get
            {
                string h = Host;
                return h == "127.0.0.1" || h == "localhost" || h == "::1" || h == "[::1]" || h == "0.0.0.0";
            }
        }

        /// <summary>Resolves the process to start. Returns false with an explanation if it cannot be started.</summary>
        public static bool TryResolveLaunch(out string fileName, out string arguments, out string workingDirectory, out string error)
        {
            fileName = arguments = workingDirectory = null;
            error = null;

            if (!IsLocalHost)
            {
                error = $"{Url} is not a local address, so Unity will not try to start ComfyUI.";
                return false;
            }

            string dir = ComfyUIDirectory;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                error = "ComfyUI directory is not set or does not exist. Set it in the AI Sprite Animation window (ComfyUI section).";
                return false;
            }

            string exe = PythonPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                string[] candidates =
                {
                    Path.Combine(dir, "python_embeded", "python.exe"),       // ComfyUI portable, dir = ComfyUI_windows_portable
                    Path.Combine(dir, "..", "python_embeded", "python.exe"), // portable, dir = its ComfyUI subfolder
                    Path.Combine(dir, ".venv", "Scripts", "python.exe"),
                    Path.Combine(dir, "venv", "Scripts", "python.exe"),
                };
                foreach (string c in candidates)
                {
                    if (File.Exists(c)) { exe = Path.GetFullPath(c); break; }
                }
                if (string.IsNullOrEmpty(exe)) exe = "python";
            }
            else if (Path.IsPathRooted(exe) && !File.Exists(exe))
            {
                error = $"Python/launcher not found: {exe}";
                return false;
            }

            string args = LaunchArgs.Replace("{host}", Host == "0.0.0.0" ? "127.0.0.1" : Host).Replace("{port}", Port.ToString());
            if (args.StartsWith("main.py", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(dir, "main.py")))
            {
                error = $"main.py not found in {dir}. Point the ComfyUI directory at the folder that contains main.py.";
                return false;
            }

            fileName = exe;
            arguments = args;
            workingDirectory = dir;
            return true;
        }
    }
}
