using System;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Cleans jitter out of a pose sequence without flattening it: an edge-preserving temporal filter (large deliberate changes such as an attack swing
    /// are left alone, small noise is averaged out), optional loop closing and optional snapping to a pixel-art friendly angle step.
    /// Works on <see cref="PoseChannels"/>.
    /// </summary>
    public static class PoseSmoother
    {
        /// <summary>
        /// Makes a looping sequence close seamlessly. A cycle that is already periodic (a sampled sine, a clean estimate) is left untouched; only when the
        /// step from the last frame back to the first is larger than every step inside the sequence (a seam: the estimate drifted or ended in another pose)
        /// the excess is spread linearly over the cycle, so the seam becomes as large as the largest inner step. Frame 0 is never changed.
        /// </summary>
        public static void CloseLoop(float[][] ch)
        {
            int n = ch.Length;
            if (n < 3) return;
            for (int c = 0; c < PoseChannels.Count; c++)
            {
                if (c == PoseChannels.RootY || c == PoseChannels.Hop) continue;   // flight phases are periodic by nature
                float wrapStep = ch[0][c] - ch[n - 1][c], maxInner = 0f;
                for (int i = 1; i < n; i++) maxInner = Mathf.Max(maxInner, Mathf.Abs(ch[i][c] - ch[i - 1][c]));
                float excess = Mathf.Abs(wrapStep) - maxInner;
                if (excess <= 0f) continue;
                float k = Mathf.Sign(wrapStep) * excess / n;   // inner steps grow by k, the seam shrinks by k*(n-1): they meet
                for (int i = 1; i < n; i++) ch[i][c] += k * i;
            }
        }

        public static void Smooth(float[][] ch, bool loop, PoseCleanupSettings s)
        {
            int n = ch.Length;
            if (n < 3 || s.smoothing <= 0f) return;
            for (int pass = 0; pass < s.passes; pass++)
            {
                var src = PoseChannels.Clone(ch);
                for (int c = 0; c < PoseChannels.Count; c++)
                {
                    float strength = Mathf.Min(0.5f, s.smoothing * (IsNoisyChannel(c) ? 1f + s.rootAndHeadSmoothing : 1f));
                    float preserve = c >= PoseChannels.RootX ? 1.5f : s.preserveDegrees;   // root: pixels, not degrees
                    for (int i = 0; i < n; i++)
                    {
                        if (!loop && (i == 0 || i == n - 1)) continue;   // one-shots keep their first and last pose
                        float sum = src[i][c], weight = 1f;
                        for (int side = -1; side <= 1; side += 2)
                        {
                            int j = loop ? (i + side + n) % n : i + side;
                            if (j < 0 || j >= n) continue;
                            float d = Mathf.Abs(src[j][c] - src[i][c]) / preserve;
                            float w = strength * Mathf.Exp(-d * d);
                            sum += w * src[j][c]; weight += w;
                        }
                        ch[i][c] = sum / weight;
                    }
                }
            }
        }

        /// <summary>Snaps every joint angle to a multiple of <paramref name="stepDegrees"/> (stepped, deliberate poses).</summary>
        public static void Quantize(float[][] ch, float stepDegrees)
        {
            if (stepDegrees <= 0f) return;
            foreach (var frame in ch)
                for (int p = 0; p < RigDefinition.PartCount; p++)
                    frame[p] = Mathf.Round(frame[p] / stepDegrees) * stepDegrees;
        }

        private static bool IsNoisyChannel(int c) =>
            c == (int)RigPart.Head || c == (int)RigPart.Hair || c == PoseChannels.RootX || c == PoseChannels.RootY || c == PoseChannels.Hop;
    }
}
