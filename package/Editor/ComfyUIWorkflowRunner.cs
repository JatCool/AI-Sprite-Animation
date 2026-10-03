using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AISpriteAnimation
{
    /// <summary>
    /// Runs complete ComfyUI workflows for the pose backends: makes sure ComfyUI is running (starting it if needed), submits a workflow, waits for the
    /// outputs, and on <see cref="ReleaseAsync"/> stops ComfyUI if (and only if) Unity started it. Same process management as the AI Redraw backend.
    /// </summary>
    public sealed class ComfyUIWorkflowRunner
    {
        private readonly ComfyUIClient client;
        private readonly ComfyUIProcessManager processManager;
        private readonly TimeSpan timeout;
        private bool started;

        public ComfyUIWorkflowRunner(AIAnimationSettings settings)
        {
            timeout = TimeSpan.FromSeconds(Math.Max(30, settings.generationTimeoutSeconds));
            client = new ComfyUIClient(ComfyUIConnectionSettings.Url);
            processManager = new ComfyUIProcessManager(client);
        }

        public bool StartedByUnity => processManager.StartedByUnity;
        public string LogTail() => processManager.LogTail();

        public async Task EnsureRunningAsync(Action<string, float> progress, CancellationToken ct)
        {
            if (started) return;
            await processManager.EnsureRunningAsync(msg => progress?.Invoke(msg, 0.1f), ct);
            started = true;
        }

        /// <summary>Names offered by a combo input of a node class (e.g. the checkpoints ComfyUI sees), or null if the node does not exist.</summary>
        public async Task<HashSet<string>> GetComboOptionsAsync(string nodeClass, string input, CancellationToken ct)
        {
            var info = await client.GetJsonAsync("object_info/" + nodeClass, ct);
            object node = MiniJson.Path(info, nodeClass);
            if (node == null) return null;
            foreach (string section in new[] { "required", "optional" })
            {
                if (!(MiniJson.Path(node, "input", section, input) is List<object> spec) || spec.Count == 0) continue;
                List<object> list = spec[0] as List<object>;
                if (list == null && spec[0] as string == "COMBO" && spec.Count > 1) list = MiniJson.Path(spec[1], "options") as List<object>;
                if (list == null) return null;
                var set = new HashSet<string>();
                foreach (var o in list) if (o is string s) set.Add(s);
                return set;
            }
            return null;
        }

        public async Task<bool> NodeExistsAsync(string nodeClass, CancellationToken ct)
        {
            var info = await client.GetJsonAsync("object_info/" + nodeClass, ct);
            return MiniJson.Path(info, nodeClass) != null;
        }

        /// <summary>Submits the workflow and waits for it. Interrupts the job if it fails or is cancelled so the GPU frees up.</summary>
        public async Task<ComfyResult> RunAsync(string workflowJson, Action<string, float> progress, float progressValue, CancellationToken ct)
        {
            try
            {
                string promptId = await client.QueuePromptAsync(workflowJson, ct);
                return await client.WaitForResultAsync(promptId, timeout, msg => progress?.Invoke(msg, progressValue), ct);
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

        public Task<byte[]> DownloadAsync(ComfyImageRef image, CancellationToken ct) => client.DownloadImageAsync(image, ct);

        /// <summary>Stops ComfyUI if Unity started it (never an externally started one) and releases the connection.</summary>
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
    }
}
