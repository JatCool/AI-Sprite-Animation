using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Measurements of a pose sequence as the viewer would see it (rendered positions in sprite pixels: grounding rule and root translation included).
    /// Used by the constraint stage to find what is broken and by the quantitative validation report. Pure math, no side effects.
    /// </summary>
    [Serializable]
    public sealed class PoseMetrics
    {
        public int Frames;
        public float LegLength;               // hip -> knee -> foot in the rig, pixels
        public float MaxFootStep;             // largest rendered displacement of a foot between two consecutive frames, px
        public int MaxFootStepFrame;          // the later frame of that step
        public float MaxRootStep;             // largest rendered displacement of the hip (root + grounding) between two consecutive frames, px
        public float HipHeightRange;          // rendered hip height: peak to peak, px (body bob / leg compression)
        public float MinLegExtension;         // smallest hip->foot distance as a fraction of the full leg length (1 = straight leg)
        public int MinLegExtensionFrame;
        public float MaxStanceSlide;          // largest deviation of the planted foot's speed from its steady speed, px per frame
        public float MaxWeaponGap;            // weapon pivot to hand, rendered, px (0 = held)
        public float MaxWeaponBelowGround;    // how far the weapon tip hangs below the ground line, px
        public float MaxJointStep;            // largest change of one joint angle between two consecutive frames, degrees
        public float MaxSpike;                // largest single-frame spike of a joint angle (a frame sticking out of both neighbours), degrees
        public int MaxSpikeFrame;
        public RigPart MaxSpikePart;
        /// <summary>How much the upper body visibly moves: the median of the rendered peak-to-peak range (px) of the neck and both hands. Below about 1 px an idle looks frozen.</summary>
        public float VisibleMotion;

        // Rendered positions per frame (kept for the constraint stage): hip, near foot, far foot, weapon tip.
        public Vector2[] Hip, FootNear, FootFar, WeaponTip;

        private static readonly RigPart[] SpikeParts =
        {
            RigPart.Body, RigPart.Head, RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.ArmFarUpper, RigPart.ArmFarLower,
            RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.LegFarUpper, RigPart.LegFarLower,
        };

        public static float LegLengthOf(RigDefinition rig) =>
            Vector2.Distance(rig.GetJoint(RigJoint.Hip), rig.GetJoint(RigJoint.KneeNear)) + Vector2.Distance(rig.GetJoint(RigJoint.KneeNear), rig.GetJoint(RigJoint.FootNear));

        public static PoseMetrics Measure(RigDefinition rig, float[][] ch, bool loop)
        {
            var m = new PoseMetrics { Frames = ch.Length, LegLength = LegLengthOf(rig) };
            int n = ch.Length;
            if (n == 0) return m;
            m.Hip = new Vector2[n]; m.FootNear = new Vector2[n]; m.FootFar = new Vector2[n]; m.WeaponTip = new Vector2[n];
            var poses = PoseChannels.ToPoses(ch);
            float hipLo = float.MaxValue, hipHi = float.MinValue;
            m.MinLegExtension = float.MaxValue;
            float upperLen = Vector2.Distance(rig.GetJoint(RigJoint.Hip), rig.GetJoint(RigJoint.KneeNear)), lowerLen = Vector2.Distance(rig.GetJoint(RigJoint.KneeNear), rig.GetJoint(RigJoint.FootNear));
            float upperLenF = Vector2.Distance(rig.GetJoint(RigJoint.Hip), rig.GetJoint(RigJoint.KneeFar)), lowerLenF = Vector2.Distance(rig.GetJoint(RigJoint.KneeFar), rig.GetJoint(RigJoint.FootFar));
            for (int i = 0; i < n; i++)
            {
                Vector2[] j = RigKinematics.JointPositions(rig, poses[i], false, true);
                m.Hip[i] = j[(int)RigJoint.Hip]; m.FootNear[i] = j[(int)RigJoint.FootNear]; m.FootFar[i] = j[(int)RigJoint.FootFar]; m.WeaponTip[i] = j[(int)RigJoint.WeaponTip];
                hipLo = Mathf.Min(hipLo, m.Hip[i].y); hipHi = Mathf.Max(hipHi, m.Hip[i].y);
                float en = Vector2.Distance(j[(int)RigJoint.Hip], j[(int)RigJoint.FootNear]) / Mathf.Max(0.01f, upperLen + lowerLen);
                float ef = Vector2.Distance(j[(int)RigJoint.Hip], j[(int)RigJoint.FootFar]) / Mathf.Max(0.01f, upperLenF + lowerLenF);
                float e = Mathf.Min(en, ef);
                if (e < m.MinLegExtension) { m.MinLegExtension = e; m.MinLegExtensionFrame = i; }
                m.MaxWeaponGap = Mathf.Max(m.MaxWeaponGap, Vector2.Distance(j[(int)RigJoint.WeaponPivot], j[(int)RigJoint.HandNear]) - Vector2.Distance(rig.GetJoint(RigJoint.WeaponPivot), rig.GetJoint(RigJoint.HandNear)));
                m.MaxWeaponBelowGround = Mathf.Max(m.MaxWeaponBelowGround, j[(int)RigJoint.WeaponTip].y - rig.groundY);
            }
            m.HipHeightRange = hipHi - hipLo;

            int steps = n - 1 + (loop && n > 2 ? 1 : 0);
            for (int s = 1; s <= steps; s++)
            {
                int a = s - 1, b = s % n;
                float fs = Mathf.Max(Vector2.Distance(m.FootNear[a], m.FootNear[b]), Vector2.Distance(m.FootFar[a], m.FootFar[b]));
                if (fs > m.MaxFootStep) { m.MaxFootStep = fs; m.MaxFootStepFrame = b; }
                m.MaxRootStep = Mathf.Max(m.MaxRootStep, Vector2.Distance(m.Hip[a], m.Hip[b]));
                for (int c = 0; c < RigDefinition.PartCount; c++) m.MaxJointStep = Mathf.Max(m.MaxJointStep, Mathf.Abs(Delta(ch[b][c], ch[a][c])));
            }

            // spikes: a frame that deviates from the mean of its neighbours while the neighbours agree with each other
            if (n >= 3)
                foreach (RigPart p in SpikeParts)
                    for (int i = loop ? 0 : 1; i < (loop ? n : n - 1); i++)
                    {
                        float prev = ch[(i + n - 1) % n][(int)p], next = ch[(i + 1) % n][(int)p];
                        float spike = Mathf.Abs(ch[i][(int)p] - 0.5f * (prev + next)) - 0.5f * Mathf.Abs(next - prev);
                        if (spike > m.MaxSpike) { m.MaxSpike = spike; m.MaxSpikeFrame = i; m.MaxSpikePart = p; }
                    }

            m.MaxStanceSlide = StanceSlide(m, rig, loop);
            m.VisibleMotion = VisibleMotionOf(rig, ch);
            return m;
        }

        /// <summary>Median over neck, near hand and far hand of the larger of the horizontal and vertical range (px) over the sequence, relative to the hip: pose only, no root translation.</summary>
        public static float VisibleMotionOf(RigDefinition rig, float[][] ch)
        {
            int n = ch.Length;
            if (n == 0) return 0f;
            RigJoint[] joints = { RigJoint.Neck, RigJoint.HandNear, RigJoint.HandFar };
            var lo = new Vector2[joints.Length]; var hi = new Vector2[joints.Length];
            for (int k = 0; k < joints.Length; k++) { lo[k] = new Vector2(float.MaxValue, float.MaxValue); hi[k] = new Vector2(float.MinValue, float.MinValue); }
            for (int i = 0; i < n; i++)
            {
                Vector2[] j = RigKinematics.JointPositions(rig, PoseChannels.ToPose(ch[i]), false, false);
                for (int k = 0; k < joints.Length; k++)
                {
                    Vector2 p = j[(int)joints[k]];
                    lo[k] = Vector2.Min(lo[k], p); hi[k] = Vector2.Max(hi[k], p);
                }
            }
            var ranges = new float[joints.Length];
            for (int k = 0; k < joints.Length; k++) ranges[k] = Mathf.Max(hi[k].x - lo[k].x, hi[k].y - lo[k].y);
            Array.Sort(ranges);
            return ranges[ranges.Length / 2];
        }

        // The planted foot (the lower one, resting on the ground line) should move at a steady speed; a foot that stutters, jumps forward or stops while it is
        // planted is "sliding". Returns the largest deviation from the steady speed of its stance, px per frame.
        private static float StanceSlide(PoseMetrics m, RigDefinition rig, bool loop)
        {
            int n = m.Frames;
            if (n < 3) return 0f;
            float ground = rig.groundY, worst = 0f;
            var runs = new List<List<float>>();
            List<float> current = null;
            int lastStance = -1;
            int steps = n - 1 + (loop ? 1 : 0);
            for (int s = 1; s <= steps; s++)
            {
                int a = s - 1, b = s % n;
                bool nearA = m.FootNear[a].y >= m.FootFar[a].y, nearB = m.FootNear[b].y >= m.FootFar[b].y;
                Vector2 pa = nearA ? m.FootNear[a] : m.FootFar[a];
                bool planted = nearA == nearB && Mathf.Abs(pa.y - ground) < 1.5f && Mathf.Abs((nearB ? m.FootNear[b] : m.FootFar[b]).y - ground) < 1.5f;
                if (!planted) { current = null; lastStance = -1; continue; }
                if (current == null || lastStance != (nearA ? 0 : 1)) { current = new List<float>(); runs.Add(current); }
                lastStance = nearA ? 0 : 1;
                current.Add((nearB ? m.FootNear[b] : m.FootFar[b]).x - pa.x);
            }
            foreach (var run in runs)
            {
                if (run.Count < 2) continue;
                var sorted = new List<float>(run); sorted.Sort();
                float steady = sorted[sorted.Count / 2];
                foreach (float v in run) worst = Mathf.Max(worst, Mathf.Abs(v - steady));
            }
            return worst;
        }

        private static float Delta(float a, float b) => a - b;

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"foot step max {MaxFootStep:0.0} px (frame {MaxFootStepFrame}), hip step max {MaxRootStep:0.0} px, hip height range {HipHeightRange:0.0} px, ")
              .Append($"leg extension min {MinLegExtension:P0} (frame {MinLegExtensionFrame}), stance slide max {MaxStanceSlide:0.0} px, ")
              .Append($"spike max {MaxSpike:0} deg ({MaxSpikePart}, frame {MaxSpikeFrame}), joint step max {MaxJointStep:0} deg, weapon gap {MaxWeaponGap:0.00} px, weapon below ground {Mathf.Max(0f, MaxWeaponBelowGround):0.0} px, visible upper-body motion {VisibleMotion:0.0} px");
            return sb.ToString();
        }
    }
}
