using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Clean-up of raw pose-estimator keypoints before they are mapped to the rig: which way the person faces, and keeping the left/right limbs
    /// consistent over time. Pose estimators label a side-view person's limbs by appearance, so the labels can flip from frame to frame
    /// (especially when the legs cross); the rig needs one limb to stay the same limb.
    /// </summary>
    public static class OpenPoseTracking
    {
        // OpenPose body-18 groups that are swapped together: (shoulder, elbow, wrist) and (hip, knee, ankle) of the right and the left side.
        private static readonly int[] RightArm = { 2, 3, 4 }, LeftArm = { 5, 6, 7 }, RightLeg = { 8, 9, 10 }, LeftLeg = { 11, 12, 13 };

        /// <summary>+1 if the person faces +x (image right), -1 if it faces -x, judged by the nose (and eyes) relative to the neck. 0 if undecidable.</summary>
        public static int DetectFacing(IReadOnlyList<OpenPoseFrame> frames, float minConfidence = 0.15f)
        {
            var dx = new List<float>();
            foreach (var f in frames)
            {
                if (!f.Has(1, minConfidence)) continue;
                float sum = 0; int cnt = 0;
                foreach (int idx in new[] { 0, 14, 15 }) if (f.Has(idx, minConfidence)) { sum += f.point[idx].x - f.point[1].x; cnt++; }
                if (cnt > 0) dx.Add(sum / cnt);
            }
            if (dx.Count == 0) return 0;
            dx.Sort();
            float median = dx[dx.Count / 2];
            return Mathf.Abs(median) < 1e-3f ? 0 : (median > 0 ? 1 : -1);
        }

        /// <summary>Mirrors every keypoint horizontally (x -> -x). Angles are all that matters downstream, so the mirror axis is irrelevant.</summary>
        public static void Mirror(IReadOnlyList<OpenPoseFrame> frames)
        {
            foreach (var f in frames)
                for (int i = 0; i < OpenPoseFrame.Count; i++) f.point[i] = new Vector2(-f.point[i].x, f.point[i].y);
        }

        /// <summary>
        /// Makes limb identities continuous in time: whenever swapping the left and right arm (or leg) of a frame explains the movement since the previous
        /// frame clearly better than keeping the labels, the two are swapped. Positions are compared relative to the hips, with a constant-velocity
        /// prediction. Returns the number of swaps.
        /// </summary>
        public static int FixLimbSwaps(IList<OpenPoseFrame> frames, float minConfidence = 0.15f)
        {
            int swaps = 0;
            swaps += TrackPair(frames, RightArm, LeftArm, minConfidence, new[] { 1, 2 });     // compare elbow and wrist
            swaps += TrackPair(frames, RightLeg, LeftLeg, minConfidence, new[] { 1, 2 });     // compare knee and ankle
            return swaps;
        }

        private static int TrackPair(IList<OpenPoseFrame> frames, int[] a, int[] b, float c, int[] compare)
        {
            int swaps = 0;
            Vector2[] prevA = null, prevB = null, prev2A = null, prev2B = null;
            foreach (var f in frames)
            {
                Vector2 origin = Origin(f, c);
                Vector2[] curA = Collect(f, a, compare, origin, c), curB = Collect(f, b, compare, origin, c);
                if (curA != null && curB != null && prevA != null && prevB != null)
                {
                    Vector2[] predA = Predict(prevA, prev2A), predB = Predict(prevB, prev2B);
                    float keep = Dist(curA, predA) + Dist(curB, predB), swap = Dist(curA, predB) + Dist(curB, predA);
                    if (swap < 0.7f * keep)
                    {
                        for (int k = 0; k < a.Length; k++)
                        {
                            (f.point[a[k]], f.point[b[k]]) = (f.point[b[k]], f.point[a[k]]);
                            (f.confidence[a[k]], f.confidence[b[k]]) = (f.confidence[b[k]], f.confidence[a[k]]);
                        }
                        (curA, curB) = (curB, curA);
                        swaps++;
                    }
                }
                if (curA != null && curB != null) { prev2A = prevA; prev2B = prevB; prevA = curA; prevB = curB; }
            }
            return swaps;
        }

        private static Vector2 Origin(OpenPoseFrame f, float c)
        {
            Vector2 sum = Vector2.zero; int cnt = 0;
            foreach (int i in new[] { 8, 11 }) if (f.Has(i, c)) { sum += f.point[i]; cnt++; }
            return cnt > 0 ? sum / cnt : (f.Has(1, c) ? f.point[1] : Vector2.zero);
        }

        private static Vector2[] Collect(OpenPoseFrame f, int[] group, int[] compare, Vector2 origin, float c)
        {
            var result = new Vector2[compare.Length];
            for (int k = 0; k < compare.Length; k++)
            {
                int idx = group[compare[k]];
                if (!f.Has(idx, c)) return null;
                result[k] = f.point[idx] - origin;
            }
            return result;
        }

        private static Vector2[] Predict(Vector2[] prev, Vector2[] prev2)
        {
            if (prev2 == null) return prev;
            var p = new Vector2[prev.Length];
            for (int k = 0; k < p.Length; k++) p[k] = prev[k] + 0.5f * (prev[k] - prev2[k]);
            return p;
        }

        private static float Dist(Vector2[] x, Vector2[] y)
        {
            float d = 0;
            for (int k = 0; k < x.Length; k++) d += Vector2.Distance(x[k], y[k]);
            return d;
        }
    }
}
