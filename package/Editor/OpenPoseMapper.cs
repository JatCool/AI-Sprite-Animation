using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>Tuning of the OpenPose -> rig mapping.</summary>
    public sealed class OpenPoseMapperSettings
    {
        /// <summary>The side of the body facing the camera is OpenPose's right side (indices 2-4, 8-10), matching the rig's "near" limbs.</summary>
        public bool nearIsRight = true;
        public float minConfidence = 0.15f;
        /// <summary>How flat the feet stay while the leg moves (1 = world-flat, 0 = follow the shin).</summary>
        public float footFlatten = 0.75f;
        /// <summary>Share of the held weapon's orientation that follows the forearm (OpenPose has no hand orientation).</summary>
        public float weaponFollowIdle = 0.3f, weaponFollowWalk = 0.3f, weaponFollowRun = 0.45f, weaponFollowAttack = 0.9f;
        /// <summary>
        /// The character holds a weapon in its near hand, so while it walks, runs or stands, that arm moves less than the person in the AI video swings an arm
        /// (the rig's own procedural animations do the same). 1 = take the AI's arm as it is. Retargeting to the character, not a change of the AI motion's character.
        /// </summary>
        public float weaponArmIdle = 0.6f, weaponArmWalk = 0.6f, weaponArmRun = 0.8f, weaponArmAttack = 1f;
        /// <summary>Hair is not an OpenPose joint: it is secondary motion, a lagging follower of the head (0 = no hair movement).</summary>
        public float hairInfluence = 0.5f, hairLag = 0.5f;
    }

    /// <summary>
    /// The only place that knows how OpenPose body-18 skeletons relate to the rig. Converts keypoint sequences into <see cref="RigPose"/>s
    /// (bone rotations relative to the parent), so the rig and everything after it never depend on OpenPose.
    ///
    /// How it maps (side view, character facing +x after mirroring):
    ///   Body           = rotation of mid-hip -> neck against the rig's hip -> neck
    ///   Head           = neck -> face points (nose, eyes, ears), relative to the body; the sequence median is removed because OpenPose has no head-centre joint
    ///   Arm (upper)    = shoulder -> elbow, relative to the body        Arm (lower) = elbow -> wrist, relative to the upper arm
    ///   Leg (upper)    = hip -> knee (legs hang from the root)         Leg (lower) = knee -> ankle, relative to the thigh
    ///   Foot           = derived (kept roughly flat)                    Weapon = derived from the forearm (held, follows it by a factor)
    ///   Hair           = derived secondary motion (lagging follower of the head)
    ///   Root x / hop   = hip travel and ankle height, scaled from skeleton size to rig size
    /// Joints that are not detected become NaN; <see cref="PoseValidator"/> interpolates or rejects them.
    /// </summary>
    public static class OpenPoseMapper
    {
        private const int Nose = 0, Neck = 1, RShoulder = 2, RElbow = 3, RWrist = 4, LShoulder = 5, LElbow = 6, LWrist = 7,
            RHip = 8, RKnee = 9, RAnkle = 10, LHip = 11, LKnee = 12, LAnkle = 13;

        /// <summary>Angle that rotates <paramref name="rest"/> onto <paramref name="observed"/> in the rig's convention (counter-clockwise on screen, y down, positive).</summary>
        public static float RotationBetween(Vector2 rest, Vector2 observed)
        {
            return Mathf.Atan2(rest.y * observed.x - rest.x * observed.y, rest.x * observed.x + rest.y * observed.y);
        }

        public static RigPose[] Map(RigDefinition rig, IReadOnlyList<OpenPoseFrame> frames, string kind, bool facingLeft, bool loop, OpenPoseMapperSettings settings = null)
        {
            settings = settings ?? new OpenPoseMapperSettings();
            int n = frames.Count;
            var poses = new RigPose[n];
            for (int i = 0; i < n; i++) poses[i] = new RigPose();
            if (n == 0) return poses;

            // Rest directions from the rig, in a right-facing frame.
            Vector2 J(RigJoint j) { Vector2 v = rig.GetJoint(j); return facingLeft ? new Vector2(rig.width - v.x, v.y) : v; }
            Vector2 headCentre = RigKinematics.PartCentroid(rig, RigPart.Head);
            if (facingLeft) headCentre.x = rig.width - headCentre.x;
            Vector2 restBody = J(RigJoint.Neck) - J(RigJoint.Hip);
            Vector2 restHead = headCentre - J(RigJoint.Neck);
            Vector2 restArmNU = J(RigJoint.ElbowNear) - J(RigJoint.ShoulderNear), restArmNL = J(RigJoint.HandNear) - J(RigJoint.ElbowNear);
            Vector2 restArmFU = J(RigJoint.ElbowFar) - J(RigJoint.ShoulderFar), restArmFL = J(RigJoint.HandFar) - J(RigJoint.ElbowFar);
            Vector2 restLegNU = J(RigJoint.KneeNear) - J(RigJoint.Hip), restLegNL = J(RigJoint.FootNear) - J(RigJoint.KneeNear);
            Vector2 restLegFU = J(RigJoint.KneeFar) - J(RigJoint.Hip), restLegFL = J(RigJoint.FootFar) - J(RigJoint.KneeFar);

            int sN = settings.nearIsRight ? 0 : 1;   // near/far selector: 0 = right is near
            int shN = sN == 0 ? RShoulder : LShoulder, shF = sN == 0 ? LShoulder : RShoulder;
            int hipN = sN == 0 ? RHip : LHip, hipF = sN == 0 ? LHip : RHip;

            // Observed body size for scaling root movement.
            var bodyLengths = new List<float>();
            float mirror = facingLeft ? -1f : 1f;
            Vector2 K(OpenPoseFrame f, int i) => new Vector2(f.point[i].x * mirror, f.point[i].y);

            var worldBody = new float[n]; var headRelRaw = new float[n];
            var midHipX = new float[n]; var lowAnkle = new float[n];
            for (int i = 0; i < n; i++)
            {
                var f = frames[i]; var p = poses[i];
                float c = settings.minConfidence;
                bool hipsOk = f.Has(RHip, c) || f.Has(LHip, c);
                Vector2 midHip = hipsOk ? Average(f, c, K, RHip, LHip) : Vector2.zero;
                midHipX[i] = hipsOk ? midHip.x : float.NaN;

                // Body
                float wBody = float.NaN;
                if (f.Has(Neck, c) && hipsOk)
                {
                    Vector2 obs = K(f, Neck) - midHip;
                    wBody = RotationBetween(restBody, obs);
                    bodyLengths.Add(obs.magnitude);
                }
                worldBody[i] = wBody;
                p.angle[(int)RigPart.Body] = wBody;

                // Head (relative angle kept raw here; median removed below)
                headRelRaw[i] = float.NaN;
                if (f.Has(Neck, c))
                {
                    var facePoints = new List<Vector2>();
                    foreach (int idx in new[] { Nose, 14, 15, 16, 17 }) if (f.Has(idx, c)) facePoints.Add(K(f, idx));
                    if (facePoints.Count > 0)
                    {
                        Vector2 sum = Vector2.zero;
                        foreach (var v in facePoints) sum += v;
                        float wHead = RotationBetween(restHead, sum / facePoints.Count - K(f, Neck));
                        headRelRaw[i] = wHead - (float.IsNaN(wBody) ? 0f : wBody);
                    }
                }

                // Arms and legs: world angle per segment, then relative to the parent segment
                float wNU = Seg(f, shN, sN == 0 ? RElbow : LElbow, restArmNU, K, c), wNL = Seg(f, sN == 0 ? RElbow : LElbow, sN == 0 ? RWrist : LWrist, restArmNL, K, c);
                float wFU = Seg(f, shF, sN == 0 ? LElbow : RElbow, restArmFU, K, c), wFL = Seg(f, sN == 0 ? LElbow : RElbow, sN == 0 ? LWrist : RWrist, restArmFL, K, c);
                float bodyRef = float.IsNaN(wBody) ? 0f : wBody;
                p.angle[(int)RigPart.ArmNearUpper] = wNU - bodyRef; p.angle[(int)RigPart.ArmNearLower] = wNL - wNU;
                p.angle[(int)RigPart.ArmFarUpper] = wFU - bodyRef; p.angle[(int)RigPart.ArmFarLower] = wFL - wFU;

                float lNU = SegFrom(f, hipN, hipF, midHip, hipsOk, sN == 0 ? RKnee : LKnee, restLegNU, K, c), lNL = Seg(f, sN == 0 ? RKnee : LKnee, sN == 0 ? RAnkle : LAnkle, restLegNL, K, c);
                float lFU = SegFrom(f, hipF, hipN, midHip, hipsOk, sN == 0 ? LKnee : RKnee, restLegFU, K, c), lFL = Seg(f, sN == 0 ? LKnee : RKnee, sN == 0 ? LAnkle : RAnkle, restLegFL, K, c);
                p.angle[(int)RigPart.LegNearUpper] = lNU; p.angle[(int)RigPart.LegNearLower] = lNL - lNU;
                p.angle[(int)RigPart.LegFarUpper] = lFU; p.angle[(int)RigPart.LegFarLower] = lFL - lFU;

                float low = float.NaN;
                if (f.Has(RAnkle, c)) low = f.point[RAnkle].y;
                if (f.Has(LAnkle, c)) low = float.IsNaN(low) ? f.point[LAnkle].y : Mathf.Max(low, f.point[LAnkle].y);
                lowAnkle[i] = low;
            }

            // Head: remove the (constant) face-point bias by centring the head angle on its median.
            float headMedian = Median(headRelRaw);
            for (int i = 0; i < n; i++)
                poses[i].angle[(int)RigPart.Head] = float.IsNaN(headRelRaw[i]) ? float.NaN : headRelRaw[i] - (float.IsNaN(headMedian) ? 0f : headMedian);

            // Root x and flight phase, scaled from skeleton size to rig size. A looping animation plays in place: the walking drift
            // (a linear trend of the hip position over the clip) is removed, what remains is the sway.
            float obsLen = Median(bodyLengths.ToArray());
            float scale = obsLen > 1e-3f ? restBody.magnitude / obsLen : 1f;
            float[] hipX = (float[])midHipX.Clone();
            if (loop) RemoveLinearTrend(hipX);
            float xRef = loop ? Mean(hipX) : FirstFinite(hipX);
            float ground = float.MinValue;
            foreach (float y in lowAnkle) if (!float.IsNaN(y)) ground = Mathf.Max(ground, y);
            bool airborne = PoseLimits.AllowsAirborne(kind);
            for (int i = 0; i < n; i++)
            {
                poses[i].root = new Vector2(float.IsNaN(hipX[i]) ? float.NaN : (hipX[i] - xRef) * scale, 0f);
                poses[i].hop = airborne && !float.IsNaN(lowAnkle[i]) && ground > float.MinValue ? Mathf.Max(0f, (ground - lowAnkle[i]) * scale) : 0f;
            }

            DeriveSecondary(poses, kind, loop, settings);
            return poses;
        }

        /// <summary>
        /// Fills the joints OpenPose cannot see from the ones it can: the feet stay roughly flat, the held weapon follows the near forearm, the hair lags behind the head.
        /// Call it again after poses were rearranged (cycle extraction swaps the near and far limbs, for example), so the derived joints always match their sources.
        /// </summary>
        public static void DeriveSecondary(IList<RigPose> poses, string kind, bool loop, OpenPoseMapperSettings settings = null)
        {
            settings = settings ?? new OpenPoseMapperSettings();
            int n = poses.Count;
            float follow = WeaponFollow(kind, settings);
            for (int i = 0; i < n; i++)
            {
                RigPose p = poses[i];
                p.angle[(int)RigPart.FootNear] = -(p.angle[(int)RigPart.LegNearUpper] + p.angle[(int)RigPart.LegNearLower]) * settings.footFlatten;
                p.angle[(int)RigPart.FootFar] = -(p.angle[(int)RigPart.LegFarUpper] + p.angle[(int)RigPart.LegFarLower]) * settings.footFlatten;
                float body = float.IsNaN(p.angle[(int)RigPart.Body]) ? 0f : p.angle[(int)RigPart.Body];
                float forearmWorld = body + p.angle[(int)RigPart.ArmNearUpper] + p.angle[(int)RigPart.ArmNearLower];
                p.angle[(int)RigPart.Weapon] = (follow - 1f) * forearmWorld;
            }

            // Hair: lagging follower of the head's world rotation (secondary motion).
            if (settings.hairInfluence > 0f)
            {
                float state = 0f;
                int passes = loop ? 2 : 1;
                var hair = new float[n];
                for (int pass = 0; pass < passes; pass++)
                    for (int i = 0; i < n; i++)
                    {
                        float body = float.IsNaN(poses[i].angle[(int)RigPart.Body]) ? 0f : poses[i].angle[(int)RigPart.Body];
                        float headWorld = poses[i].angle[(int)RigPart.Head] + body;
                        if (float.IsNaN(headWorld)) headWorld = 0f;
                        float target = -settings.hairInfluence * headWorld;
                        state = Mathf.Lerp(target, state, settings.hairLag);
                        hair[i] = state;
                    }
                for (int i = 0; i < n; i++) poses[i].angle[(int)RigPart.Hair] = hair[i];
            }
        }

        // Least-squares line through the finite samples, subtracted from them (NaN stays NaN).
        private static void RemoveLinearTrend(float[] v)
        {
            double sx = 0, sy = 0, sxx = 0, sxy = 0; int cnt = 0;
            for (int i = 0; i < v.Length; i++)
            {
                if (float.IsNaN(v[i])) continue;
                sx += i; sy += v[i]; sxx += (double)i * i; sxy += (double)i * v[i]; cnt++;
            }
            if (cnt < 3) return;
            double den = cnt * sxx - sx * sx;
            if (Math.Abs(den) < 1e-9) return;
            double slope = (cnt * sxy - sx * sy) / den, icpt = (sy - slope * sx) / cnt;
            for (int i = 0; i < v.Length; i++) if (!float.IsNaN(v[i])) v[i] = (float)(v[i] - (slope * i + icpt));
        }

        /// <summary>
        /// The OpenPose skeleton a rig pose would produce (right-facing, sprite pixels, y down). Used to draw the ControlNet guidance image from the
        /// character's own proportions, and to test the mapper (pose -> skeleton -> pose must round-trip).
        /// </summary>
        public static OpenPoseFrame Synthesize(RigDefinition rig, RigPose pose, bool nearIsRight = true)
        {
            Vector2[] j = RigKinematics.JointPositions(rig, pose, false, true);
            var world = RigKinematics.WorldMatrices(rig, pose);
            Vector2 root = RigKinematics.RootOffset(rig, pose, world);
            Vector2 headCentre = world[(int)RigPart.Head].Apply(RigKinematics.PartCentroid(rig, RigPart.Head).x, RigKinematics.PartCentroid(rig, RigPart.Head).y) + root;

            var f = new OpenPoseFrame();
            void Set(int i, Vector2 v) { f.point[i] = v; f.confidence[i] = 1f; }
            Set(Neck, j[(int)RigJoint.Neck]);

            // Face points (nose, eyes, ears) around the head centre, turned with the head, so ControlNet sees a face looking forward.
            Vector2 hc = RigKinematics.PartCentroid(rig, RigPart.Head);
            float headW = 0f, headH = 0f;
            {
                int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
                for (int y = 0; y < rig.height; y++)
                    for (int x = 0; x < rig.width; x++)
                        if (rig.HasPart(x, y, RigPart.Head)) { x0 = Mathf.Min(x0, x); x1 = Mathf.Max(x1, x); y0 = Mathf.Min(y0, y); y1 = Mathf.Max(y1, y); }
                headW = x1 >= x0 ? x1 - x0 + 1 : 8f; headH = y1 >= y0 ? y1 - y0 + 1 : 8f;
            }
            float ha = pose.angle[(int)RigPart.Body] + pose.angle[(int)RigPart.Head], cs = Mathf.Cos(ha), sn = Mathf.Sin(ha);
            Vector2 Face(float ox, float oy)
            {
                float fx = ox * headW, fy = oy * headH;
                return headCentre + new Vector2(cs * fx + sn * fy, -sn * fx + cs * fy);
            }
            Set(Nose, Face(0.42f, 0.10f)); Set(14, Face(0.20f, -0.10f)); Set(15, Face(0.02f, -0.10f)); Set(16, Face(-0.14f, 0f)); Set(17, Face(-0.22f, 0f));
            int shN = nearIsRight ? RShoulder : LShoulder, shF = nearIsRight ? LShoulder : RShoulder;
            Set(shN, j[(int)RigJoint.ShoulderNear]); Set(shN + 1, j[(int)RigJoint.ElbowNear]); Set(shN + 2, j[(int)RigJoint.HandNear]);
            Set(shF, j[(int)RigJoint.ShoulderFar]); Set(shF + 1, j[(int)RigJoint.ElbowFar]); Set(shF + 2, j[(int)RigJoint.HandFar]);
            int hN = nearIsRight ? RHip : LHip, hF = nearIsRight ? LHip : RHip;
            Set(hN, j[(int)RigJoint.Hip]); Set(hN + 1, j[(int)RigJoint.KneeNear]); Set(hN + 2, j[(int)RigJoint.FootNear]);
            Set(hF, j[(int)RigJoint.Hip]); Set(hF + 1, j[(int)RigJoint.KneeFar]); Set(hF + 2, j[(int)RigJoint.FootFar]);
            return f;
        }

        // ---- helpers
        private static Vector2 Average(OpenPoseFrame f, float c, Func<OpenPoseFrame, int, Vector2> K, int a, int b)
        {
            Vector2 sum = Vector2.zero; int cnt = 0;
            if (f.Has(a, c)) { sum += K(f, a); cnt++; }
            if (f.Has(b, c)) { sum += K(f, b); cnt++; }
            return cnt == 0 ? Vector2.zero : sum / cnt;
        }

        private static float Seg(OpenPoseFrame f, int from, int to, Vector2 rest, Func<OpenPoseFrame, int, Vector2> K, float c)
            => f.Has(from, c) && f.Has(to, c) ? RotationBetween(rest, K(f, to) - K(f, from)) : float.NaN;

        // Hip -> knee: the two hips of a side view coincide, so the shared mid-hip is the stable origin.
        private static float SegFrom(OpenPoseFrame f, int ownHip, int otherHip, Vector2 midHip, bool midOk, int knee, Vector2 rest, Func<OpenPoseFrame, int, Vector2> K, float c)
            => midOk && f.Has(knee, c) ? RotationBetween(rest, K(f, knee) - midHip) : float.NaN;

        internal static float WeaponFollow(string kind, OpenPoseMapperSettings s)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "attack": return s.weaponFollowAttack;
                case "run": return s.weaponFollowRun;
                case "walk": return s.weaponFollowWalk;
                default: return s.weaponFollowIdle;
            }
        }

        private static float Median(float[] values)
        {
            var list = new List<float>();
            foreach (float v in values) if (!float.IsNaN(v)) list.Add(v);
            if (list.Count == 0) return float.NaN;
            list.Sort();
            return list[list.Count / 2];
        }

        private static float Mean(float[] values)
        {
            double sum = 0; int cnt = 0;
            foreach (float v in values) if (!float.IsNaN(v)) { sum += v; cnt++; }
            return cnt == 0 ? 0f : (float)(sum / cnt);
        }

        private static float FirstFinite(float[] values)
        {
            foreach (float v in values) if (!float.IsNaN(v)) return v;
            return 0f;
        }
    }
}
