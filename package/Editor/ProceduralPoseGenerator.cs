using System;
using System.Threading;
using System.Threading.Tasks;

namespace AISpriteAnimation
{
    /// <summary>
    /// Procedural poses (the Rig method's Idle/Walk/Run/Attack with seeded amplitude variation). Instant, no ComfyUI, no AI.
    /// Useful for tests and as a fallback that is always labelled "not AI": it is never presented as AI-generated motion.
    /// </summary>
    public sealed class ProceduralPoseGenerator : IPoseGenerator
    {
        private readonly AIAnimationSettings settings;

        public string Name => "Procedural fallback";
        public bool IsAI => false;

        public ProceduralPoseGenerator(AIAnimationSettings settings) { this.settings = settings; }

        public Task<PoseGeneratorResult> GenerateAsync(PoseGeneratorContext ctx, Action<string, float> progress, CancellationToken ct)
        {
            progress?.Invoke("Building procedural poses (not AI)...", 0.5f);
            var poses = PoseSketch.Build(ctx.Preset.poseKind, ctx.Frames, ctx.Frames, ctx.Preset.rigIntensity, ctx.Seed, settings.aiPoseSketchVariation);
            return Task.FromResult(new PoseGeneratorResult
            {
                Poses = poses,
                Backend = "Procedural fallback (not AI)",
                IsAI = false,
                Notes = "Procedural poses with seeded amplitude variation. No AI model was used.",
            });
        }

        public Task ReleaseAsync(Action<string, float> progress) => Task.CompletedTask;
    }
}
