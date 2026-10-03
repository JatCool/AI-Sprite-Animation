using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>The motion found in one generated clip, ready to be judged against other clips.</summary>
    public sealed class SDPoseCandidate
    {
        public CycleExtraction Extraction;
        public RigPose[] VideoPoses;          // the whole clip mapped to the rig (before a cycle is cut out)
        public int Facing;                    // +1 right, -1 left (before mirroring), 0 unknown
        public int LimbSwaps;                 // left/right label swaps repaired
        public bool SwordArmSwapped;          // near and far arm were exchanged so that the sword arm behaves like a sword arm
        public float Coverage;                // fraction of the key joints detected with confidence
        public float ProfileRatio;            // shoulder separation / torso length: ~0 side view, ~1 front or back view
        public float VisibleMotion;           // idle: how far (px) the neck and hands move over the extracted loop, hip-relative
        public float Roughness;               // mean |second difference| of the joint angles over the clip (degrees): high = jittery or mislabelled detections
        public float Quality;                 // 0..1, higher is better; 0 = unusable
        public string Verdict = "";
    }

    /// <summary>
    /// Turns SDPose keypoints of a generated clip into rig motion and judges how good the clip is: is the person fully detected, seen from the side,
    /// and actually doing the requested movement (a walking swing that repeats, an attack with arm activity). Pure math.
    /// </summary>
    public static class SDPoseAnalysis
    {
        private static readonly int[] KeyJoints = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };

        /// <param name="facingLeft">The sprite faces left: the motion is mirrored so it matches the rig.</param>
        public static SDPoseCandidate Analyze(RigDefinition rig, List<OpenPoseFrame> keypoints, string kind, bool loop, int frames, bool facingLeft,
            OpenPoseMapperSettings settings = null)
        {
            var c = new SDPoseCandidate();
            if (keypoints.Count < 6) { c.Verdict = $"only {keypoints.Count} frames"; return c; }

            c.Facing = OpenPoseTracking.DetectFacing(keypoints);
            if (c.Facing < 0) OpenPoseTracking.Mirror(keypoints);              // normalise to a right-facing person
            c.LimbSwaps = OpenPoseTracking.FixLimbSwaps(keypoints);
            if (facingLeft) OpenPoseTracking.Mirror(keypoints);                // then match the sprite's direction

            c.Coverage = Coverage(keypoints);
            c.ProfileRatio = ProfileRatio(keypoints);
            c.VideoPoses = OpenPoseMapper.Map(rig, keypoints, kind, facingLeft, loop, settings);
            c.Extraction = MotionCycleExtractor.Extract(c.VideoPoses, frames, kind, loop, settings);

            if (!c.Extraction.Ok) { c.Verdict = c.Extraction.Notes; return c; }
            c.SwordArmSwapped = ChooseSwordArm(c.Extraction.Poses, kind, loop, settings);
            ApplyWeaponArmDamping(c.Extraction.Poses, kind, loop, settings);
            float cov = Mathf.Clamp01((c.Coverage - 0.5f) / 0.4f);
            float profile = Mathf.Clamp01((0.8f - c.ProfileRatio) / 0.45f);
            string k = (kind ?? "").ToLowerInvariant();
            float seam = 1f / (1f + c.Extraction.Closure / (loop ? 15f : 25f));
            float motion;
            if (k == "walk" || k == "run") motion = Mathf.Clamp01(c.Extraction.Swing / (k == "run" ? 110f : 85f)) * seam;
            else if (!loop) motion = Mathf.Clamp01(c.Extraction.Swing / 90f) * seam;
            else motion = seam;
            c.Roughness = Roughness(c.VideoPoses);
            if (k == "idle")
            {
                // an idle should be calm: prefer clips where the arms hang (a hand held to the face is a valid AI behaviour, but a poor idle)
                float arms = 0; foreach (var p in c.Extraction.Poses) arms += (Mathf.Abs(p[RigPart.ArmNearUpper]) + 0.5f * Mathf.Abs(p[RigPart.ArmNearLower]) + Mathf.Abs(p[RigPart.ArmFarUpper]) + 0.5f * Mathf.Abs(p[RigPart.ArmFarLower])) * 0.5f * Mathf.Rad2Deg;
                arms /= c.Extraction.Poses.Length;
                motion *= 1f / (1f + (arms / 60f) * (arms / 60f));
                // ...and it should live: a clip that hardly moves at all is a frozen idle (its sway would have to be amplified past usefulness)
                c.VisibleMotion = PoseMetrics.VisibleMotionOf(rig, PoseChannels.FromPoses(c.Extraction.Poses));
                motion *= 0.35f + 0.65f * Mathf.Clamp01(c.VisibleMotion / 0.8f);
            }
            float smooth = 1f / (1f + (c.Roughness / 40f) * (c.Roughness / 40f));
            c.Quality = cov * profile * motion * smooth;
            c.Verdict = $"coverage {c.Coverage:P0}, profile ratio {c.ProfileRatio:0.00}, roughness {c.Roughness:0}, swing {c.Extraction.Swing:0} deg, {(k == "idle" ? $"visible motion {c.VisibleMotion:0.0} px, " : "")}seam {c.Extraction.Closure:0.0} deg, facing {(c.Facing >= 0 ? "right" : "left")}, {c.LimbSwaps} limb swaps -> quality {c.Quality:0.00}{(c.SwordArmSwapped ? " (arms exchanged for the sword arm)" : "")}";
            return c;
        }

        private static readonly RigPart[] SmoothnessJoints =
        {
            RigPart.Body, RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.ArmFarUpper, RigPart.ArmFarLower,
            RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.LegFarUpper, RigPart.LegFarLower,
        };

        // A real movement is smooth from one video frame to the next; mislabelled or flickering detections make the angles jump.
        private static float Roughness(RigPose[] video)
        {
            float[][] ch = PoseChannels.FromPoses(video);
            PoseChannels.FillNonFinite(ch, false);
            double sum = 0; int cnt = 0;
            foreach (RigPart p in SmoothnessJoints)
                for (int t = 1; t < ch.Length - 1; t++)
                {
                    sum += Mathf.Abs(ch[t - 1][(int)p] - 2f * ch[t][(int)p] + ch[t + 1][(int)p]); cnt++;
                }
            return cnt > 0 ? (float)(sum / cnt) : 0f;
        }

        // The near arm of the rig holds the weapon. OpenPose labels the arms by appearance, which says nothing about which one holds a sword:
        // so the arm that suits the role is used. A sword arm stays calm during Idle/Walk/Run (and is the active one during an Attack).
        // Returns true if the arms were exchanged.
        private static bool ChooseSwordArm(RigPose[] poses, string kind, bool loop, OpenPoseMapperSettings settings)
        {
            float near = ArmActivity(poses, RigPart.ArmNearUpper, RigPart.ArmNearLower), far = ArmActivity(poses, RigPart.ArmFarUpper, RigPart.ArmFarLower);
            bool attack = string.Equals(kind, "attack", StringComparison.OrdinalIgnoreCase);
            bool swap = attack ? far > 1.25f * near : far < 0.8f * near;
            if (!swap) return false;
            foreach (var p in poses)
            {
                (p.angle[(int)RigPart.ArmNearUpper], p.angle[(int)RigPart.ArmFarUpper]) = (p.angle[(int)RigPart.ArmFarUpper], p.angle[(int)RigPart.ArmNearUpper]);
                (p.angle[(int)RigPart.ArmNearLower], p.angle[(int)RigPart.ArmFarLower]) = (p.angle[(int)RigPart.ArmFarLower], p.angle[(int)RigPart.ArmNearLower]);
            }
            OpenPoseMapper.DeriveSecondary(poses, kind, loop, settings);   // the weapon follows the (new) near forearm
            return true;
        }

        // The near arm carries the weapon: it moves less than a free arm (see OpenPoseMapperSettings.weaponArmWalk).
        private static void ApplyWeaponArmDamping(RigPose[] poses, string kind, bool loop, OpenPoseMapperSettings settings)
        {
            settings = settings ?? new OpenPoseMapperSettings();
            float k;
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "walk": k = settings.weaponArmWalk; break;
                case "run": k = settings.weaponArmRun; break;
                case "attack": k = settings.weaponArmAttack; break;
                default: k = settings.weaponArmIdle; break;
            }
            if (Mathf.Approximately(k, 1f)) return;
            foreach (var p in poses) { p.angle[(int)RigPart.ArmNearUpper] *= k; p.angle[(int)RigPart.ArmNearLower] *= k; }
            OpenPoseMapper.DeriveSecondary(poses, kind, loop, settings);
        }

        // Mean absolute angle plus range of the upper arm and half of the forearm: how much an arm is doing.
        private static float ArmActivity(RigPose[] poses, RigPart upper, RigPart lower)
        {
            float lo = float.MaxValue, hi = float.MinValue, sum = 0;
            foreach (var p in poses)
            {
                float u = p[upper] * Mathf.Rad2Deg, l = p[lower] * Mathf.Rad2Deg;
                lo = Mathf.Min(lo, u); hi = Mathf.Max(hi, u); sum += Mathf.Abs(u) + 0.5f * Mathf.Abs(l);
            }
            return (hi - lo) + sum / poses.Length;
        }

        private static float Coverage(IList<OpenPoseFrame> frames)
        {
            double sum = 0;
            foreach (var f in frames)
            {
                int ok = 0;
                foreach (int j in KeyJoints) if (f.Has(j, 0.3f)) ok++;
                sum += ok / (double)KeyJoints.Length;
            }
            return (float)(sum / frames.Count);
        }

        // Median over frames of |right shoulder x - left shoulder x| / |neck - mid hip|: a person in profile has both shoulders almost on top of each other.
        private static float ProfileRatio(IList<OpenPoseFrame> frames)
        {
            var ratios = new List<float>();
            foreach (var f in frames)
            {
                if (!f.Has(2, 0.3f) || !f.Has(5, 0.3f) || !f.Has(1, 0.3f) || !(f.Has(8, 0.3f) || f.Has(11, 0.3f))) continue;
                Vector2 hip = f.Has(8, 0.3f) && f.Has(11, 0.3f) ? (f.point[8] + f.point[11]) * 0.5f : (f.Has(8, 0.3f) ? f.point[8] : f.point[11]);
                float torso = Vector2.Distance(f.point[1], hip);
                if (torso < 1e-3f) continue;
                ratios.Add(Mathf.Abs(f.point[2].x - f.point[5].x) / torso);
            }
            if (ratios.Count == 0) return 1f;
            ratios.Sort();
            return ratios[ratios.Count / 2];
        }
    }
}
