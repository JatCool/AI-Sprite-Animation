using UnityEditor;

namespace AISpriteAnimation
{
    /// <summary>Makes sure a ComfyUI started by Unity never outlives the editor or a script reload.</summary>
    [InitializeOnLoad]
    internal static class ComfyUILifecycleHooks
    {
        static ComfyUILifecycleHooks()
        {
            // Asset-import worker processes also load editor assemblies; they must never touch ComfyUI.
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            ComfyUIProcessManager.CleanupOrphanFromPidFile();
            EditorApplication.quitting += ComfyUIProcessManager.ShutdownAll;
            AssemblyReloadEvents.beforeAssemblyReload += ComfyUIProcessManager.ShutdownAll;
        }
    }
}
