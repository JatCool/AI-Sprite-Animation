using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    public sealed class CycleExtraction
    {
        public RigPose[] Poses;               // the requested number of poses, ready for validation (null if nothing usable was found)
        public string Method = "";            // how the animation was cut out of the video
        public float Start, Length;           // in video frames
        public float Closure;                 // mean joint mismatch (degrees) where the cycle closes
        public float Swing;                   // thigh swing (degrees, near minus far thigh, peak to peak) inside the extracted part
        public string Notes = "";
        public bool Ok => Poses != null;
    }

    /// <summary>
    /// Cuts one animation out of a generated clip of a person moving. The clip is not an animation: it has a start and an end, drifts, and rarely
    /// contains exactly one gait cycle. For loops the best repeatable stretch is searched:
    ///   * a full cycle: the clip contains a stretch whose end looks like its start;
    ///   * a half cycle with mirror symmetry: walking and running are symmetric (the right leg does what the left leg did half a cycle earlier),
    ///     so a stretch whose end looks like its start with near and far limbs swapped is half of a cycle; the other half is its mirror image.
    /// One-shots (attack) take the stretch with the most arm activity that ends close to where it started.
    /// Everything is measured on the rig's own joint angles, so the result is already retargeted to the character.
    /// </summary>
    public static class MotionCycleExtractor
    {
        private static readonly RigPart[] Core =
        {
            RigPart.Body, RigPart.Head, RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.ArmFarUpper, RigPart.ArmFarLower,
            RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.LegFarUpper, RigPart.LegFarLower,
        };
        private static readonly float[] Weight = { 0.5f, 0.3f, 0.4f, 0.3f, 0.4f, 0.3f, 1f, 0.7f, 1f, 0.7f };

        private static readonly (RigPart, RigPart)[] SwapPairs =
        {
            (RigPart.ArmNearUpper, RigPart.ArmFarUpper), (RigPart.ArmNearLower, RigPart.ArmFarLower),
            (RigPart.LegNearUpper, RigPart.LegFarUpper), (RigPart.LegNearLower, RigPart.LegFarLower),
        };

        public static CycleExtraction Extract(IReadOnlyList<RigPose> video, int n, string kind, bool loop, OpenPoseMapperSettings mapperSettings = null)
        {
            var result = new CycleExtraction();
            int T = video.Count;
            if (T < 6) { result.Notes = $"The clip has only {T} usable frames."; return result; }

            float[][] ch = PoseChannels.FromPoses(video);
            PoseChannels.FillNonFinite(ch, false);
            var sig = new Signal(ch);
            string k = (kind ?? "").ToLowerInvariant();

            if (!loop) ExtractWindow(sig, n, result);
            else if (k == "walk" || k == "run") ExtractGait(sig, n, k == "run" ? 40f : 30f, result);
            else ExtractIdle(sig, n, result);

            if (result.Poses != null) OpenPoseMapper.DeriveSecondary(result.Poses, kind, loop, mapperSettings);
            return result;
        }

        // ---------------------------------------------------------------- walk / run

        private static void ExtractGait(Signal sig, int n, float minSwing, CycleExtraction r)
        {
            int T = sig.Length;
            float bestHalf = float.MaxValue, bestFull = float.MaxValue;
            float halfT0 = 0, halfH = 0, fullT0 = 0, fullP = 0, halfSwing = 0, fullSwing = 0, halfE = 0, fullE = 0;
            for (float t0 = 0; t0 <= T - 1 - 4; t0 += 0.5f)
                for (float len = 4; t0 + len <= T - 1 + 1e-3f; len += 0.5f)
                {
                    float swing = sig.ThighSwing(t0, t0 + len);
                    if (swing < minSwing) continue;
                    // half cycle: the end must look like the start with near and far limbs exchanged
                    float eHalf = Diff(Swap(sig.At(t0)), sig.At(t0 + len));
                    if (eHalf / swing < bestHalf) { bestHalf = eHalf / swing; halfT0 = t0; halfH = len; halfSwing = swing; halfE = eHalf; }
                    // full cycle: the end must look like the start (the stretch must contain both swings)
                    if (len >= 6 && swing >= 1.4f * minSwing)
                    {
                        float eFull = Diff(sig.At(t0), sig.At(t0 + len));
                        if (eFull / swing < bestFull) { bestFull = eFull / swing; fullT0 = t0; fullP = len; fullSwing = swing; fullE = eFull; }
                    }
                }

            if (bestHalf == float.MaxValue && bestFull == float.MaxValue)
            {
                r.Notes = $"The clip contains no walking/running swing of the legs (needs at least {minSwing:0} degrees between the thighs).";
                return;
            }
            // A genuine full cycle keeps the asymmetries of the clip; prefer it unless the half cycle closes much better.
            bool useFull = bestFull != float.MaxValue && bestFull <= 1.25f * bestHalf;
            var poses = new float[n][];
            if (useFull)
            {
                for (int i = 0; i < n; i++) poses[i] = sig.At(fullT0 + fullP * i / n);
                DetrendRoot(poses, sig, fullT0, fullP, false);
                r.Method = "full cycle found in the clip"; r.Start = fullT0; r.Length = fullP; r.Closure = fullE; r.Swing = fullSwing;
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    float u = 2f * i / n;
                    if (u < 1f) poses[i] = sig.At(halfT0 + halfH * u);
                    else poses[i] = Swap(sig.At(halfT0 + halfH * (u - 1f)));
                }
                DetrendRoot(poses, sig, halfT0, halfH, true);
                r.Method = "half cycle + mirror symmetry (right and left limbs exchanged for the second half)"; r.Start = halfT0; r.Length = halfH; r.Closure = halfE; r.Swing = halfSwing;
            }
            r.Poses = PoseChannels.ToPoses(poses);
            r.Notes = $"{r.Method}; frames {r.Start:0.#}-{r.Start + r.Length:0.#} of the clip, seam mismatch {r.Closure:0.0} deg, thigh swing {r.Swing:0} deg.";
        }

        // Walking in place: the hip travel inside the chosen stretch is a drift, not an animation. Remove the line between its end points.
        private static void DetrendRoot(float[][] poses, Signal sig, float t0, float len, bool repeated)
        {
            float a = sig.At(t0)[PoseChannels.RootX], b = sig.At(t0 + len)[PoseChannels.RootX];
            int n = poses.Length;
            for (int i = 0; i < n; i++)
            {
                float u = repeated ? (2f * i / n) % 1f : (float)i / n;
                poses[i][PoseChannels.RootX] -= a + (b - a) * u;
            }
        }

        // ---------------------------------------------------------------- idle (any other loop)

        private static void ExtractIdle(Signal sig, int n, CycleExtraction r)
        {
            int T = sig.Length;
            float best = float.MaxValue, bestT0 = 0, bestLen = 0;
            for (float t0 = 0; t0 <= T - 1 - 6; t0 += 0.5f)
                for (float len = 6; t0 + len <= T - 1 + 1e-3f; len += 0.5f)
                {
                    float e = Diff(sig.At(t0), sig.At(t0 + len)) - 0.08f * len;   // closes well, and prefers the longer stretch
                    if (e < best) { best = e; bestT0 = t0; bestLen = len; }
                }
            var poses = new float[n][];
            float closure = Diff(sig.At(bestT0), sig.At(bestT0 + bestLen));
            if (closure <= 4f)
            {
                for (int i = 0; i < n; i++) poses[i] = sig.At(bestT0 + bestLen * i / n);
                r.Method = "closed stretch of the clip";
            }
            else
            {
                // nothing closes by itself: play the whole clip forward and back
                bestT0 = 0; bestLen = T - 1; closure = 0f;
                for (int i = 0; i < n; i++)
                {
                    float u = 2f * i / n;
                    poses[i] = sig.At((T - 1) * (u <= 1f ? u : 2f - u));
                }
                r.Method = "clip played forward and back (no stretch closes by itself)";
            }
            DetrendRoot(poses, sig, bestT0, bestLen, false);
            r.Start = bestT0; r.Length = bestLen; r.Closure = closure; r.Swing = sig.ThighSwing(bestT0, bestT0 + bestLen);
            r.Poses = PoseChannels.ToPoses(poses);
            r.Notes = $"{r.Method}; frames {r.Start:0.#}-{r.Start + r.Length:0.#}, seam mismatch {r.Closure:0.0} deg.";
        }

        // ---------------------------------------------------------------- attack (one-shot)

        private static void ExtractWindow(Signal sig, int n, CycleExtraction r)
        {
            int T = sig.Length;
            int minLen = Mathf.Min(T - 1, 8);
            float best = float.MinValue, bestT0 = 0, bestLen = 0;
            for (float t0 = 0; t0 <= T - 1 - minLen; t0 += 0.5f)
                for (float len = minLen; t0 + len <= T - 1 + 1e-3f; len += 0.5f)
                {
                    float activity = sig.Range(RigPart.ArmNearUpper, t0, t0 + len) + 0.5f * sig.Range(RigPart.ArmNearLower, t0, t0 + len)
                                   + 0.5f * sig.Range(RigPart.ArmFarUpper, t0, t0 + len) + 0.5f * sig.Range(RigPart.Body, t0, t0 + len);
                    float endMismatch = Diff(sig.At(t0), sig.At(t0 + len));
                    float score = activity - 0.7f * endMismatch + 0.3f * len;
                    if (score > best) { best = score; bestT0 = t0; bestLen = len; }
                }
            var poses = new float[n][];
            for (int i = 0; i < n; i++) poses[i] = sig.At(bestT0 + bestLen * (n > 1 ? i / (float)(n - 1) : 0f));
            r.Method = "most active stretch of the clip that ends where it started";
            r.Start = bestT0; r.Length = bestLen; r.Closure = Diff(sig.At(bestT0), sig.At(bestT0 + bestLen));
            r.Swing = Mathf.Max(sig.Range(RigPart.ArmNearUpper, bestT0, bestT0 + bestLen), sig.ThighSwing(bestT0, bestT0 + bestLen));
            r.Poses = PoseChannels.ToPoses(poses);
            r.Notes = $"{r.Method}; frames {r.Start:0.#}-{r.Start + r.Length:0.#}, start/end mismatch {r.Closure:0.0} deg, near arm range {sig.Range(RigPart.ArmNearUpper, bestT0, bestT0 + bestLen):0} deg.";
        }

        // ---------------------------------------------------------------- helpers

        private static float Diff(float[] a, float[] b)
        {
            float sum = 0, w = 0;
            for (int i = 0; i < Core.Length; i++) { sum += Weight[i] * Mathf.Abs(a[(int)Core[i]] - b[(int)Core[i]]); w += Weight[i]; }
            return sum / w;
        }

        private static float[] Swap(float[] v)
        {
            var c = (float[])v.Clone();
            foreach (var (near, far) in SwapPairs) { c[(int)near] = v[(int)far]; c[(int)far] = v[(int)near]; }
            return c;
        }

        private sealed class Signal
        {
            private readonly float[][] ch;
            public Signal(float[][] channels) { ch = channels; }
            public int Length => ch.Length;

            public float[] At(float t)
            {
                t = Mathf.Clamp(t, 0f, ch.Length - 1);
                int a = Mathf.FloorToInt(t), b = Mathf.Min(a + 1, ch.Length - 1);
                float f = t - a;
                var v = new float[PoseChannels.Count];
                for (int c = 0; c < v.Length; c++) v[c] = Mathf.Lerp(ch[a][c], ch[b][c], f);
                return v;
            }

            public float Range(RigPart part, float t0, float t1)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (float t = t0; t <= t1 + 1e-3f; t += 0.5f) { float v = At(t)[(int)part]; lo = Mathf.Min(lo, v); hi = Mathf.Max(hi, v); }
                return hi - lo;
            }

            // Peak-to-peak of (near thigh - far thigh) inside [t0, t1]: how far apart the legs get while the stretch lasts.
            public float ThighSwing(float t0, float t1)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (float t = t0; t <= t1 + 1e-3f; t += 0.5f)
                {
                    float[] v = At(t);
                    float d = v[(int)RigPart.LegNearUpper] - v[(int)RigPart.LegFarUpper];
                    lo = Mathf.Min(lo, d); hi = Mathf.Max(hi, d);
                }
                return hi - lo;
            }
        }
    }
}
