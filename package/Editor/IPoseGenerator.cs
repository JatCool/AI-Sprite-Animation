using System;
using System.Threading;
using System.Threading.Tasks;

namespace AISpriteAnimation
{
    /// <summary>Where the poses of AI Pose + Rig come from. SDPose is the only backend that produces AI-generated motion.</summary>
    public enum PoseBackend
    {
        /// <summary>AnimateDiff generates a video of a generic person doing the animation, SDPose reads the pose of every frame, the rig adopts the motion.</summary>
        SDPose = 0,
        /// <summary>1.2.0 behaviour: a procedural sketch skeleton is given to AnimateDiff and its frames only nudge the sketch. Not AI motion.</summary>
        SketchEvidenceLegacy = 1,
        /// <summary>Procedural poses (the Rig method's formulas with seeded variation). No AI, no ComfyUI. For testing and as a clearly labelled fallback.</summary>
        ProceduralFallback = 2,
    }

    public static class PoseBackendLabels
    {
        public static readonly PoseBackend[] Order = { PoseBackend.SDPose, PoseBackend.SketchEvidenceLegacy, PoseBackend.ProceduralFallback };
        public static readonly string[] Labels = { "SDPose (AI-generated motion)", "Sketch + AnimateDiff evidence (legacy 1.2.0, not AI motion)", "Procedural fallback (not AI)" };
        public static int IndexOf(PoseBackend b) => Math.Max(0, Array.IndexOf(Order, b));
    }

    /// <summary>Everything a pose generator needs.</summary>
    public sealed class PoseGeneratorContext
    {
        public SourceSprite Source;
        public SpriteRigAsset RigAsset;
        public AnimationPreset Preset;
        /// <summary>The project's settings (a private copy: generators may change it).</summary>
        public AIAnimationSettings Settings;
        public int Frames, Fps;
        public bool Loop;
        public long Seed;
        public bool FacingLeft;
        public string ExtraPrompt = "", ExtraNegativePrompt = "";
        /// <summary>Debug: folder for intermediate files (video frames, keypoints, ...).</summary>
        public string DumpFolder;
        /// <summary>Tests: replaces the ComfyUI backend of the legacy generator.</summary>
        public IAIAnimationBackend BackendOverride;
    }

    public sealed class PoseGeneratorResult
    {
        /// <summary>Raw rig poses, one per requested frame (validation and smoothing happen afterwards).</summary>
        public RigPose[] Poses;
        public string Backend = "";
        /// <summary>True only if the motion was produced by an AI model.</summary>
        public bool IsAI;
        /// <summary>Human-readable account of what was generated and how it was cut into an animation.</summary>
        public string Notes = "";
        public float FitError;
        public double ComfySeconds, AnalysisSeconds;
        public int Renders;
        /// <summary>Legacy backend: motion in the AI frames vs the sketch.</summary>
        public float EvidenceMotion, SketchMotion;
        /// <summary>SDPose: quality (0..1) of the chosen candidate and how many candidates were generated.</summary>
        public float Quality;
        public int Candidates;
    }

    /// <summary>A source of rig poses for AI Pose + Rig. The result is validated, saved and rendered by the same code whichever generator made it.</summary>
    public interface IPoseGenerator
    {
        string Name { get; }
        bool IsAI { get; }
        Task<PoseGeneratorResult> GenerateAsync(PoseGeneratorContext context, Action<string, float> progress, CancellationToken ct);
        /// <summary>Releases resources; stops ComfyUI if (and only if) this generator started it. Always called, also after failures.</summary>
        Task ReleaseAsync(Action<string, float> progress);
    }

    /// <summary>The selected backend cannot work (for example the SDPose model is not installed). Never answered by silently using another backend.</summary>
    public sealed class PoseBackendUnavailableException : Exception
    {
        public PoseBackendUnavailableException(string message) : base(message) { }
    }

    /// <summary>The backend ran but produced nothing usable (for example no candidate contained a walking motion).</summary>
    public sealed class PoseGenerationFailedException : Exception
    {
        public PoseGenerationFailedException(string message) : base(message) { }
    }
}
