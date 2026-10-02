using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AISpriteAnimation
{
    /// <summary>
    /// Detects/starts/stops a local ComfyUI process. Only a process that THIS manager started is ever stopped
    /// (<see cref="StartedByUnity"/>); an already-running ComfyUI is used as-is and left running.
    /// </summary>
    public sealed class ComfyUIProcessManager
    {
        private static readonly string PidFilePath =
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "AIAnimation", "comfyui.pid"));
        private static readonly List<ComfyUIProcessManager> Live = new List<ComfyUIProcessManager>();

        private readonly ComfyUIClient client;
        private readonly object sync = new object();
        private readonly Queue<string> logTail = new Queue<string>();
        private Process process;
        private int shutdownGraceSeconds = 10; // snapshot: EditorPrefs is main-thread only

        /// <summary>True while Unity owns a ComfyUI process it launched itself.</summary>
        public bool StartedByUnity { get; private set; }

        public ComfyUIProcessManager(ComfyUIClient client) { this.client = client; }

        /// <summary>
        /// Makes sure the API answers. Uses an existing instance if there is one; otherwise starts the configured
        /// process and waits until it is ready. Throws <see cref="ComfyUIException"/> (after cleaning up) on failure.
        /// </summary>
        public async Task EnsureRunningAsync(Action<string> status, CancellationToken ct)
        {
            string unreachable = $"ComfyUI could not be started or is not reachable at {client.BaseUri.GetLeftPart(UriPartial.Authority)}";

            if (await client.IsReadyAsync(ct))
            {
                status?.Invoke("Using the ComfyUI instance that is already running...");
                return; // StartedByUnity stays false: we must not stop it.
            }

            if (!ComfyUIConnectionSettings.TryResolveLaunch(out string file, out string args, out string dir, out string error))
                throw new ComfyUIException($"{unreachable}\n{error}");

            status?.Invoke("Starting ComfyUI...");
            shutdownGraceSeconds = ComfyUIConnectionSettings.ShutdownGraceSeconds;
            StartProcess(file, args, dir, unreachable);

            try
            {
                status?.Invoke("Waiting for ComfyUI...");
                var deadline = DateTime.UtcNow.AddSeconds(ComfyUIConnectionSettings.StartupTimeoutSeconds);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        process.WaitForExit(); // flush the async stdout/stderr readers so the log tail is complete
                        throw new ComfyUIException($"{unreachable}\nThe ComfyUI process exited with code {process.ExitCode}.\n{LogTail()}");
                    }
                    if (await client.IsReadyAsync(ct)) return;
                    if (DateTime.UtcNow > deadline)
                        throw new ComfyUIException($"{unreachable}\nTimed out after {ComfyUIConnectionSettings.StartupTimeoutSeconds}s waiting for startup.\n{LogTail()}");
                    await Task.Delay(1000, ct);
                }
            }
            catch
            {
                await ShutdownAsync();
                throw;
            }
        }

        public Task ShutdownAsync() => Task.Run(ShutdownBlocking);

        /// <summary>Stops the ComfyUI process if (and only if) this manager started it. Safe to call repeatedly.</summary>
        public void ShutdownBlocking()
        {
            Process p;
            lock (sync)
            {
                p = process;
                process = null;
                if (!StartedByUnity || p == null) return;
                StartedByUnity = false;
            }

            lock (Live) Live.Remove(this);
            try
            {
                if (!p.HasExited)
                {
                    TrySendCtrlC(p);                                   // graceful: lets ComfyUI release the GPU itself
                    int graceMs = shutdownGraceSeconds * 1000;
                    if (!p.WaitForExit(graceMs))
                    {
                        Debug.LogWarning("[AI Animation] ComfyUI did not exit gracefully; force-killing its process tree.");
                        KillTree(p);
                        p.WaitForExit(5000);
                    }
                }
                if (!p.HasExited)
                    Debug.LogError($"[AI Animation] Could not stop ComfyUI (pid {p.Id}). Close it manually.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AI Animation] Error while stopping ComfyUI: " + e.Message);
            }
            finally
            {
                try { p.Dispose(); } catch { /* ignore */ }
                DeletePidFile();
            }
        }

        public string LogTail()
        {
            lock (logTail) return logTail.Count == 0 ? "(no ComfyUI output captured)" : "ComfyUI output (last lines):\n" + string.Join("\n", logTail);
        }

        // ---- static lifecycle helpers (called from ComfyUILifecycleHooks) ----

        public static void ShutdownAll()
        {
            ComfyUIProcessManager[] copy;
            lock (Live) copy = Live.ToArray();
            foreach (var m in copy) m.ShutdownBlocking();
        }

        /// <summary>Kills a ComfyUI that a previous Unity session/domain started and lost track of (crash, forced reload).</summary>
        public static void CleanupOrphanFromPidFile()
        {
            try
            {
                if (!File.Exists(PidFilePath)) return;
                string[] parts = File.ReadAllText(PidFilePath).Split('|');
                if (parts.Length != 4 || !int.TryParse(parts[0], out int pid) || !long.TryParse(parts[1], out long ticks)
                    || !int.TryParse(parts[2], out int ownerPid) || !long.TryParse(parts[3], out long ownerTicks))
                {
                    File.Delete(PidFilePath);
                    return;
                }

                // Still owned by a live editor (this one, or another Unity instance)? Then it is not an orphan.
                if (IsSameProcessAlive(ownerPid, ownerTicks)) return;
                File.Delete(PidFilePath);

                Process p;
                try { p = Process.GetProcessById(pid); } catch (ArgumentException) { return; }
                if (p.HasExited || Math.Abs(p.StartTime.ToUniversalTime().Ticks - ticks) > TimeSpan.TicksPerSecond * 2) return; // pid was reused

                Debug.LogWarning($"[AI Animation] Stopping orphaned ComfyUI process (pid {pid}) left over from an earlier Unity session.");
                KillTree(p);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AI Animation] Orphan cleanup failed: " + e.Message);
            }
        }

        // ---- internals ----

        private static bool IsSameProcessAlive(int pid, long startTicksUtc)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                return !p.HasExited && Math.Abs(p.StartTime.ToUniversalTime().Ticks - startTicksUtc) <= TimeSpan.TicksPerSecond * 2;
            }
            catch (ArgumentException) { return false; }   // no such process
            catch (InvalidOperationException) { return false; }
        }

        private void StartProcess(string file, string args, string dir, string unreachable)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler onLine = (s, e) => AppendLog(e.Data);
            p.OutputDataReceived += onLine;
            p.ErrorDataReceived += onLine;

            try
            {
                p.Start();
            }
            catch (Win32Exception e)
            {
                p.Dispose();
                throw new ComfyUIException($"{unreachable}\nCould not launch '{file} {args}': {e.Message}", e);
            }

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            lock (sync)
            {
                process = p;
                StartedByUnity = true;
            }
            lock (Live) Live.Add(this);
            WritePidFile(p);
            Debug.Log($"[AI Animation] Started ComfyUI (pid {p.Id}): {file} {args}");
        }

        private void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (logTail)
            {
                logTail.Enqueue(line);
                while (logTail.Count > 40) logTail.Dequeue();
            }
        }

        private static void WritePidFile(Process p)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PidFilePath));
                var self = Process.GetCurrentProcess();
                File.WriteAllText(PidFilePath, $"{p.Id}|{p.StartTime.ToUniversalTime().Ticks}|{self.Id}|{self.StartTime.ToUniversalTime().Ticks}");
            }
            catch { /* best effort */ }
        }

        private static void DeletePidFile()
        {
            try { if (File.Exists(PidFilePath)) File.Delete(PidFilePath); } catch { /* best effort */ }
        }

        private static void KillTree(Process p)
        {
#if UNITY_EDITOR_WIN
            try
            {
                var psi = new ProcessStartInfo("taskkill", $"/PID {p.Id} /T /F") { UseShellExecute = false, CreateNoWindow = true };
                using (var k = Process.Start(psi)) k?.WaitForExit(10000);
                return;
            }
            catch { /* fall through to Kill() */ }
#endif
            try { p.Kill(); } catch { /* already gone */ }
        }

#if UNITY_EDITOR_WIN
        private const uint CtrlCEvent = 0;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

        // Sends Ctrl+C to the child's (hidden) console so Python shuts down cleanly.
        private static void TrySendCtrlC(Process p)
        {
            try
            {
                FreeConsole();
                if (AttachConsole((uint)p.Id))
                {
                    SetConsoleCtrlHandler(IntPtr.Zero, true); // ignore Ctrl+C in Unity itself while it is sent
                    GenerateConsoleCtrlEvent(CtrlCEvent, 0);
                    Thread.Sleep(200);
                    FreeConsole();
                    SetConsoleCtrlHandler(IntPtr.Zero, false);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AI Animation] Graceful shutdown signal failed: " + e.Message);
            }
        }
#else
        private static void TrySendCtrlC(Process p) { }
#endif
    }
}
