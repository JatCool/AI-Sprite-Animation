using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AISpriteAnimation
{
    public class ComfyUIException : Exception
    {
        public ComfyUIException(string message) : base(message) { }
        public ComfyUIException(string message, Exception inner) : base(message, inner) { }
    }

    public readonly struct ComfyImageRef
    {
        public readonly string NodeId, Filename, Subfolder, Type;

        public ComfyImageRef(string nodeId, string filename, string subfolder, string type)
        {
            NodeId = nodeId; Filename = filename; Subfolder = subfolder; Type = type;
        }
    }

    /// <summary>The outputs of a finished prompt: images and text of every output node.</summary>
    public sealed class ComfyResult
    {
        public readonly Dictionary<string, object> Outputs;
        public ComfyResult(Dictionary<string, object> outputs) { Outputs = outputs; }

        /// <summary>The first text output of a node (e.g. PreviewAny), or null.</summary>
        public string TextOf(string nodeId)
        {
            if (Outputs != null && Outputs.TryGetValue(nodeId, out object node) && MiniJson.Path(node, "text") is List<object> texts && texts.Count > 0) return texts[0] as string;
            return null;
        }

        public List<ComfyImageRef> ImagesOf(string nodeId)
        {
            var result = new List<ComfyImageRef>();
            if (Outputs == null || !Outputs.TryGetValue(nodeId, out object node) || !(MiniJson.Path(node, "images") is List<object> images)) return result;
            foreach (var img in images)
            {
                string filename = MiniJson.Path(img, "filename") as string;
                if (filename != null) result.Add(new ComfyImageRef(nodeId, filename, MiniJson.Path(img, "subfolder") as string ?? "", MiniJson.Path(img, "type") as string ?? "output"));
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Filename, b.Filename));
            return result;
        }
    }

    /// <summary>Thin wrapper over the ComfyUI HTTP API (/system_stats, /upload/image, /prompt, /history, /view, /queue, /interrupt).</summary>
    public sealed class ComfyUIClient : IDisposable
    {
        private readonly HttpClient http;
        private readonly string clientId = Guid.NewGuid().ToString("N");

        public Uri BaseUri { get; }

        public ComfyUIClient(string baseUrl)
        {
            BaseUri = new Uri(baseUrl.TrimEnd('/') + "/");
            http = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(120) };
        }

        public void Dispose() => http.Dispose();

        /// <summary>True if the server answers /system_stats.</summary>
        public async Task<bool> IsReadyAsync(CancellationToken ct, int timeoutMs = 2000)
        {
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeoutMs);
                    using (var response = await http.GetAsync("system_stats", cts.Token))
                        return response.IsSuccessStatusCode;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return false; }
        }

        /// <summary>GET a JSON endpoint (e.g. "object_info", "system_stats") and parse it with <see cref="MiniJson"/>.</summary>
        public async Task<object> GetJsonAsync(string path, CancellationToken ct)
        {
            using (var response = await http.GetAsync(path, ct))
            {
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) throw new ComfyUIException($"GET /{path} failed ({(int)response.StatusCode}): {body}");
                return MiniJson.Parse(body);
            }
        }

        /// <summary>Uploads a PNG into ComfyUI's input folder; returns the value for a LoadImage node ("subfolder/name").</summary>
        public async Task<string> UploadImageAsync(byte[] png, string fileName, string subfolder, CancellationToken ct)
        {
            using (var form = new MultipartFormDataContent())
            {
                var file = new ByteArrayContent(png);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                form.Add(file, "image", fileName);
                form.Add(new StringContent("input"), "type");
                form.Add(new StringContent(subfolder ?? ""), "subfolder");
                form.Add(new StringContent("true"), "overwrite");

                using (var response = await http.PostAsync("upload/image", form, ct))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new ComfyUIException($"Uploading the sprite failed ({(int)response.StatusCode}): {body}");

                    var json = MiniJson.Parse(body);
                    string name = MiniJson.Path(json, "name") as string ?? fileName;
                    string sub = MiniJson.Path(json, "subfolder") as string;
                    return string.IsNullOrEmpty(sub) ? name : sub + "/" + name;
                }
            }
        }

        /// <summary>Queues a workflow (API format JSON text). Returns the prompt id.</summary>
        public async Task<string> QueuePromptAsync(string workflowJson, CancellationToken ct)
        {
            string payload = "{\"client_id\":\"" + clientId + "\",\"prompt\":" + workflowJson + "}";
            using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
            using (var response = await http.PostAsync("prompt", content, ct))
            {
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new ComfyUIException("ComfyUI rejected the workflow:\n" + DescribePromptError(body));

                string id = MiniJson.Path(MiniJson.Parse(body), "prompt_id") as string;
                if (string.IsNullOrEmpty(id))
                    throw new ComfyUIException("ComfyUI did not return a prompt_id. Response: " + body);
                return id;
            }
        }

        /// <summary>
        /// Polls /history until the prompt finishes. <paramref name="status"/> receives progress text each poll.
        /// Returns the output images (filtered to <paramref name="outputNodeId"/> if given).
        /// </summary>
        public async Task<List<ComfyImageRef>> WaitForCompletionAsync(string promptId, TimeSpan timeout, string outputNodeId,
            Action<string> status, CancellationToken ct)
        {
            ComfyResult result = await WaitForResultAsync(promptId, timeout, status, ct);
            return CollectImages(result.Outputs, outputNodeId);
        }

        /// <summary>Polls /history until the prompt finishes and returns all of its outputs (images and text).</summary>
        public async Task<ComfyResult> WaitForResultAsync(string promptId, TimeSpan timeout, Action<string> status, CancellationToken ct)
        {
            DateTime start = DateTime.UtcNow;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                double elapsed = (DateTime.UtcNow - start).TotalSeconds;
                if (elapsed > timeout.TotalSeconds)
                    throw new ComfyUIException($"Timed out after {timeout.TotalSeconds:0}s waiting for ComfyUI to finish the animation.");

                string historyBody;
                try
                {
                    historyBody = await http.GetStringAsync("history/" + promptId);
                }
                catch (HttpRequestException e)
                {
                    throw new ComfyUIException("Lost connection to ComfyUI while generating (did it crash?): " + e.Message, e);
                }

                var entry = MiniJson.Path(MiniJson.Parse(historyBody), promptId);
                if (entry != null)
                {
                    string statusStr = MiniJson.Path(entry, "status", "status_str") as string;
                    if (statusStr == "error")
                        throw new ComfyUIException("ComfyUI reported an error while generating:\n" + DescribeExecutionError(entry));

                    var outputs = MiniJson.Path(entry, "outputs") as Dictionary<string, object>;
                    if (outputs != null && (statusStr == "success" || MiniJson.Path(entry, "status", "completed") is bool done && done))
                        return new ComfyResult(outputs);
                }

                status?.Invoke(await DescribeQueueAsync(promptId, elapsed));
                await Task.Delay(1000, ct);
            }
        }

        public async Task<byte[]> DownloadImageAsync(ComfyImageRef image, CancellationToken ct)
        {
            string url = "view?filename=" + Uri.EscapeDataString(image.Filename)
                       + "&subfolder=" + Uri.EscapeDataString(image.Subfolder ?? "")
                       + "&type=" + Uri.EscapeDataString(image.Type ?? "output");
            using (var response = await http.GetAsync(url, ct))
            {
                if (!response.IsSuccessStatusCode)
                    throw new ComfyUIException($"Downloading frame '{image.Filename}' failed ({(int)response.StatusCode}).");
                return await response.Content.ReadAsByteArrayAsync();
            }
        }

        /// <summary>Best-effort: stop the running job (used on cancel/failure so the GPU frees up).</summary>
        public async Task InterruptAsync()
        {
            try
            {
                using (var cts = new CancellationTokenSource(3000))
                using (var content = new StringContent("{}", Encoding.UTF8, "application/json"))
                using (await http.PostAsync("interrupt", content, cts.Token)) { }
            }
            catch { /* server may already be gone */ }
        }

        private async Task<string> DescribeQueueAsync(string promptId, double elapsedSeconds)
        {
            try
            {
                var queue = MiniJson.Parse(await http.GetStringAsync("queue"));
                if (MiniJson.Path(queue, "queue_running") is List<object> running)
                {
                    foreach (var item in running)
                        if (item is List<object> row && row.Count > 1 && (row[1] as string) == promptId)
                            return $"Generating animation... {elapsedSeconds:0}s";
                }
                return $"Waiting in ComfyUI queue... {elapsedSeconds:0}s";
            }
            catch { return $"Generating animation... {elapsedSeconds:0}s"; }
        }

        private static List<ComfyImageRef> CollectImages(Dictionary<string, object> outputs, string outputNodeId)
        {
            var result = new List<ComfyImageRef>();
            foreach (var kv in outputs)
            {
                if (!string.IsNullOrEmpty(outputNodeId) && kv.Key != outputNodeId) continue;
                if (!(MiniJson.Path(kv.Value, "images") is List<object> images)) continue;

                var nodeImages = new List<ComfyImageRef>();
                foreach (var img in images)
                {
                    string filename = MiniJson.Path(img, "filename") as string;
                    if (filename == null) continue;
                    nodeImages.Add(new ComfyImageRef(kv.Key, filename,
                        MiniJson.Path(img, "subfolder") as string ?? "", MiniJson.Path(img, "type") as string ?? "output"));
                }
                nodeImages.Sort((a, b) => string.CompareOrdinal(a.Filename, b.Filename));
                result.AddRange(nodeImages);
            }
            return result;
        }

        private static string DescribePromptError(string body)
        {
            try
            {
                var json = MiniJson.Parse(body);
                var sb = new StringBuilder();
                string type = MiniJson.Path(json, "error", "type") as string;
                string message = MiniJson.Path(json, "error", "message") as string;
                string details = MiniJson.Path(json, "error", "details") as string;
                if (message != null) sb.AppendLine($"{type}: {message} {details}");
                if (MiniJson.Path(json, "node_errors") is Dictionary<string, object> nodeErrors)
                {
                    foreach (var kv in nodeErrors)
                    {
                        string cls = MiniJson.Path(kv.Value, "class_type") as string;
                        sb.AppendLine($"  Node {kv.Key} ({cls}):");
                        if (MiniJson.Path(kv.Value, "errors") is List<object> errors)
                            foreach (var e in errors)
                                sb.AppendLine($"    - {MiniJson.Path(e, "message")} {MiniJson.Path(e, "details")} {MiniJson.Path(e, "extra_info", "input_name")}");
                    }
                }
                return sb.Length > 0 ? sb.ToString().TrimEnd() : body;
            }
            catch { return body; }
        }

        private static string DescribeExecutionError(object historyEntry)
        {
            if (MiniJson.Path(historyEntry, "status", "messages") is List<object> messages)
            {
                foreach (var m in messages)
                {
                    if (m is List<object> pair && pair.Count > 1 && (pair[0] as string) == "execution_error")
                    {
                        return $"Node {MiniJson.Path(pair[1], "node_id")} ({MiniJson.Path(pair[1], "node_type")}): "
                             + $"{MiniJson.Path(pair[1], "exception_type")}: {MiniJson.Path(pair[1], "exception_message")}";
                    }
                }
            }
            return "(no error details in /history)";
        }
    }
}
