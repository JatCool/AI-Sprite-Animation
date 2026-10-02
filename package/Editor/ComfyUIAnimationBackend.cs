using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AISpriteAnimation
{
    /// <summary>Runs the AnimateDiff workflow on a local ComfyUI, starting/stopping it as needed.</summary>
    public sealed class ComfyUIAnimationBackend : IAIAnimationBackend
    {
        private const string UploadSubfolder = "unity_ai_animation";

        private readonly AIAnimationSettings settings;
        private readonly ComfyUIClient client;
        private readonly ComfyUIProcessManager processManager;
        private readonly TimeSpan generationTimeout;

        public string Name => "ComfyUI (AnimateDiff)";

        public ComfyUIAnimationBackend(AIAnimationSettings settings)
        {
            this.settings = settings;
            generationTimeout = TimeSpan.FromSeconds(Math.Max(30, settings.generationTimeoutSeconds));
            client = new ComfyUIClient(ComfyUIConnectionSettings.Url);
            processManager = new ComfyUIProcessManager(client);
        }

        public async Task<IReadOnlyList<byte[]>> GenerateFramesAsync(GenerationRequest request, Action<string, float> progress, CancellationToken ct)
        {
            string workflowTemplate = settings.LoadWorkflowText();

            // Validate the workflow before touching ComfyUI at all (a broken workflow must not start the process).
            var dummyNames = new Dictionary<string, string>();
            foreach (var key in request.ExtraImages.Keys) dummyNames[key] = "validation.png";
            ComfyWorkflowBuilder.Build(workflowTemplate, BuildValues(settings, request, "validation.png", dummyNames));

            await processManager.EnsureRunningAsync(msg => progress?.Invoke(msg, 0.1f), ct);

            try
            {
                progress?.Invoke("Uploading sprite...", 0.2f);
                string uploaded = await client.UploadImageAsync(request.InputPng, request.OutputPrefix + "_input.png", UploadSubfolder, ct);
                var extraNames = new Dictionary<string, string>();
                foreach (var kv in request.ExtraImages)
                    extraNames[kv.Key] = await client.UploadImageAsync(kv.Value, request.OutputPrefix + kv.Key.Trim('_').ToLowerInvariant() + ".png", UploadSubfolder, ct);

                progress?.Invoke("Submitting ComfyUI workflow...", 0.25f);
                string workflow = ComfyWorkflowBuilder.Build(workflowTemplate, BuildValues(settings, request, uploaded, extraNames));
                string promptId = await client.QueuePromptAsync(workflow, ct);

                var images = await client.WaitForCompletionAsync(promptId, generationTimeout, settings.nodes.outputNodeId,
                    msg => progress?.Invoke(msg, 0.5f), ct);
                if (images.Count == 0)
                    throw new ComfyUIException("ComfyUI finished but returned no images. Check that the workflow ends in a SaveImage/PreviewImage node"
                        + (string.IsNullOrEmpty(settings.nodes.outputNodeId) ? "." : $" with ID '{settings.nodes.outputNodeId}'."));

                var frames = new List<byte[]>(images.Count);
                for (int i = 0; i < images.Count; i++)
                {
                    progress?.Invoke($"Downloading frames... {i + 1}/{images.Count}", 0.8f + 0.1f * (i + 1) / images.Count);
                    frames.Add(await client.DownloadImageAsync(images[i], ct));
                }
                return frames;
            }
            catch (OperationCanceledException)
            {
                await client.InterruptAsync();
                throw;
            }
            catch (ComfyUIException)
            {
                await client.InterruptAsync();
                throw;
            }
        }

        public async Task ReleaseAsync(Action<string, float> progress)
        {
            try
            {
                if (processManager.StartedByUnity)
                {
                    progress?.Invoke("Stopping ComfyUI...", 0.97f);
                    await processManager.ShutdownAsync();
                }
            }
            finally
            {
                client.Dispose();
            }
        }

        public string LogTail() => processManager.LogTail();

        /// <summary>Maps every workflow placeholder to its value. Also used by the diagnostics to validate the workflow.</summary>
        public static Dictionary<string, ComfyWorkflowBuilder.Value> BuildValues(AIAnimationSettings s, GenerationRequest r,
            string uploadedImage, IDictionary<string, string> extraImageNames)
        {
            var p = r.Preset;
            var v = new Dictionary<string, ComfyWorkflowBuilder.Value>
            {
                [ComfyWorkflowBuilder.InputImage] = ComfyWorkflowBuilder.Value.Str(uploadedImage),
                [ComfyWorkflowBuilder.NoiseType] = ComfyWorkflowBuilder.Value.Str(s.noiseType),
                [ComfyWorkflowBuilder.Prompt] = ComfyWorkflowBuilder.Value.Str(r.Prompt),
                [ComfyWorkflowBuilder.NegativePrompt] = ComfyWorkflowBuilder.Value.Str(r.NegativePrompt),
                [ComfyWorkflowBuilder.FrameCount] = ComfyWorkflowBuilder.Value.Num(r.FrameCount),
                [ComfyWorkflowBuilder.Width] = ComfyWorkflowBuilder.Value.Num(r.Width),
                [ComfyWorkflowBuilder.Height] = ComfyWorkflowBuilder.Value.Num(r.Height),
                [ComfyWorkflowBuilder.Fps] = ComfyWorkflowBuilder.Value.Num(r.Fps),
                [ComfyWorkflowBuilder.Seed] = ComfyWorkflowBuilder.Value.Num(r.Seed),
                [ComfyWorkflowBuilder.Steps] = ComfyWorkflowBuilder.Value.Num(p.steps),
                [ComfyWorkflowBuilder.Cfg] = ComfyWorkflowBuilder.Value.Num(p.cfg),
                [ComfyWorkflowBuilder.Denoise] = ComfyWorkflowBuilder.Value.Num(p.denoise),
                [ComfyWorkflowBuilder.MotionScale] = ComfyWorkflowBuilder.Value.Num(p.motionScale),
                [ComfyWorkflowBuilder.IpAdapterWeight] = ComfyWorkflowBuilder.Value.Num(p.ipAdapterWeight),
                [ComfyWorkflowBuilder.IpAdapterPreset] = ComfyWorkflowBuilder.Value.Str(s.ipAdapterPreset),
                [ComfyWorkflowBuilder.TileStrength] = ComfyWorkflowBuilder.Value.Num(p.controlNetStrength),
                [ComfyWorkflowBuilder.TileEnd] = ComfyWorkflowBuilder.Value.Num(p.tileEndPercent),
                [ComfyWorkflowBuilder.TileControlNetModel] = ComfyWorkflowBuilder.Value.Str(s.tileControlNetName),
                [ComfyWorkflowBuilder.PoseStrength] = ComfyWorkflowBuilder.Value.Num(p.poseStrength),
                [ComfyWorkflowBuilder.PoseControlNetModel] = ComfyWorkflowBuilder.Value.Str(s.poseControlNetName),
                [ComfyWorkflowBuilder.Checkpoint] = ComfyWorkflowBuilder.Value.Str(s.checkpointName),
                [ComfyWorkflowBuilder.MotionModel] = ComfyWorkflowBuilder.Value.Str(s.motionModelName),
                [ComfyWorkflowBuilder.OutputPrefix] = ComfyWorkflowBuilder.Value.Str(r.OutputPrefix),
            };
            if (extraImageNames != null)
                foreach (var kv in extraImageNames) v[kv.Key] = ComfyWorkflowBuilder.Value.Str(kv.Value);
            return v;
        }
    }
}
