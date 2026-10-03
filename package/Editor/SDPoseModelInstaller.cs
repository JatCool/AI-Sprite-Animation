using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace AISpriteAnimation
{
    /// <summary>
    /// Downloads the SDPose checkpoint (1.92 GB) into ComfyUI's models/checkpoints folder when - and only when - the user asks for it.
    /// Resumes an interrupted download, verifies size and SHA-256, and only then gives the file its final name. No credentials are used or stored.
    /// </summary>
    public static class SDPoseModelInstaller
    {
        public const string ConfirmationText =
            "Download the SDPose model?\n\n" +
            "File: sdpose_wholebody_fp16.safetensors (1.92 GB)\n" +
            "From: " + SDPoseModel.SourcePage + "\n" +
            "To: <ComfyUI>/models/checkpoints\n" +
            "License: " + SDPoseModel.License + "\n\n" +
            "This is a one-time download; it is verified with SHA-256 afterwards.";

        /// <returns>The installed file's path. Throws with an explanation on failure; cancellation keeps the partial file so the next attempt resumes.</returns>
        public static async Task<string> InstallAsync(Action<string, float> progress, CancellationToken ct)
        {
            string folder = SDPoseModel.CheckpointFolder();
            if (folder == null)
                throw new InvalidOperationException("The ComfyUI folder is not set (or ComfyUI is not local). Set it in the ComfyUI section of the window, or copy the file manually (see Documentation~/sdpose-model.md).");
            Directory.CreateDirectory(folder);
            string finalPath = Path.Combine(folder, SDPoseModel.DefaultFileName);
            string partPath = finalPath + ".part";

            if (File.Exists(finalPath) && new FileInfo(finalPath).Length == SDPoseModel.SizeBytes) return finalPath;

            using (var http = new HttpClient { Timeout = TimeSpan.FromHours(2) })
            {
                long have = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                if (have > SDPoseModel.SizeBytes) { File.Delete(partPath); have = 0; }

                if (have < SDPoseModel.SizeBytes)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, SDPoseModel.DownloadUrl);
                    if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) { have = 0; File.Delete(partPath); }
                        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                            throw new InvalidOperationException($"Download failed: HTTP {(int)response.StatusCode} from {SDPoseModel.DownloadUrl}");

                        using (var source = await response.Content.ReadAsStreamAsync())
                        using (var target = new FileStream(partPath, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            var buffer = new byte[1 << 20];
                            long done = have;
                            DateTime last = DateTime.MinValue;
                            int read;
                            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                            {
                                await target.WriteAsync(buffer, 0, read, ct);
                                done += read;
                                if ((DateTime.UtcNow - last).TotalMilliseconds > 250)
                                {
                                    last = DateTime.UtcNow;
                                    progress?.Invoke($"Downloading SDPose model... {done / 1048576} / {SDPoseModel.SizeBytes / 1048576} MB", 0.9f * done / SDPoseModel.SizeBytes);
                                }
                            }
                        }
                    }
                }
            }

            progress?.Invoke("Verifying the download (SHA-256)...", 0.92f);
            long size = new FileInfo(partPath).Length;
            if (size != SDPoseModel.SizeBytes)
                throw new InvalidOperationException($"The download is incomplete ({size} of {SDPoseModel.SizeBytes} bytes). Run the installer again to resume.");
            string hash = await Task.Run(() =>
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(partPath))
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }, ct);
            if (hash != SDPoseModel.Sha256)
            {
                File.Delete(partPath);
                throw new InvalidOperationException($"The downloaded file is corrupt (SHA-256 {hash}, expected {SDPoseModel.Sha256}). It was deleted; try again.");
            }
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partPath, finalPath);
            progress?.Invoke("SDPose model installed.", 1f);
            return finalPath;
        }
    }
}
