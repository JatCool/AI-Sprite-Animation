using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AISpriteAnimation
{
    public enum DiagnosticStatus { Ok, Warning, Error, Info }

    public sealed class DiagnosticItem
    {
        public string Group, Label, Detail;
        public DiagnosticStatus Status;
        public DiagnosticItem(string group, DiagnosticStatus status, string label, string detail = null)
        {
            Group = group; Status = status; Label = label; Detail = detail;
        }
    }

    /// <summary>
    /// Checks the whole setup before a generation is attempted: ComfyUI install, API, GPU, custom nodes, models and the workflow.
    /// With a reachable API it validates against the live ComfyUI (/object_info); otherwise it falls back to checking files on disk.
    /// </summary>
    public static class ComfyUIDiagnostics
    {
        private struct Hint { public string Folder, Url; }

        // Where model files go and where to get them (shown when something is missing).
        private static readonly Dictionary<string, Hint> ModelHints = new Dictionary<string, Hint>
        {
            ["v1-5-pruned-emaonly.safetensors"] = new Hint { Folder = "models/checkpoints", Url = "https://huggingface.co/stable-diffusion-v1-5/stable-diffusion-v1-5/resolve/main/v1-5-pruned-emaonly.safetensors" },
            ["v3_sd15_mm.ckpt"] = new Hint { Folder = "models/animatediff_models", Url = "https://huggingface.co/guoyww/animatediff/resolve/main/v3_sd15_mm.ckpt" },
            ["control_v11f1e_sd15_tile.pth"] = new Hint { Folder = "models/controlnet", Url = "https://huggingface.co/lllyasviel/ControlNet-v1-1/resolve/main/control_v11f1e_sd15_tile.pth" },
            ["control_v11p_sd15_openpose.pth"] = new Hint { Folder = "models/controlnet", Url = "https://huggingface.co/lllyasviel/ControlNet-v1-1/resolve/main/control_v11p_sd15_openpose.pth" },
            ["ip-adapter-plus_sd15.safetensors"] = new Hint { Folder = "models/ipadapter", Url = "https://huggingface.co/h94/IP-Adapter/resolve/main/models/ip-adapter-plus_sd15.safetensors" },
            ["CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors"] = new Hint { Folder = "models/clip_vision", Url = "https://huggingface.co/h94/IP-Adapter/resolve/main/models/image_encoder/model.safetensors (rename to the file name shown)" },
        };

        private const string AnimateDiffRepo = "https://github.com/Kosinkadink/ComfyUI-AnimateDiff-Evolved";
        private const string IpAdapterRepo = "https://github.com/comfyorg/comfyui-ipadapter (fork of cubiq/ComfyUI_IPAdapter_plus)";

        private static string NodeRepoHint(string classType)
        {
            if (classType.StartsWith("ADE_")) return "install " + AnimateDiffRepo + " into ComfyUI/custom_nodes";
            if (classType.StartsWith("IPAdapter")) return "install " + IpAdapterRepo + " into ComfyUI/custom_nodes";
            return "install the custom node pack that provides it into ComfyUI/custom_nodes";
        }

        /// <param name="startIfNeeded">If the API is not reachable: start ComfyUI (owned by this run), validate, then stop it again.</param>
        public static async Task<List<DiagnosticItem>> RunAsync(AIAnimationSettings settings, bool startIfNeeded, CancellationToken ct)
        {
            var items = new List<DiagnosticItem>();
            const string cu = "ComfyUI";

            // --- ComfyUI install ---
            string comfyRoot = null;
            if (ComfyUIConnectionSettings.TryResolveLaunch(out string file, out string args, out string dir, out string launchError))
            {
                comfyRoot = dir;
                items.Add(new DiagnosticItem(cu, DiagnosticStatus.Ok, "Found", dir));
                items.Add(new DiagnosticItem(cu, DiagnosticStatus.Ok, "Launch command", file + " " + args));
            }
            else items.Add(new DiagnosticItem(cu, DiagnosticStatus.Warning, "Cannot auto-start", launchError + " (an already-running ComfyUI is still used)"));

            AddVersionInfo(items, settings, comfyRoot);
            if (settings.mode == AnimationMode.Rig)
                items.Add(new DiagnosticItem("Mode", DiagnosticStatus.Ok, "Rig mode: ComfyUI is not needed",
                    "Frames are made from the sprite's own pixels. The checks below only matter for AIRedraw mode."));
            else items.Add(new DiagnosticItem("Mode", DiagnosticStatus.Info, "AIRedraw mode: ComfyUI is required"));

            // --- API ---
            using (var client = new ComfyUIClient(ComfyUIConnectionSettings.Url))
            {
                var manager = new ComfyUIProcessManager(client);
                try
                {
                    bool ready = await client.IsReadyAsync(ct);
                    if (!ready && startIfNeeded)
                    {
                        try { await manager.EnsureRunningAsync(null, ct); ready = true; }
                        catch (ComfyUIException e) { items.Add(new DiagnosticItem(cu, DiagnosticStatus.Error, "Could not start", e.Message)); }
                    }

                    if (ready)
                    {
                        items.Add(new DiagnosticItem(cu, DiagnosticStatus.Ok, "API reachable", ComfyUIConnectionSettings.Url));
                        await CheckLiveAsync(client, settings, items, ct);
                    }
                    else
                    {
                        items.Add(new DiagnosticItem(cu, DiagnosticStatus.Info, "API not reachable right now",
                            "Unity starts ComfyUI automatically when you generate. Use 'Validate with ComfyUI' to check nodes and models now."));
                        CheckFiles(settings, comfyRoot, items);
                        CheckWorkflowOffline(settings, items);
                    }
                }
                finally
                {
                    if (manager.StartedByUnity) await manager.ShutdownAsync();
                }
            }
            return items;
        }

        // ---------- versions ----------

        private static void AddVersionInfo(List<DiagnosticItem> items, AIAnimationSettings settings, string comfyRoot)
        {
            const string g = "Versions";
            items.Add(new DiagnosticItem(g, DiagnosticStatus.Info, "AI Sprite Animation package: " + PackagePaths.Version));
            items.Add(new DiagnosticItem(g, DiagnosticStatus.Info, "Unity: " + UnityEngine.Application.unityVersion));
            string wf = settings.workflow != null ? $"custom ({settings.workflow.name})" : $"{ComfyWorkflowBuilder.BundledWorkflowName} v{ComfyWorkflowBuilder.BundledWorkflowVersion}";
            items.Add(new DiagnosticItem(g, DiagnosticStatus.Info, "Workflow: " + wf));
            if (comfyRoot == null) return;

            string comfy = ReadPythonVersion(Path.Combine(comfyRoot, "comfyui_version.py"));
            items.Add(new DiagnosticItem(g, DiagnosticStatus.Info, "ComfyUI: " + (comfy ?? "unknown")));
            items.Add(new DiagnosticItem(g, DiagnosticStatus.Info, "AnimateDiff-Evolved: " + NodePackVersion(Path.Combine(comfyRoot, "custom_nodes"), "animatediff")));
            items.Add(new DiagnosticItem(g, DiagnosticStatus.Info, "IPAdapter: " + NodePackVersion(Path.Combine(comfyRoot, "custom_nodes"), "ipadapter")));
        }

        // "pyproject version + git commit" of the custom node folder whose name contains `fragment`.
        private static string NodePackVersion(string customNodesDir, string fragment)
        {
            if (!Directory.Exists(customNodesDir)) return "not installed";
            foreach (string d in Directory.GetDirectories(customNodesDir))
            {
                if (!Path.GetFileName(d).ToLowerInvariant().Contains(fragment)) continue;
                string version = null;
                try
                {
                    string toml = Path.Combine(d, "pyproject.toml");
                    if (File.Exists(toml))
                        foreach (string line in File.ReadAllLines(toml))
                        {
                            string t = line.Trim();
                            if (t.StartsWith("version") && t.Contains("=")) { version = t.Substring(t.IndexOf('=') + 1).Trim().Trim('"'); break; }
                        }
                }
                catch { /* best effort */ }
                string commit = GitCommit(d);
                return $"{Path.GetFileName(d)} {(version != null ? "v" + version : "(no version)")}{(commit != null ? " @" + commit : "")}";
            }
            return "not installed";
        }

        private static string GitCommit(string repoDir)
        {
            try
            {
                string head = Path.Combine(repoDir, ".git", "HEAD");
                if (!File.Exists(head)) return null;
                string h = File.ReadAllText(head).Trim();
                if (h.StartsWith("ref:"))
                {
                    string refFile = Path.Combine(repoDir, ".git", h.Substring(4).Trim().Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(refFile)) h = File.ReadAllText(refFile).Trim();
                    else
                    {
                        string packed = Path.Combine(repoDir, ".git", "packed-refs");
                        string want = h.Substring(4).Trim();
                        h = null;
                        if (File.Exists(packed))
                            foreach (string line in File.ReadAllLines(packed))
                                if (line.EndsWith(" " + want)) { h = line.Split(' ')[0]; break; }
                    }
                }
                return h != null && h.Length >= 7 ? h.Substring(0, 7) : null;
            }
            catch { return null; }
        }

        private static string ReadPythonVersion(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                foreach (string line in File.ReadAllLines(file))
                    if (line.TrimStart().StartsWith("__version__") && line.Contains("="))
                        return line.Substring(line.IndexOf('=') + 1).Trim().Trim('"');
            }
            catch { /* best effort */ }
            return null;
        }

        // ---------- live validation through the ComfyUI API ----------

        /// <summary>Validates a RUNNING ComfyUI through its API: GPU, node classes, model files, and every node/option of the workflow.</summary>
        public static async Task CheckLiveAsync(ComfyUIClient client, AIAnimationSettings settings, List<DiagnosticItem> items, CancellationToken ct)
        {
            var stats = await client.GetJsonAsync("system_stats", ct);
            if (MiniJson.Path(stats, "system", "comfyui_version") is string liveVersion)
                items.Add(new DiagnosticItem("Versions", DiagnosticStatus.Info, "ComfyUI (API reports): " + liveVersion));
            if (MiniJson.Path(stats, "devices") is List<object> devices && devices.Count > 0)
            {
                string name = MiniJson.Path(devices[0], "name") as string;
                string type = MiniJson.Path(devices[0], "type") as string;
                double vram = MiniJson.Path(devices[0], "vram_total") is double d ? d : 0;
                bool gpu = type == "cuda" || type == "xpu" || type == "mps";
                items.Add(new DiagnosticItem("ComfyUI", gpu ? DiagnosticStatus.Ok : DiagnosticStatus.Warning,
                    gpu ? "GPU available" : "No GPU (CPU only: generation will be extremely slow)", $"{name}, {vram / (1024 * 1024 * 1024):0.0} GB VRAM"));
            }

            var objectInfo = await client.GetJsonAsync("object_info", ct) as Dictionary<string, object>;
            if (objectInfo == null) { items.Add(new DiagnosticItem("Workflow", DiagnosticStatus.Error, "Could not read /object_info")); return; }

            // Named groups, so the window reads like a checklist.
            CheckNode(items, objectInfo, "AnimateDiff", "ADE_UseEvolvedSampling");
            CheckNode(items, objectInfo, "AnimateDiff", "ADE_ApplyAnimateDiffModel");
            CheckOption(items, objectInfo, "AnimateDiff", "ADE_LoadAnimateDiffModel", "model_name", settings.motionModelName, "Motion model");
            CheckNode(items, objectInfo, "IPAdapter", "IPAdapterUnifiedLoader");
            CheckNode(items, objectInfo, "IPAdapter", "IPAdapter");
            CheckOption(items, objectInfo, "IPAdapter", "IPAdapterModelLoader", "ipadapter_file", settings.ipAdapterModelFile, "IPAdapter model");
            CheckOption(items, objectInfo, "IPAdapter", "CLIPVisionLoader", "clip_name", settings.clipVisionFile, "CLIP Vision model");
            CheckOption(items, objectInfo, "Models", "CheckpointLoaderSimple", "ckpt_name", settings.checkpointName, "Checkpoint");
            CheckOption(items, objectInfo, "ControlNet", "ControlNetLoader", "control_net_name", settings.tileControlNetName, "Tile ControlNet");
            CheckOption(items, objectInfo, "ControlNet", "ControlNetLoader", "control_net_name", settings.poseControlNetName, "OpenPose ControlNet");

            // Generic workflow validation: every node class exists and every model/enum value used is accepted.
            string text = settings.LoadWorkflowText();
            if (string.IsNullOrWhiteSpace(text)) { items.Add(new DiagnosticItem("Workflow", DiagnosticStatus.Error, "No workflow found")); return; }
            try
            {
                var request = new GenerationRequest { Preset = settings.presets.Count > 0 ? settings.presets[0] : new AnimationPreset(), Prompt = "x", NegativePrompt = "x", FrameCount = ComfyWorkflowBuilder.MaxFrames, Width = 512, Height = 512, Fps = 12, OutputPrefix = "diag" };
                var extras = new Dictionary<string, string>();
                for (int i = 0; i < ComfyWorkflowBuilder.MaxFrames; i++) extras[ComfyWorkflowBuilder.PoseSlot(i)] = "diag.png";
                extras[ComfyWorkflowBuilder.ReferenceImage] = "diag.png";
                string built = ComfyWorkflowBuilder.Build(text, ComfyUIAnimationBackend.BuildValues(settings, request, "diag.png", extras));
                var nodes = (Dictionary<string, object>)MiniJson.Parse(built);

                int problems = 0;
                foreach (var kv in nodes)
                {
                    string cls = MiniJson.Path(kv.Value, "class_type") as string;
                    if (cls == null) continue;
                    if (!objectInfo.TryGetValue(cls, out object info))
                    {
                        items.Add(new DiagnosticItem("Workflow", DiagnosticStatus.Error, $"Node {kv.Key}: '{cls}' is not installed", NodeRepoHint(cls)));
                        problems++;
                        continue;
                    }
                    if (cls == "LoadImage" || !(MiniJson.Path(kv.Value, "inputs") is Dictionary<string, object> inputs)) continue;
                    foreach (var input in inputs)
                    {
                        if (!(input.Value is string str)) continue;
                        var options = ComboOptions(info, input.Key);
                        if (options != null && !options.Contains(str))
                        {
                            items.Add(new DiagnosticItem("Workflow", DiagnosticStatus.Error, $"Node {kv.Key} ({cls}).{input.Key}: '{str}' is not available", ModelInstallHint(str, options)));
                            problems++;
                        }
                    }
                }
                if (problems == 0) items.Add(new DiagnosticItem("Workflow", DiagnosticStatus.Ok, "Valid", $"{nodes.Count} nodes, all installed, all models/options found"));
            }
            catch (Exception e)
            {
                items.Add(new DiagnosticItem("Workflow", DiagnosticStatus.Error, "Invalid", e.Message));
            }
        }

        private static void CheckNode(List<DiagnosticItem> items, Dictionary<string, object> info, string group, string classType)
        {
            if (info.ContainsKey(classType)) items.Add(new DiagnosticItem(group, DiagnosticStatus.Ok, $"Node installed: {classType}"));
            else items.Add(new DiagnosticItem(group, DiagnosticStatus.Error, $"Node missing: {classType}", NodeRepoHint(classType)));
        }

        private static void CheckOption(List<DiagnosticItem> items, Dictionary<string, object> info, string group, string classType, string input, string value, string label)
        {
            if (!info.TryGetValue(classType, out object node))
            {
                items.Add(new DiagnosticItem(group, DiagnosticStatus.Error, $"{label}: cannot check ('{classType}' node missing)", NodeRepoHint(classType)));
                return;
            }
            var options = ComboOptions(node, input);
            if (options != null && options.Contains(value)) items.Add(new DiagnosticItem(group, DiagnosticStatus.Ok, $"{label} found: {value}"));
            else items.Add(new DiagnosticItem(group, DiagnosticStatus.Error, $"{label} missing: {value}", ModelInstallHint(value, options)));
        }

        private static string ModelInstallHint(string file, ICollection<string> available)
        {
            string hint = ModelHints.TryGetValue(file, out Hint h) ? $"Put '{file}' in ComfyUI/{h.Folder}. Download: {h.Url}" : $"'{file}' is not in the list ComfyUI offers";
            if (available != null && available.Count > 0 && available.Count <= 8) hint += $" (available: {string.Join(", ", available)})";
            return hint;
        }

        // Combo options from /object_info: either [[a,b,c], {...}] (old) or ["COMBO", {options:[a,b,c]}] (new).
        private static HashSet<string> ComboOptions(object nodeInfo, string inputName)
        {
            foreach (string section in new[] { "required", "optional" })
            {
                if (!(MiniJson.Path(nodeInfo, "input", section, inputName) is List<object> spec) || spec.Count == 0) continue;
                List<object> list = spec[0] as List<object>;
                if (list == null && spec[0] as string == "COMBO" && spec.Count > 1) list = MiniJson.Path(spec[1], "options") as List<object>;
                if (list == null) return null;
                var set = new HashSet<string>();
                foreach (var o in list) if (o is string s) set.Add(s);
                return set;
            }
            return null;
        }

        // ---------- offline fallback: check files on disk ----------

        private static void CheckFiles(AIAnimationSettings settings, string comfyRoot, List<DiagnosticItem> items)
        {
            if (comfyRoot == null) return;
            string nodesDir = Path.Combine(comfyRoot, "custom_nodes");
            items.Add(DirContains(nodesDir, "animatediff") ? new DiagnosticItem("AnimateDiff", DiagnosticStatus.Ok, "Custom node folder present")
                : new DiagnosticItem("AnimateDiff", DiagnosticStatus.Error, "Custom node folder missing", NodeRepoHint("ADE_")));
            items.Add(DirContains(nodesDir, "ipadapter") ? new DiagnosticItem("IPAdapter", DiagnosticStatus.Ok, "Custom node folder present")
                : new DiagnosticItem("IPAdapter", DiagnosticStatus.Error, "Custom node folder missing", NodeRepoHint("IPAdapter")));

            FileCheck(items, comfyRoot, "AnimateDiff", "Motion model", settings.motionModelName, "models/animatediff_models");
            FileCheck(items, comfyRoot, "IPAdapter", "IPAdapter model", settings.ipAdapterModelFile, "models/ipadapter");
            FileCheck(items, comfyRoot, "IPAdapter", "CLIP Vision model", settings.clipVisionFile, "models/clip_vision");
            FileCheck(items, comfyRoot, "Models", "Checkpoint", settings.checkpointName, "models/checkpoints");
            FileCheck(items, comfyRoot, "ControlNet", "Tile ControlNet", settings.tileControlNetName, "models/controlnet");
            FileCheck(items, comfyRoot, "ControlNet", "OpenPose ControlNet", settings.poseControlNetName, "models/controlnet");
        }

        private static void FileCheck(List<DiagnosticItem> items, string root, string group, string label, string file, string folder)
        {
            if (File.Exists(Path.Combine(root, folder, file))) items.Add(new DiagnosticItem(group, DiagnosticStatus.Ok, $"{label} found: {file}"));
            else items.Add(new DiagnosticItem(group, DiagnosticStatus.Error, $"{label} missing: {file}", ModelInstallHint(file, null)));
        }

        private static bool DirContains(string dir, string fragment)
        {
            if (!Directory.Exists(dir)) return false;
            foreach (string d in Directory.GetDirectories(dir))
                if (Path.GetFileName(d).ToLowerInvariant().Contains(fragment)) return true;
            return false;
        }

        private static void CheckWorkflowOffline(AIAnimationSettings settings, List<DiagnosticItem> items)
        {
            string text = settings.LoadWorkflowText();
            if (string.IsNullOrWhiteSpace(text)) { items.Add(new DiagnosticItem("Workflow", DiagnosticStatus.Error, "No workflow found")); return; }
            try
            {
                var request = new GenerationRequest { Preset = settings.presets.Count > 0 ? settings.presets[0] : new AnimationPreset(), Prompt = "x", NegativePrompt = "x", FrameCount = ComfyWorkflowBuilder.MaxFrames, Width = 512, Height = 512, Fps = 12, OutputPrefix = "diag" };
                var extras = new Dictionary<string, string>();
                for (int i = 0; i < ComfyWorkflowBuilder.MaxFrames; i++) extras[ComfyWorkflowBuilder.PoseSlot(i)] = "diag.png";
                extras[ComfyWorkflowBuilder.ReferenceImage] = "diag.png";
                ComfyWorkflowBuilder.Build(text, ComfyUIAnimationBackend.BuildValues(settings, request, "diag.png", extras));
                items.Add(new DiagnosticItem("Workflow", DiagnosticStatus.Ok, "Valid JSON, all placeholders resolved", "Node/model availability is checked once ComfyUI is running."));
            }
            catch (Exception e)
            {
                items.Add(new DiagnosticItem("Workflow", DiagnosticStatus.Error, "Invalid", e.Message));
            }
        }
    }
}
