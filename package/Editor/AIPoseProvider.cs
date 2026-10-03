using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// <see cref="IRigPoseProvider"/> backed by saved AI pose data. It never talks to the AI: it reads the FINAL poses stored in the pose asset (the AI motion after
    /// validation and constraints), resamples them to the requested frame count and hands them to the unchanged rig renderer. Nothing is cleaned or smoothed
    /// again on the way: what was saved is what is built. (Re-running the clean-up is an explicit action: <see cref="AIPoseAsset.Reclean"/>.)
    /// </summary>
    public sealed class AIPoseProvider : IRigPoseProvider
    {
        private readonly AIPoseAsset asset;
        private readonly RigDefinition rig;
        private readonly PoseCleanupSettings cleanup;

        /// <summary>Check of the poses of the last <see cref="GetPoses"/> call (read-only: connectivity, foot travel, loop closing) plus notes about the asset.</summary>
        public PoseReport LastReport { get; private set; }
        /// <summary>The final poses of the asset, at the asset's own frame count (before resampling).</summary>
        public RigPose[] LastCleaned { get; private set; }

        public AIPoseProvider(AIPoseAsset asset, RigDefinition rig, PoseCleanupSettings cleanup)
        {
            this.asset = asset ?? throw new ArgumentNullException(nameof(asset));
            this.rig = rig ?? throw new ArgumentNullException(nameof(rig));
            this.cleanup = cleanup ?? new PoseCleanupSettings();
        }

        /// <param name="animation">Ignored: the asset already is one specific animation.</param>
        /// <param name="cycle">Ignored: loops are resampled cyclically from the asset's own cycle.</param>
        /// <param name="intensity">Scales all joint rotations (the preset's rig intensity); root movement is not scaled.</param>
        public RigPose[] GetPoses(string animation, int frameCount, int cycle, float intensity)
        {
            var report = new PoseReport { Kind = asset.animation };
            RigPose[] final;
            if (asset.frames.Count > 0) final = asset.FinalPoses();
            else if (asset.sourceFrames.Count > 0)
            {
                // an asset made by hand or by an older version without final poses: clean it now (the result is not stored)
                var cleaned = PoseCleanup.Run(rig, asset.SourcePoses(), asset.animation, asset.loop, cleanup);
                final = cleaned.Poses; report = cleaned.Report;
                report.Warnings.Add("The pose asset had no final poses; they were computed from its source poses. Use Re-apply Constraints to store them.");
            }
            else throw new InvalidOperationException($"'{asset.name}' contains no pose frames.");

            float[][] ch = PoseChannels.FromPoses(final);
            PoseValidator.Check(rig, ch, asset.animation, asset.loop, report);
            if (!string.IsNullOrEmpty(asset.rigSignature) && asset.rigSignature != AIPoseAsset.SignatureOf(rig))
                report.Warnings.Add("The rig was edited after these poses were finalised. The constraints used the old rig: use Re-apply Constraints to adapt them.");
            if (report.Errors.Count > 0) throw new PoseRejectedException(report);
            LastReport = report;
            LastCleaned = final;

            float[][] resampled = PoseChannels.Resample(ch, frameCount, asset.loop);
            bool changed = resampled.Length != ch.Length || !Mathf.Approximately(intensity, 1f);
            if (!Mathf.Approximately(intensity, 1f))
                foreach (var frame in resampled)
                    for (int p = 0; p < RigDefinition.PartCount; p++) frame[p] *= intensity;
            if (changed && cleanup.constraints != null && cleanup.constraints.enabled)
            {
                // another frame count or intensity than the finalised poses: only the guarantees (limits) are applied again, the motion is left alone
                var scratch = new PoseReport { Kind = asset.animation };
                PoseConstraints.Enforce(rig, resampled, asset.animation, asset.loop, cleanup, scratch);
            }
            return PoseChannels.ToPoses(resampled);
        }
    }
}
