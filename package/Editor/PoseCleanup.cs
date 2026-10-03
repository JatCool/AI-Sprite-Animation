using System;
using System.Collections.Generic;

namespace AISpriteAnimation
{
    /// <summary>
    /// The clean-up stage between any AI pose estimate and the rig renderer:
    ///   validate/repair (wrap-around, impossible angles, broken frames, flips)
    ///   -> constraints (spikes, lopsided gait, speed, leg extension, foot jumps, root lurches, foot lock, weapon)
    ///   -> close loops -> smooth -> quantise
    ///   -> the limits once more (smoothing may move a value back over a limit) -> final check.
    /// Malformed AI output never reaches the AnimationClip: a sequence that cannot be repaired is rejected and an exception explains why.
    /// The result is the FINAL pose data: it is what gets saved in the pose asset and what the rig renders.
    /// </summary>
    public static class PoseCleanup
    {
        public sealed class Result
        {
            public RigPose[] Poses;
            public PoseReport Report;
        }

        /// <summary>Cleans <paramref name="raw"/> (not modified). Throws <see cref="PoseRejectedException"/> if the sequence is unusable.</summary>
        /// <param name="reference">Optional: the procedural animation of the same type, to put the AI-vs-procedural numbers in the report.</param>
        public static Result Run(RigDefinition rig, IReadOnlyList<RigPose> raw, string kind, bool loop, PoseCleanupSettings settings, IReadOnlyList<RigPose> reference = null)
        {
            float[][] source = PoseChannels.FromPoses(raw);
            float[][] ch = PoseChannels.Clone(source);

            // What the AI delivered (undetected joints filled so it can be measured).
            float[][] measurable = PoseChannels.Clone(source);
            PoseChannels.FillNonFinite(measurable, loop);

            PoseReport report = PoseValidator.Repair(rig, ch, kind, loop, settings);
            if (report.Rejected) throw new PoseRejectedException(report);

            PoseConstraints.Apply(rig, ch, kind, loop, settings, report, false);

            if (loop && settings.closeLoops) PoseSmoother.CloseLoop(ch);
            if (settings.smoothingEnabled) PoseSmoother.Smooth(ch, loop, settings);
            PoseSmoother.Quantize(ch, settings.stepDegrees);

            // Smoothing and loop closing can move values back over a limit, and quantising can touch the limits: guarantee them again.
            PoseConstraints.Apply(rig, ch, kind, loop, settings, report, true);
            for (int i = 0; i < ch.Length; i++)
                for (int p = 0; p < RigDefinition.PartCount; p++)
                {
                    PoseLimits.Get(kind, p, out float lo, out float hi);
                    ch[i][p] = UnityEngine.Mathf.Clamp(ch[i][p], lo, hi);
                }

            // The earlier checks were made on the raw data; the report that goes with the result describes the final poses.
            report.Warnings.Clear();
            report.Errors.RemoveAll(e => e.StartsWith("A child bone is detached"));
            PoseValidator.Check(rig, ch, kind, loop, report);
            if (report.Errors.Count > 0) throw new PoseRejectedException(report);

            report.SourceMetrics = PoseMetrics.Measure(rig, measurable, loop);
            report.FinalMetrics = PoseMetrics.Measure(rig, ch, loop);
            MeasureChange(measurable, ch, report);
            RigPose[] finalPoses = PoseChannels.ToPoses(ch);
            if (reference != null) ComparePoses(finalPoses, reference, loop, report);
            return new Result { Poses = finalPoses, Report = report };
        }

        /// <summary>Puts the AI-vs-procedural numbers (mean and maximum joint deviation) into the report.</summary>
        public static void ComparePoses(IReadOnlyList<RigPose> final, IReadOnlyList<RigPose> reference, bool loop, PoseReport report)
        {
            PoseContribution c = PoseContribution.Compute(final, reference, loop);
            report.HasReference = true;
            report.MeanDeviationDegrees = c.MeanDeviationDegrees;
            c.MaxDeviation(final, reference, out float max, out string where);
            report.MaxDeviationDegrees = max; report.MaxDeviationWhere = where;
        }

        private static void MeasureChange(float[][] source, float[][] final, PoseReport report)
        {
            float worst = 0f; string where = "";
            for (int i = 0; i < final.Length; i++)
                for (int p = 0; p < RigDefinition.PartCount; p++)
                {
                    RigPart part = (RigPart)p;
                    if (part == RigPart.Hair || part == RigPart.FootNear || part == RigPart.FootFar || part == RigPart.Weapon) continue;   // derived joints
                    float d = Math.Abs(final[i][p] - source[i][p]);
                    if (d > worst) { worst = d; where = $"{part}, frame {i}"; }
                }
            report.MaxChangeFromSource = worst;
            report.MaxChangeWhere = worst > 0.01f ? where : "no change";
        }
    }

    public sealed class PoseRejectedException : Exception
    {
        public readonly PoseReport Report;
        public PoseRejectedException(PoseReport report) : base("The AI pose sequence was rejected by validation. " + report) { Report = report; }
    }
}
