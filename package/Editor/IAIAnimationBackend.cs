using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AISpriteAnimation
{
    /// <summary>
    /// Produces animation frames (PNG bytes, in playback order) from a prepared sprite.
    /// ComfyUI/AnimateDiff is the only implementation today; other models or services can be added behind this.
    /// </summary>
    public interface IAIAnimationBackend
    {
        string Name { get; }

        Task<IReadOnlyList<byte[]>> GenerateFramesAsync(GenerationRequest request, Action<string, float> progress, CancellationToken ct);

        /// <summary>Releases resources (e.g. stops a server this backend started). Called after the frames were imported, also on failure.</summary>
        Task ReleaseAsync(Action<string, float> progress);
    }
}
