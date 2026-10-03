using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    public enum ContributionLevel { Negligible, Moderate, Significant }

    /// <summary>
    /// How much of a pose sequence is the AI's own motion: the difference to a procedural reference animation (the Rig method's Idle/Walk/Run/Attack
    /// for the same frame count), measured on the joints the AI actually determines. Loops are compared at the best cyclic alignment so a different
    /// starting phase does not count as a different motion.
    /// Levels: below 5 degrees mean difference = negligible (a few degrees of correction is not AI motion), 5-15 = moderate, above 15 = significant.
    /// </summary>
    public sealed class PoseContribution
    {
        public const float NegligibleBelow = 5f, SignificantAbove = 15f;

        public float MeanDeviationDegrees;       // mean |AI - reference| over the measured joints and frames, at the best alignment
        public float AiMotionShare;              // 0..1: share of the AI joint motion (time curves) the procedural reference does not explain (1 - mean r^2 per joint)
        public float MotionDeviationDegrees;     // the part of the difference that is movement (each joint's average angle removed)
        public float StanceOffsetDegrees;        // the part that is a constant difference in stance (average angle of each joint)
        public int AlignmentShift;               // frames the reference was rotated to align it
        public float AiRangeDegrees, ReferenceRangeDegrees;   // mean peak-to-peak per joint
        public ContributionLevel Level;
        public string Summary = "";

        private static readonly RigPart[] Measured =
        {
            RigPart.Body, RigPart.Head, RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.ArmFarUpper, RigPart.ArmFarLower,
            RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.LegFarUpper, RigPart.LegFarLower,
        };

        /// <summary>A per-joint comparison (peak-to-peak range and mean angle of the AI poses and of the reference, in degrees) for reports and benchmarks.</summary>
        public static string JointTable(IReadOnlyList<RigPose> ai, IReadOnlyList<RigPose> reference, bool loop, int shift)
        {
            int n = Math.Min(ai.Count, reference.Count);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("joint           range AI / ref      mean AI / ref");
            foreach (RigPart p in Measured)
            {
                float lo = float.MaxValue, hi = float.MinValue, rlo = float.MaxValue, rhi = float.MinValue; double ma = 0, mr = 0;
                for (int i = 0; i < n; i++)
                {
                    float v = ai[i][p] * Mathf.Rad2Deg, rv = reference[(i + shift) % n][p] * Mathf.Rad2Deg;
                    lo = Mathf.Min(lo, v); hi = Mathf.Max(hi, v); rlo = Mathf.Min(rlo, rv); rhi = Mathf.Max(rhi, rv); ma += v; mr += rv;
                }
                sb.AppendLine($"{p,-14} {hi - lo,5:0} / {rhi - rlo,-5:0}      {ma / n,6:0} / {mr / n,-6:0}");
            }
            return sb.ToString();
        }

        /// <summary>The largest single difference to the reference (at the best alignment) and where it is.</summary>
        public void MaxDeviation(IReadOnlyList<RigPose> ai, IReadOnlyList<RigPose> reference, out float max, out string where)
        {
            int n = Math.Min(ai.Count, reference.Count);
            max = 0f; where = "";
            for (int i = 0; i < n; i++)
                foreach (RigPart p in Measured)
                {
                    float d = Mathf.Abs(ai[i][p] - reference[(i + AlignmentShift) % n][p]) * Mathf.Rad2Deg;
                    if (d > max) { max = d; where = $"{p}, frame {i}"; }
                }
        }

        public static ContributionLevel Classify(float meanDeviation) =>
            meanDeviation < NegligibleBelow ? ContributionLevel.Negligible : meanDeviation <= SignificantAbove ? ContributionLevel.Moderate : ContributionLevel.Significant;

        public static PoseContribution Compute(IReadOnlyList<RigPose> ai, IReadOnlyList<RigPose> reference, bool loop)
        {
            int n = Math.Min(ai.Count, reference.Count);
            var c = new PoseContribution();
            if (n == 0) { c.Summary = "no poses"; return c; }

            float best = float.MaxValue; int bestShift = 0;
            for (int shift = 0; shift < (loop ? n : 1); shift++)
            {
                float d = 0;
                for (int i = 0; i < n; i++)
                    foreach (RigPart p in Measured) d += Mathf.Abs(ai[i][p] - reference[(i + shift) % n][p]) * Mathf.Rad2Deg;
                d /= n * Measured.Length;
                if (d < best) { best = d; bestShift = shift; }
            }
            c.MeanDeviationDegrees = best;
            c.AlignmentShift = bestShift;

            double aiRange = 0, refRange = 0, unexplained = 0, motionDev = 0, stance = 0; int counted = 0;
            foreach (RigPart p in Measured)
            {
                float lo = float.MaxValue, hi = float.MinValue, rlo = float.MaxValue, rhi = float.MinValue;
                var a = new double[n]; var r = new double[n];
                for (int i = 0; i < n; i++)
                {
                    float v = ai[i][p] * Mathf.Rad2Deg, rv = reference[(i + bestShift) % n][p] * Mathf.Rad2Deg;
                    a[i] = v; r[i] = rv;
                    lo = Mathf.Min(lo, v); hi = Mathf.Max(hi, v); rlo = Mathf.Min(rlo, rv); rhi = Mathf.Max(rhi, rv);
                }
                aiRange += hi - lo; refRange += rhi - rlo;
                double meanA = 0, meanR = 0; for (int i = 0; i < n; i++) { meanA += a[i]; meanR += r[i]; }
                meanA /= n; meanR /= n;
                stance += Math.Abs(meanA - meanR);
                for (int i = 0; i < n; i++) motionDev += Math.Abs((a[i] - meanA) - (r[i] - meanR)) / n;
                // how much of this joint's motion curve the reference curve explains (squared correlation)
                double ma = 0, mr = 0; for (int i = 0; i < n; i++) { ma += a[i]; mr += r[i]; }
                ma /= n; mr /= n;
                double saa = 0, srr = 0, sar = 0;
                for (int i = 0; i < n; i++) { saa += (a[i] - ma) * (a[i] - ma); srr += (r[i] - mr) * (r[i] - mr); sar += (a[i] - ma) * (r[i] - mr); }
                if (saa / n < 1.0) continue;                    // the AI hardly moves this joint (< 1 deg^2): nothing to explain
                double r2 = srr > 1e-9 ? Math.Max(0.0, sar) * Math.Max(0.0, sar) / (saa * srr) : 0.0;
                unexplained += 1.0 - r2; counted++;
            }
            c.MotionDeviationDegrees = (float)(motionDev / Measured.Length);
            c.StanceOffsetDegrees = (float)(stance / Measured.Length);
            c.AiRangeDegrees = (float)(aiRange / Measured.Length);
            c.ReferenceRangeDegrees = (float)(refRange / Measured.Length);
            c.AiMotionShare = counted > 0 ? (float)(unexplained / counted) : 0f;
            c.Level = Classify(best);
            c.Summary = $"AI contribution: {c.Level.ToString().ToLowerInvariant()} - mean {best:0.0} deg from the procedural reference" +
                        $"{(loop && bestShift != 0 ? $" (aligned, phase shift {bestShift} frames)" : "")}, of which movement {c.MotionDeviationDegrees:0.0} deg and stance {c.StanceOffsetDegrees:0.0} deg, " +
                        $"{c.AiMotionShare:P0} of the joint motion curves is not explained by the reference; joint range {c.AiRangeDegrees:0} deg (AI) vs {c.ReferenceRangeDegrees:0} deg (reference).";
            if (c.StanceOffsetDegrees > 2f * c.MotionDeviationDegrees && c.StanceOffsetDegrees >= NegligibleBelow)
                c.Summary += $" Note: this is mostly a constant difference in stance; the movement itself differs by only {c.MotionDeviationDegrees:0.0} deg.";
            return c;
        }
    }
}
