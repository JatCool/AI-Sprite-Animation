using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// One pose of the rig: a rotation per part (relative to its parent bone, radians) plus the root translation.
    /// Convention (screen, y down): positive = counter-clockwise. A limb hanging down swings FORWARD, a forward-pointing weapon tip goes UP,
    /// an upright torso leans BACK. Negative values are the opposite. Hand-authored, procedural or (in the future) AI-generated poses all end up in this shape.
    /// </summary>
    public sealed class RigPose
    {
        public readonly float[] angle = new float[RigDefinition.PartCount];
        public Vector2 root;     // intentional horizontal / vertical movement of the whole character, in sprite pixels (y down)
        public float hop;        // intentional lift off the ground in pixels (running, jumping); 0 = feet stay on the ground line

        public float this[RigPart p] { get => angle[(int)p]; set => angle[(int)p] = value; }
        public void SetDegrees(RigPart p, float degrees) => angle[(int)p] = degrees * Mathf.Deg2Rad;
    }

    /// <summary>
    /// Source of rig poses. The procedural implementation below ships with the package. A future "AI Pose + Rig" mode can implement this
    /// interface (e.g. poses estimated from an AnimateDiff/OpenPose generation) and reuse the rig renderer unchanged: the original pixels
    /// stay untouched, only the poses come from somewhere else.
    /// </summary>
    public interface IRigPoseProvider
    {
        /// <summary>Returns <paramref name="frameCount"/> poses. Loops sample a cycle of <paramref name="cycle"/> frames; one-shots include both endpoints.</summary>
        RigPose[] GetPoses(string animation, int frameCount, int cycle, float intensity);
    }

    /// <summary>Procedural Idle / Walk / Run / Attack poses.</summary>
    public sealed class ProceduralRigPoses : IRigPoseProvider
    {
        public static readonly ProceduralRigPoses Instance = new ProceduralRigPoses();

        public RigPose[] GetPoses(string animation, int frameCount, int cycle, float intensity)
        {
            var poses = new RigPose[frameCount];
            for (int i = 0; i < frameCount; i++) poses[i] = Evaluate(animation, i, cycle, intensity);
            return poses;
        }

        public static RigPose Evaluate(string animation, int t, int n, float k)
        {
            switch ((animation ?? "").ToLowerInvariant())
            {
                case "walk": return Locomotion(t, n, k, false);
                case "run": return Locomotion(t, n, k, true);
                case "idle": return Idle(t, n, k);
                case "attack": return Attack(t, n, k);
                default: return new RigPose();   // rest pose
            }
        }

        // ---- locomotion: alternating legs, counter-swinging arms, flexing knees and elbows ----
        private static RigPose Locomotion(int t, int n, float k, bool run)
        {
            float ph = 2f * Mathf.PI * t / n;
            float s = Mathf.Sin(ph), c = Mathf.Cos(ph);
            float thighAmp = (run ? 46f : 26f) * k, kneeFlex = (run ? 105f : 48f) * k, armAmp = (run ? 42f : 20f) * k, elbow = (run ? 88f : 22f) * k;
            float lean = (run ? 11f : 2f) * k;
            float hold = run ? 0.6f : 0.75f;   // how much of the arm motion the weapon cancels (keeps a held sword steadier)

            var p = new RigPose();
            float thighN = thighAmp * s, thighF = -thighAmp * s;
            float shinN = -(6f + kneeFlex * Mathf.Max(0f, c)), shinF = -(6f + kneeFlex * Mathf.Max(0f, -c));
            p.SetDegrees(RigPart.LegNearUpper, thighN); p.SetDegrees(RigPart.LegNearLower, shinN);
            p.SetDegrees(RigPart.LegFarUpper, thighF); p.SetDegrees(RigPart.LegFarLower, shinF);
            p.SetDegrees(RigPart.FootNear, -(thighN + shinN) * 0.75f);       // feet stay roughly flat
            p.SetDegrees(RigPart.FootFar, -(thighF + shinF) * 0.75f);

            float upN = -armAmp * s, upF = armAmp * s;
            float loN = elbow * (0.6f + 0.4f * -s), loF = elbow * (0.6f + 0.4f * s);
            p.SetDegrees(RigPart.ArmNearUpper, upN); p.SetDegrees(RigPart.ArmNearLower, loN);
            p.SetDegrees(RigPart.ArmFarUpper, upF); p.SetDegrees(RigPart.ArmFarLower, loF);
            p.SetDegrees(RigPart.Weapon, -(upN + loN) * hold);

            p.SetDegrees(RigPart.Body, -lean);   // forward lean = negative
            p.SetDegrees(RigPart.Head, lean * 0.6f + 1.2f * Mathf.Sin(2f * ph));
            p.SetDegrees(RigPart.Hair, -(run ? 16f : 8f) * k * Mathf.Cos(ph - 0.8f));
            if (run) p.hop = 2.2f * k * (0.5f + 0.5f * Mathf.Cos(2f * ph));
            return p;
        }

        // ---- idle: breathing, tiny weight shift, hair and weapon settle ----
        private static RigPose Idle(int t, int n, float k)
        {
            float ph = 2f * Mathf.PI * t / n;
            float b = Mathf.Sin(ph);
            var p = new RigPose();
            p.SetDegrees(RigPart.Body, 1.2f * k * b);
            p.SetDegrees(RigPart.Head, -0.8f * k * b);
            p.SetDegrees(RigPart.Hair, -4f * k * Mathf.Cos(ph - 0.5f));
            p.SetDegrees(RigPart.ArmNearUpper, (3f + 2.5f * b) * k); p.SetDegrees(RigPart.ArmNearLower, (8f + 2f * b) * k);
            p.SetDegrees(RigPart.ArmFarUpper, (-3f - 2f * b) * k); p.SetDegrees(RigPart.ArmFarLower, 6f * k);
            p.SetDegrees(RigPart.Weapon, -(3f + 8f) * 0.5f * k + 2.5f * k * b);
            p.SetDegrees(RigPart.LegNearUpper, 4f); p.SetDegrees(RigPart.LegNearLower, -3f); p.SetDegrees(RigPart.FootNear, -1f);
            p.SetDegrees(RigPart.LegFarUpper, -5f); p.SetDegrees(RigPart.LegFarLower, -3f); p.SetDegrees(RigPart.FootFar, 7f);
            return p;
        }

        // ---- attack: anticipation -> swing -> follow-through -> recovery (one-shot; both endpoints are sampled) ----
        // columns: u, body, head, hair, upperN, lowerN, weapon, upperF, lowerF, thighN, shinN, footN, thighF, shinF, footF, rootX
        private static readonly float[][] AttackKeys =
        {
            new[] { 0.00f,   0f,  0f,   0f,    5f,  10f,   0f,   -5f,   8f,    6f,  -4f,  -2f,   -6f,  -4f,   0f,  0f },
            new[] { 0.25f,   9f, -3f,  10f,  105f,  25f,  15f,   25f,  20f,   -8f, -10f,   0f,   10f,  -6f,  -2f, -1f },  // anticipation: lean back, sword raised
            new[] { 0.45f,  -8f,  3f,  -5f,   60f,  10f, -25f,  -10f,  10f,   20f, -14f,  -6f,  -14f,  -8f,   2f,  1.5f }, // swing: lean in, sword sweeps down
            new[] { 0.60f, -12f,  6f, -12f,   25f,   5f, -55f,  -18f,  12f,   26f, -10f, -12f,  -18f,  -6f,   4f,  2.5f }, // follow-through
            new[] { 0.80f,  -4f,  2f,  -4f,   15f,   8f, -20f,   -8f,   9f,   14f,  -6f,  -6f,  -10f,  -5f,   2f,  1f },   // recovery
            new[] { 1.00f,   0f,  0f,   0f,    5f,  10f,   0f,   -5f,   8f,    6f,  -4f,  -2f,   -6f,  -4f,   0f,  0f },
        };

        private static RigPose Attack(int t, int n, float k)
        {
            float u = n > 1 ? t / (float)(n - 1) : 0f;
            int a = 0;
            while (a < AttackKeys.Length - 2 && u > AttackKeys[a + 1][0]) a++;
            float[] ka = AttackKeys[a], kb = AttackKeys[a + 1];
            float f = Mathf.Clamp01((u - ka[0]) / (kb[0] - ka[0]));
            f = f * f * (3f - 2f * f);   // ease in/out
            float V(int i) => Mathf.Lerp(ka[i], kb[i], f);

            var p = new RigPose();
            p.SetDegrees(RigPart.Body, V(1) * k); p.SetDegrees(RigPart.Head, V(2) * k); p.SetDegrees(RigPart.Hair, V(3) * k);
            p.SetDegrees(RigPart.ArmNearUpper, V(4) * k); p.SetDegrees(RigPart.ArmNearLower, V(5) * k); p.SetDegrees(RigPart.Weapon, V(6) * k);
            p.SetDegrees(RigPart.ArmFarUpper, V(7) * k); p.SetDegrees(RigPart.ArmFarLower, V(8) * k);
            p.SetDegrees(RigPart.LegNearUpper, V(9) * k); p.SetDegrees(RigPart.LegNearLower, V(10) * k); p.SetDegrees(RigPart.FootNear, V(11) * k);
            p.SetDegrees(RigPart.LegFarUpper, V(12) * k); p.SetDegrees(RigPart.LegFarLower, V(13) * k); p.SetDegrees(RigPart.FootFar, V(14) * k);
            p.root = new Vector2(V(15) * k, 0f);
            return p;
        }
    }
}
