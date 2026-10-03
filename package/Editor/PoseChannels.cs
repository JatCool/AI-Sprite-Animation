using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// A pose sequence as plain float channels (degrees per part, then root x, root y, hop), which is what validation and smoothing work on.
    /// Channel layout: [0 .. PartCount-1] = joint angles in degrees, then RootX, RootY, Hop (sprite pixels).
    /// </summary>
    public static class PoseChannels
    {
        public const int RootX = RigDefinition.PartCount, RootY = RootX + 1, Hop = RootX + 2, Count = RootX + 3;

        public static float[][] FromPoses(IReadOnlyList<RigPose> poses)
        {
            var ch = new float[poses.Count][];
            for (int i = 0; i < ch.Length; i++)
            {
                ch[i] = new float[Count];
                var p = poses[i];
                if (p == null) continue;
                for (int k = 0; k < RigDefinition.PartCount; k++) ch[i][k] = p.angle[k] * Mathf.Rad2Deg;
                ch[i][RootX] = p.root.x; ch[i][RootY] = p.root.y; ch[i][Hop] = p.hop;
            }
            return ch;
        }

        public static RigPose[] ToPoses(float[][] ch)
        {
            var poses = new RigPose[ch.Length];
            for (int i = 0; i < ch.Length; i++) poses[i] = ToPose(ch[i]);
            return poses;
        }

        public static RigPose ToPose(float[] row)
        {
            var p = new RigPose();
            for (int k = 0; k < RigDefinition.PartCount; k++) p.angle[k] = row[k] * Mathf.Deg2Rad;
            p.root = new Vector2(row[RootX], row[RootY]);
            p.hop = row[Hop];
            return p;
        }

        public static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>Linear interpolation along a time series (cyclic when <paramref name="loop"/>), used to fill invalid samples.</summary>
        public static float Sample(float[][] ch, int channel, float t, bool loop)
        {
            int n = ch.Length;
            if (n == 0) return 0f;
            if (loop) { t %= n; if (t < 0) t += n; }
            else t = Mathf.Clamp(t, 0, n - 1);
            int a = Mathf.FloorToInt(t), b = loop ? (a + 1) % n : Mathf.Min(a + 1, n - 1);
            return Mathf.Lerp(ch[a][channel], ch[b][channel], t - a);
        }

        /// <summary>
        /// Replaces non-finite samples by linear interpolation along time (cyclic for loops; edges copy the nearest finite sample; a channel without any finite sample becomes 0).
        /// Returns the number of replaced samples.
        /// </summary>
        public static int FillNonFinite(float[][] ch, bool loop)
        {
            int n = ch.Length, filled = 0;
            for (int c = 0; c < Count; c++)
            {
                var good = new List<int>();
                for (int i = 0; i < n; i++) if (IsFinite(ch[i][c])) good.Add(i);
                if (good.Count == n) continue;
                if (good.Count == 0) { for (int i = 0; i < n; i++) ch[i][c] = 0f; filled += n; continue; }
                var source = Clone(ch);
                for (int i = 0; i < n; i++)
                {
                    if (IsFinite(source[i][c])) continue;
                    int prev = -1, next = -1;
                    for (int k = i - 1; k >= 0; k--) if (IsFinite(source[k][c])) { prev = k; break; }
                    for (int k = i + 1; k < n; k++) if (IsFinite(source[k][c])) { next = k; break; }
                    if (loop)
                    {
                        if (prev < 0) prev = good[good.Count - 1] - n;
                        if (next < 0) next = good[0] + n;
                    }
                    if (prev < 0) ch[i][c] = source[next][c];
                    else if (next < 0) ch[i][c] = source[prev][c];
                    else ch[i][c] = Mathf.Lerp(source[((prev % n) + n) % n][c], source[next % n][c], (i - prev) / (float)(next - prev));
                    filled++;
                }
            }
            return filled;
        }

        public static float[][] Clone(float[][] ch)
        {
            var c = new float[ch.Length][];
            for (int i = 0; i < c.Length; i++) c[i] = (float[])ch[i].Clone();
            return c;
        }

        /// <summary>Resamples a sequence to <paramref name="count"/> frames. Loops are sampled cyclically (frame count/N maps to frame 0).</summary>
        public static float[][] Resample(float[][] ch, int count, bool loop)
        {
            if (ch.Length == count) return Clone(ch);
            var result = new float[count][];
            for (int i = 0; i < count; i++)
            {
                float t = loop ? i * (float)ch.Length / count : (count > 1 ? i * (ch.Length - 1f) / (count - 1) : 0f);
                result[i] = new float[Count];
                for (int k = 0; k < Count; k++) result[i][k] = Sample(ch, k, t, loop);
            }
            return result;
        }
    }
}
