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
        /// <summary>Whole-pixel translation of a part relative to its parent (y down), applied before its rotation; children follow. A shifted part moves with
        /// its pixels unchanged (no re-sampling), the way pixel artists animate an idle; a shifted Body or Head stretches the seam it leaves (see SpriteRig).</summary>
        public readonly Vector2Int[] shift = new Vector2Int[RigDefinition.PartCount];

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

    /// <summary>Procedural Idle / Walk / Run / Attack / Jump / Sit / Crouch / CrouchWalk poses.</summary>
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
                case "jump": return Jump(t, n, k);
                case "fall": return Fall(t, n, k);
                case "sit": return Sit(t, n, k);
                case "crouch": return Crouch(t, n, k);
                case "crouchwalk": return CrouchWalk(t, n, k);
                case "wakeup": return WakeUp(t, n);
                case "ignite": return FromWorldKeys(IgniteKeys, t, n, true);
                case "lowerhand": return FromWorldKeys(LowerHandKeys, t, n, false, true);
                case "dash": return Dash(t, n);
                case "hurt": return FromWorldKeys(HurtKeys, t, n, false, true);
                case "leanoncolumn": return LeanOnColumn(t, n);
                case "reachcolumn": return FromWorldKeys(ReachColumnKeys, t, n, false, true);
                case "climbdown": return ClimbDown(t, n);
                case "staggershock": return FromWorldKeys(StaggerShockKeys, t, n, false, true);
                case "spit": return FromWorldKeys(SpitKeys, t, n, false, true);
                case "rearup": return FromWorldKeys(RearUpKeys, t, n, false, true);
                case "trot": return Trot(t, n, k);
                case "recoil": return FromWorldKeys(RecoilKeys, t, n, false, true);
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

        // ---- trot: the walk of a side-view quadruped rigged with its front legs as the arms and its hind legs as the legs (the hip at the rear) ----
        // The legs move in diagonal pairs (near front with far hind, far front with near hind), half a cycle apart. A pair on the ground sweeps back with
        // straight legs, so its paws stay on the ground line (the renderer pins the lowest hind paw; a front paw on a straight leg rises less than a pixel at
        // these angles); the lifted pair bends at the wrist and the hock and swings forward. Small angles on purpose: leg pixels cut from the belly leave holes
        // when they swing far. The head nods twice a cycle; the torso does not move (the tail is part of it).
        private static RigPose Trot(int t, int n, float k)
        {
            float ph = 2f * Mathf.PI * t / n;
            var p = new RigPose();
            void Pair(float phase, RigPart frontUpper, RigPart frontLower, RigPart hindUpper, RigPart hindLower, RigPart hindFoot)
            {
                float swing = Mathf.Sin(phase), lift = Mathf.Pow(Mathf.Max(0f, Mathf.Cos(phase)), 1.5f);   // lifted while it swings forward
                float front = 12f * k * swing, hind = 12f * k * swing;
                p.SetDegrees(frontUpper, front);
                p.SetDegrees(frontLower, -40f * k * lift);                       // the wrist folds the paw back and up
                p.SetDegrees(hindUpper, hind);
                p.SetDegrees(hindLower, -34f * k * lift);                        // the hock folds
                p.SetDegrees(hindFoot, -(hind - 34f * k * lift) * 0.8f);         // the paw stays roughly flat
            }
            Pair(ph, RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.LegFarUpper, RigPart.LegFarLower, RigPart.FootFar);
            Pair(ph + Mathf.PI, RigPart.ArmFarUpper, RigPart.ArmFarLower, RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.FootNear);
            p.SetDegrees(RigPart.Head, 2f * k * Mathf.Sin(2f * ph));
            return p;
        }

        // ---- idle (version 4): one breath per loop, the way pixel artists draw an idle (modelled on a hand-made 12-frame reference at 100 ms; 24 @ 20 by
        // default): the upper body RISES by one whole pixel and nothing but the hair is rotated, so every pixel stays as drawn. Overlapping action as a ripple:
        // on the way up the head leads, the chest follows, the hands come last; on the way down the head goes first, then the chest, the hands lag. The hair
        // sways continuously and flicks behind every head move (the part that profits from a higher frame rate). The legs never move. Frame 0 is the rest
        // pose (other clips blend into it). Whole-pixel moves are RigPose.shift (a shifted Body or Head stretches its seam, see SpriteRig). Keys are fractions
        // of the cycle, so any frame count samples the same timing.
        // v1 turned the torso ~1 degree (jitter); v2 dipped the upper body through the knees as one block (mechanical); v3 was this at 12 @ 10 with a stepped hair.
        private const float IdleHeadUp = 0.30f, IdleChestUp = 0.36f, IdleHandsUp = 0.42f, IdleHeadDown = 0.70f, IdleChestDown = 0.74f, IdleHandsDown = 0.80f;

        private static RigPose Idle(int t, int n, float k)
        {
            float u = n > 0 ? (t % n) / (float)n : 0f;
            bool head = u >= IdleHeadUp && u < IdleHeadDown, chest = u >= IdleChestUp && u < IdleChestDown, hands = u >= IdleHandsUp && u < IdleHandsDown;
            var p = new RigPose();
            int headY = head ? -1 : 0, chestY = chest ? -1 : 0, handsY = hands ? -1 : 0;
            p.shift[(int)RigPart.Body] = new Vector2Int(0, chestY);
            p.shift[(int)RigPart.Head] = new Vector2Int(0, headY - chestY);                      // relative to the chest it rides on
            p.shift[(int)RigPart.ArmNearUpper] = p.shift[(int)RigPart.ArmFarUpper] = new Vector2Int(0, handsY - chestY);
            // the ponytail: a slow sway plus a flick that trails each head move and settles
            float Flick(float since) => since < 0f ? 0f : Mathf.Exp(-since / 0.07f) * Mathf.Sin(since / 0.07f * 1.6f + 1.2f);
            float hair = 5f * Mathf.Sin(2f * Mathf.PI * u) + 7f * Flick(u - IdleHeadUp) - 7f * Flick(u - IdleHeadDown);   // a ~10 px ponytail needs ~6 degrees per pixel at its tip
            p.SetDegrees(RigPart.Hair, hair * k);
            p.SetDegrees(RigPart.ArmNearUpper, 3f * k); p.SetDegrees(RigPart.ArmNearLower, 8f * k);
            p.SetDegrees(RigPart.ArmFarUpper, -3f * k); p.SetDegrees(RigPart.ArmFarLower, 6f * k);
            p.SetDegrees(RigPart.Weapon, -(3f + 8f) * 0.5f * k);
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

        // ---- jump: crouch -> launch -> rise -> apex tuck -> fall -> landing crouch -> recover (one-shot; both endpoints are sampled) ----
        // The clip carries NO travel height: a game moves the character itself (rigidbody), so the clip only changes the pose. `hop` merely keeps the hips at standing
        // height while the tucked legs shorten (the renderer otherwise pins the lowest foot to the ground line); the crouches sink the body, which is wanted.
        // columns: u, body, head, hair, upperN, lowerN, weapon, upperF, lowerF, thighN, shinN, footN, thighF, shinF, footF, hop
        // key times sit on the frame grid of the default 10-frame clip (frame i = i/9) so every key pose is rendered; other frame counts interpolate between them
        private static readonly float[][] JumpKeys =
        {
            new[] { 0.00f,   0f,  0f,   0f,    5f,  10f,   0f,   -5f,   8f,    6f,  -4f,  -2f,   -6f,  -4f,   0f, 0f },
            new[] { 2f/9f, -14f,  6f,  -6f,  -35f,  20f,  10f,  -40f,  20f,   58f, -98f,  28f,   50f, -88f,  24f, 0f },   // anticipation: crouch, arms swing back
            new[] { 3f/9f,   4f, -2f,   4f,   80f,  10f, -62f,   70f,  10f,    4f,  -2f, -25f,   -2f,  -2f, -20f, 0f }, // launch: legs push off, arms thrown up
            new[] { 4f/9f,   2f,  0f,   8f,   85f,  25f, -75f,   75f,  20f,   35f, -60f,   5f,   25f, -50f,   5f, 0f },    // rise: near knee comes up
            new[] { 5f/9f,  -4f,  2f,  12f,   75f,  30f, -70f,   65f,  25f,   55f, -95f,  15f,   42f, -80f,  12f, 1f },    // apex: tucked
            new[] { 6f/9f,  -2f,  1f,  13f,   65f,  32f, -62f,   58f,  28f,   38f, -72f,  12f,   32f, -62f,  10f, 1f },  // apex hold: still tucked, starting to open
            new[] { 7f/9f,   0f,  0f,  14f,   55f,  35f, -55f,   50f,  30f,   20f, -25f,  15f,   14f, -25f,  10f, 0f },  // fall: legs reach down, arms out
            new[] { 8f/9f, -10f,  4f,  -8f,   20f,  30f, -25f,    5f,  20f,   56f, -94f,  26f,   48f, -84f,  22f, 0f },    // landing: crouch absorbs
            new[] { 1.00f,   0f,  0f,   0f,    5f,  10f,   0f,   -5f,   8f,    6f,  -4f,  -2f,   -6f,  -4f,   0f, 0f },
        };

        private static RigPose Jump(int t, int n, float k)
        {
            float u = n > 1 ? t / (float)(n - 1) : 0f;
            int a = 0;
            while (a < JumpKeys.Length - 2 && u > JumpKeys[a + 1][0]) a++;
            float[] ka = JumpKeys[a], kb = JumpKeys[a + 1];
            float f = Mathf.Clamp01((u - ka[0]) / (kb[0] - ka[0]));
            f = f * f * (3f - 2f * f);   // ease in/out
            float V(int i) => Mathf.Lerp(ka[i], kb[i], f);

            var p = new RigPose();
            p.SetDegrees(RigPart.Body, V(1) * k); p.SetDegrees(RigPart.Head, V(2) * k); p.SetDegrees(RigPart.Hair, V(3) * k);
            p.SetDegrees(RigPart.ArmNearUpper, V(4) * k); p.SetDegrees(RigPart.ArmNearLower, V(5) * k); p.SetDegrees(RigPart.Weapon, V(6) * k);
            p.SetDegrees(RigPart.ArmFarUpper, V(7) * k); p.SetDegrees(RigPart.ArmFarLower, V(8) * k);
            p.SetDegrees(RigPart.LegNearUpper, V(9) * k); p.SetDegrees(RigPart.LegNearLower, V(10) * k); p.SetDegrees(RigPart.FootNear, V(11) * k);
            p.SetDegrees(RigPart.LegFarUpper, V(12) * k); p.SetDegrees(RigPart.LegFarLower, V(13) * k); p.SetDegrees(RigPart.FootFar, V(14) * k);
            p.hop = V(15) * k;
            return p;
        }

        // ---- fall (loop): held while the character drops: the arms thrown up for balance, the legs apart,
        // the hair streams upward; a slow flutter (arms, hair, legs out of phase) keeps it alive. No hop: the game moves the body, the feet stay on the pivot's row.
        private static RigPose Fall(int t, int n, float k)
        {
            float ph = 2f * Mathf.PI * t / Mathf.Max(1, n);
            float s = Mathf.Sin(ph), c = Mathf.Sin(ph + 1.2f);
            var p = new RigPose();
            p.SetDegrees(RigPart.Body, (-3f + 1.5f * s) * k);
            p.SetDegrees(RigPart.Head, (2f - 1.5f * s) * k);   // keeps the face nearly level while the torso sways
            p.SetDegrees(RigPart.Hair, (18f + 6f * c) * k);
            // the arms thrown up above the shoulders for balance, flailing a little out of phase
            p.SetDegrees(RigPart.ArmNearUpper, (138f + 10f * s) * k);
            p.SetDegrees(RigPart.ArmNearLower, (22f + 8f * c) * k);
            p.SetDegrees(RigPart.Weapon, (-30f - 6f * s) * k);
            p.SetDegrees(RigPart.ArmFarUpper, (118f + 10f * c) * k);
            p.SetDegrees(RigPart.ArmFarLower, (18f + 6f * s) * k);
            // the legs apart: the near one forward with the knee bent, the far one trailing back
            p.SetDegrees(RigPart.LegNearUpper, (30f + 5f * s) * k);
            p.SetDegrees(RigPart.LegNearLower, (-42f - 5f * s) * k);
            p.SetDegrees(RigPart.FootNear, 16f * k);
            p.SetDegrees(RigPart.LegFarUpper, (-12f - 5f * s) * k);
            p.SetDegrees(RigPart.LegFarLower, (-26f + 5f * s) * k);
            p.SetDegrees(RigPart.FootFar, 12f * k);
            return p;
        }

        // ---- sit: a small dip, a squat, then the character drops onto the ground with the knees up, torso upright, and holds it (one-shot; the last frame is the held pose) ----
        // The clip changes the pose only: the game shrinks the collider and keeps the feet on the ground, and the renderer pins the lowest foot to the ground line,
        // so the thighs rising forward with the shins hanging down lowers the hips to the ground by themselves. Standing up again is the Animator going back to Idle.
        // columns: u, body, head, hair, upperN, lowerN, weapon, upperF, lowerF, thighN, shinN, footN, thighF, shinF, footF, hop
        // key times sit on the frame grid of the default 8-frame clip (frame i = i/7); other frame counts interpolate between them
        private static readonly float[][] SitKeys =
        {
            new[] { 0.00f,   0f,  0f,   0f,    5f,  10f,   0f,   -5f,   8f,    6f,  -4f,  -2f,   -6f,  -4f,   0f, 0f },
            new[] { 1f/7f,  -4f,  2f,   3f,  -10f,  14f,   0f,  -12f,  14f,   22f, -34f,  12f,   18f, -30f,  10f, 0f },   // anticipation: a small dip, arms swing back
            new[] { 2f/7f, -10f,  4f,  -3f,   14f,  26f, -22f,   12f,  24f,   56f, -92f,  26f,   52f, -86f,  24f, 0f },   // squat
            new[] { 4f/7f,  -7f,  2f,   8f,   34f,  30f, -38f,   28f,  28f,  100f,-110f,  14f,   92f,-102f,  12f, 0f },   // dropping: knees come up, hips go down
            new[] { 6f/7f,   3f, -2f, -10f,   46f,  34f, -48f,   40f,  32f,  122f,-122f,   4f,  112f,-112f,   2f, 0f },   // impact: slight lean back, knees at their highest
            new[] { 1.00f,  -3f,  1f,  -4f,   52f,  22f, -44f,   46f,  20f,  116f,-116f,   2f,  106f,-106f,   0f, 0f },   // settled: upright, forearms resting on the knees (the held pose)
        };

        private static RigPose Sit(int t, int n, float k)
        {
            float u = n > 1 ? t / (float)(n - 1) : 0f;
            int a = 0;
            while (a < SitKeys.Length - 2 && u > SitKeys[a + 1][0]) a++;
            float[] ka = SitKeys[a], kb = SitKeys[a + 1];
            float f = Mathf.Clamp01((u - ka[0]) / (kb[0] - ka[0]));
            f = f * f * (3f - 2f * f);   // ease in/out
            float V(int i) => Mathf.Lerp(ka[i], kb[i], f);

            var p = new RigPose();
            p.SetDegrees(RigPart.Body, V(1) * k); p.SetDegrees(RigPart.Head, V(2) * k); p.SetDegrees(RigPart.Hair, V(3) * k);
            p.SetDegrees(RigPart.ArmNearUpper, V(4) * k); p.SetDegrees(RigPart.ArmNearLower, V(5) * k); p.SetDegrees(RigPart.Weapon, V(6) * k);
            p.SetDegrees(RigPart.ArmFarUpper, V(7) * k); p.SetDegrees(RigPart.ArmFarLower, V(8) * k);
            p.SetDegrees(RigPart.LegNearUpper, V(9) * k); p.SetDegrees(RigPart.LegNearLower, V(10) * k); p.SetDegrees(RigPart.FootNear, V(11) * k);
            p.SetDegrees(RigPart.LegFarUpper, V(12) * k); p.SetDegrees(RigPart.LegFarLower, V(13) * k); p.SetDegrees(RigPart.FootFar, V(14) * k);
            p.hop = V(15) * k;
            return p;
        }

        // ---- crouch (goose step): the hips drop to about knee height (not onto the heels), the knees are tucked forward, the torso leans in and the head stays forward and low ----
        // One-shot, the last frame is the held pose. Meant for passing narrow gaps or ducking under something flying at the character.
        // The torso rotates about the hips and the head is its child: Head is always exactly -Body, so the head's net rotation is 0 and the face is not re-sampled
        // (a tilted head distorts the face at this pixel size), while it still travels forward and down with the lean. The arms are children of the torso too: their
        // values are the wanted world angles minus the lean. The legs are not children of the torso. The player's cloak tail follows the near thigh (rig mapping).
        // The renderer pins the lowest foot to the ground line, so the folded legs lower the hips by themselves; the feet end flat (thigh + shin + foot = 0).
        // columns: u, body, head, hair, upperN, lowerN, weapon, upperF, lowerF, thighN, shinN, footN, thighF, shinF, footF, hop
        // key times sit on the frame grid of the default 6-frame clip (frame i = i/5); other frame counts interpolate between them
        private static readonly float[][] CrouchKeys =
        {
            new[] { 0.00f,   0f,  0f,   0f,    5f,  10f,   0f,   -5f,   8f,    6f,  -4f,  -2f,   -6f,  -4f,   0f, 0f },
            new[] { 1f/5f,  -8f,  8f,   0f,   12f,  16f,   2f,    8f,  14f,   40f, -60f,  22f,   34f, -52f,  20f, 0f },   // quick dip, torso starts to lean
            new[] { 2f/5f, -20f, 20f,   0f,   28f,  22f,  -4f,   24f,  18f,   76f,-108f,  32f,   70f,-100f,  30f, 0f },
            new[] { 3f/5f, -34f, 34f,   4f,   46f,  26f, -10f,   36f,  22f,  100f,-138f,  38f,   94f,-130f,  36f, 0f },   // bottom (slightly below the held pose)
            new[] { 4f/5f, -32f, 32f,   1f,   42f,  25f,  -8f,   32f,  20f,   96f,-132f,  36f,   90f,-126f,  34f, 0f },   // settle
            new[] { 1.00f, -32f, 32f,   0f,   42f,  25f,  -8f,   32f,  20f,   96f,-132f,  36f,   90f,-126f,  34f, 0f },   // held pose: arms hang in front of the knees (world 10 deg, forearm 35), sword low
        };

        private static RigPose Crouch(int t, int n, float k)
        {
            float u = n > 1 ? t / (float)(n - 1) : 0f;
            int a = 0;
            while (a < CrouchKeys.Length - 2 && u > CrouchKeys[a + 1][0]) a++;
            float[] ka = CrouchKeys[a], kb = CrouchKeys[a + 1];
            float f = Mathf.Clamp01((u - ka[0]) / (kb[0] - ka[0]));
            f = f * f * (3f - 2f * f);   // ease in/out
            float V(int i) => Mathf.Lerp(ka[i], kb[i], f);

            var p = new RigPose();
            p.SetDegrees(RigPart.Body, V(1) * k); p.SetDegrees(RigPart.Head, V(2) * k); p.SetDegrees(RigPart.Hair, V(3) * k);
            p.SetDegrees(RigPart.ArmNearUpper, V(4) * k); p.SetDegrees(RigPart.ArmNearLower, V(5) * k); p.SetDegrees(RigPart.Weapon, V(6) * k);
            p.SetDegrees(RigPart.ArmFarUpper, V(7) * k); p.SetDegrees(RigPart.ArmFarLower, V(8) * k);
            p.SetDegrees(RigPart.LegNearUpper, V(9) * k); p.SetDegrees(RigPart.LegNearLower, V(10) * k); p.SetDegrees(RigPart.FootNear, V(11) * k);
            p.SetDegrees(RigPart.LegFarUpper, V(12) * k); p.SetDegrees(RigPart.LegFarLower, V(13) * k); p.SetDegrees(RigPart.FootFar, V(14) * k);
            p.hop = V(15) * k;
            return p;
        }


        // ---- WakeUp and Ignite: key tables in WORLD angles (degrees, 0 = hanging straight down, positive = swinging forward, 180 = up), converted to the rig's parent-relative angles ----
        // This is easier to author for poses far from standing (lying face down): an arm "pointing down" is 0 whatever the torso does. Rig intensity is ignored (the poses are
        // exact). Feet: footWorld 0 = flat on the ground, footWorld = shinWorld = toes following the shin (kneeling / lying).
        // columns: u, body, head, hair, upperN, lowerN, upperF, lowerF, thighN, shinN, footN, thighF, shinF, footF, hop, rootX, rootY (y down, pixels)
        private static RigPose FromWorldKeys(float[][] keys, int t, int n, bool tremble = false, bool smooth = false)
        {
            float u = n > 1 ? t / (float)(n - 1) : 0f;
            int a = 0;
            while (a < keys.Length - 2 && u > keys[a + 1][0]) a++;
            float[] ka = keys[a], kb = keys[a + 1];
            float f = Mathf.Clamp01((u - ka[0]) / (kb[0] - ka[0]));
            float eased = f * f * (3f - 2f * f);   // ease in/out
            // smooth = a cubic Hermite curve through the keys (no stop at every key), with the tangent set to zero at a local extreme so it never overshoots a key.
            float V(int i)
            {
                if (!smooth) return Mathf.Lerp(ka[i], kb[i], eased);
                float h = kb[0] - ka[0];
                float slope = (kb[i] - ka[i]) / h;
                float m0 = 0f, m1 = 0f;
                if (a > 0) { float before = (ka[i] - keys[a - 1][i]) / (ka[0] - keys[a - 1][0]); if (before * slope > 0f) m0 = (before + slope) * 0.5f; }
                if (a + 2 < keys.Length) { float after = (keys[a + 2][i] - kb[i]) / (keys[a + 2][0] - kb[0]); if (after * slope > 0f) m1 = (after + slope) * 0.5f; }
                float f2 = f * f, f3 = f2 * f;
                return (2f * f3 - 3f * f2 + 1f) * ka[i] + (f3 - 2f * f2 + f) * h * m0 + (-2f * f3 + 3f * f2) * kb[i] + (f3 - f2) * h * m1;
            }

            float body = V(1), head = V(2), hair = V(3), upN = V(4), loN = V(5), upF = V(6), loF = V(7), thN = V(8), shN = V(9), ftN = V(10), thF = V(11), shF = V(12), ftF = V(13);
            if (tremble && u > 0.4f)   // the held pose of Ignite shivers a little: the power is not under control yet
            {
                upN += 1.6f * Mathf.Sin(t * 2.4f);
                loN += 2.2f * Mathf.Sin(t * 2.4f + 1.2f);
            }

            var p = new RigPose();
            p.SetDegrees(RigPart.Body, body);
            p.SetDegrees(RigPart.Head, head - body);
            p.SetDegrees(RigPart.Hair, hair - head);
            p.SetDegrees(RigPart.ArmNearUpper, upN - body); p.SetDegrees(RigPart.ArmNearLower, loN - upN);
            p.SetDegrees(RigPart.ArmFarUpper, upF - body); p.SetDegrees(RigPart.ArmFarLower, loF - upF);
            p.SetDegrees(RigPart.LegNearUpper, thN); p.SetDegrees(RigPart.LegNearLower, shN - thN); p.SetDegrees(RigPart.FootNear, ftN - shN);
            p.SetDegrees(RigPart.LegFarUpper, thF); p.SetDegrees(RigPart.LegFarLower, shF - thF); p.SetDegrees(RigPart.FootFar, ftF - shF);
            p.hop = V(14);
            p.root = new Vector2(V(15), V(16));
            return p;
        }

        // WakeUp (one-shot, 72 frames at 12 fps = 6 s, smooth curves through the keys; version 3, modelled on a pixel-art wake-up reference):
        //   0-11  lying face down, breathing, a hand twitches (the first sign of life)
        //  12-21  the head lifts a little, the hands slide in beside the ribs, the elbows rise
        //  22-33  the near knee, then the far knee, slide under her; she pushes the floor away
        //  34-42  hands and knees: head hanging, her back rocks with every breath
        //  43-49  the near foot steps forward (a lunge, the far knee still on the floor)
        //  50-58  she rises hunched, the back foot drags in, arms hanging, head down
        //  59-64  standing, swaying, the head slowly comes up
        //  65-71  the near hand goes to her forehead (elbow high, head bowed) and is held (the last 3 frames are the hold: the game plays the first line while it lasts, then Idle)
        // Lying: the torso is rotated -90 (clockwise: the head leads to the right, the face looks at the ground), the legs trail backwards (-88).
        // The renderer pins the lowest foot to the ground line, so every phase keeps a foot, a knee or a shin on the ground (no leg hangs in the air behind the body).
        // Breathing, the twitch and the rocking are added on top of the keys in WakeUp().
        public const int WakeUpFrames = 72;
        private static float F(int frame) => frame / (float)(WakeUpFrames - 1);

        private static readonly float[][] WakeUpKeys =
        {
            //                u    body  head  hair   upN   loN   upF   loF   thN   shN   ftN   thF   shF   ftF  hop rX rY
            new[] { F(0),   -90f,  -90f,  0f,  -70f,   75f,  -78f,   70f,  -88f,  -88f,  -88f,  -92f,  -92f,  -92f, 0f, 0f, 0f },
            new[] { F(11),  -90f,  -88f,  2f,  -70f,   75f,  -78f,   70f,  -88f,  -88f,  -88f,  -92f,  -92f,  -92f, 0f, 0f, 0f },
            new[] { F(17),  -89f,  -80f,  6f,  -96f,   45f, -100f,   40f,  -88f,  -88f,  -88f,  -92f,  -92f,  -92f, 0f, 0f, 0f },
            new[] { F(22),  -87f,  -72f,  8f, -126f,   14f, -130f,   18f,  -86f,  -88f,  -88f,  -91f,  -91f,  -91f, 0f, 0f, 0f },
            // 24-34: the push-up. Her hands stay PLANTED on the floor while the torso rises: for every key the arm angles were computed from the rig (arm 5.7 + 4.7 px, thigh 9.8, torso 9.7,
            // knee 2.5 px above the floor, hand 2 px): hip height H = 2.5 + thigh * cos(thigh angle), shoulder height S = H + torso * sin(body + 90), the hand directly under the shoulder at
            // distance d = S - 2, upper arm = -acos((a^2 + d^2 - b^2) / 2ad), forearm = +acos((b^2 + d^2 - a^2) / 2bd). On hands and knees a horizontal back is the best case (S = 12 = the arm's reach),
            // and measured on the rendered frames that still left the hands 4-5 px above the floor (version 4), so from frame 31 the torso tips nose-down (body -113 degrees, head -98):
            // the shoulders drop about 4 px and the hands touch the floor (gap 0-1 px, version 5; version 3 held the back 12 degrees up: "pushing up from nothing").
            new[] { F(24),  -87f,  -71f,  8f,  -55f,   97f,  -58f,   95f,  -80f,  -90f,  -90f,  -86f,  -91f,  -91f, 0f, 0f, 0f },
            new[] { F(26),  -86f,  -70f,  8f,  -50f,   69f,  -52f,   66f,  -65f,  -92f,  -92f,  -80f,  -92f,  -92f, 0f, 0f, 0f },
            new[] { F(28),  -95f,  -76f,  8f,  -34f,   43f,  -36f,   41f,  -40f,  -92f,  -92f,  -56f,  -92f,  -92f, 0f, 0f, 0f },
            new[] { F(31),  -104f,  -88f,  9f,  -15f,   18f,  -16f,   16f,  -15f,  -92f,  -92f,  -28f,  -92f,  -92f, 0f, 0f, 0f },
            new[] { F(34),  -113f,  -98f, 10f,  -14f,   17f,  -15f,   16f,    4f,  -90f,  -90f,    4f,  -90f,  -90f, 0f, 0f, 0f },
            new[] { F(38),  -114f,  -100f,  9f,  -13f,   16f,  -14f,   15f,    5f,  -90f,  -90f,    5f,  -90f,  -90f, 0f, 0f, 0f },
            new[] { F(42),  -113f,  -98f,  9f,  -14f,   17f,  -15f,   16f,    6f,  -90f,  -90f,    5f,  -90f,  -90f, 0f, 0f, 0f },
            new[] { F(44),  -108f,  -95f,  8f,   -13f,   16f,   -14f,   15f,   8f,  -86f,  -84f,    4f,  -90f,  -90f, 0f, 0f, 0f },
            new[] { F(46),  -76f,  -60f,  8f,    4f,    8f,    6f,   10f,   34f,  -52f,  -42f,    4f,  -90f,  -90f, 0f, 0f, 0f },
            new[] { F(49),  -44f,  -30f,  6f,   20f,   28f,   24f,   26f,   86f,  -14f,    0f,    4f,  -90f,  -90f, 0f, 0f, 0f },
            new[] { F(54),  -24f,  -16f,  5f,   26f,   32f,   28f,   32f,   46f,   -4f,    0f,  -18f,  -34f,  -20f, 0f, 0f, 0f },
            new[] { F(58),   -9f,  -14f,  4f,   14f,   20f,   14f,   20f,   14f,    0f,    0f,   -2f,  -16f,  -12f, 0f, 0f, 0f },
            new[] { F(62),   -3f,  -10f,  3f,    6f,   10f,   -2f,    6f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
            new[] { F(65),    0f,   -4f,  3f,   30f,   60f,   -5f,    4f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
            new[] { F(68),    0f,   -9f,  3f,  100f,  150f,   -5f,    3f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
            new[] { F(71),    0f,   -9f,  3f,  110f,  165f,   -5f,    3f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
        };

        // Key pose plus life: the breathing of the lying body, the twitch of a hand, the rocking on hands and knees, the sway of the first steps of standing.
        // All of it is added to the body (its children, the head and the upper arms, follow it) and eased in and out, so no key pose is disturbed.
        private static RigPose WakeUp(int t, int n)
        {
            RigPose p = FromWorldKeys(WakeUpKeys, t, n, false, true);
            float f = n > 1 ? t / (float)(n - 1) * (WakeUpFrames - 1) : 0f;
            float Window(float from, float to, float ease) => Mathf.Clamp01((f - from) / ease) * Mathf.Clamp01((to - f) / ease);

            float breath = 2.6f * Mathf.Sin(f * 2f * Mathf.PI / 9f) * Window(-1f, 24f, 4f);                 // lying: a slow breath that lifts the back
            breath += 1.2f * Mathf.Sin((f - 34f) * 2f * Mathf.PI / 8f) * Window(34f, 43f, 3f);                // hands and knees: she gasps, the back rocks
            breath += 1.6f * Mathf.Sin((f - 59f) * 2f * Mathf.PI / 10f) * Window(58f, 66f, 3f);               // standing: she sways
            p.angle[(int)RigPart.Body] += breath * Mathf.Deg2Rad;
            p.angle[(int)RigPart.Head] -= breath * 0.6f * Mathf.Deg2Rad;                                      // the head lags behind the back
            p.angle[(int)RigPart.Hair] -= breath * 0.8f * Mathf.Deg2Rad;

            float twitch = 9f * Mathf.Sin((f - 6f) * Mathf.PI / 1.5f) * Window(5f, 10f, 1.5f);               // the near hand twitches: two quick flicks
            p.angle[(int)RigPart.ArmNearLower] += twitch * Mathf.Deg2Rad;
            return p;
        }

        // Ignite (one-shot, 10 frames at 12 fps, the last frame is the held pose): she looks at her right hand, raises it palm up in front of her, and it shivers.
        // The flame itself is a game effect placed on the palm of the last frame.
        private static readonly float[][] IgniteKeys =
        {
            //          u    body  head  hair   upN   loN   upF   loF   thN   shN   ftN   thF   shF   ftF  hop rX rY
            new[] { 0.00f,    0f,    0f,  0f,    5f,   15f,   -5f,    3f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
            new[] { 0.22f,    2f,   -4f,  2f,   30f,   60f,   10f,   50f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
            new[] { 0.44f,    4f,  -10f,  3f,   45f,   95f,   12f,   55f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
            new[] { 0.70f,    3f,  -12f,  3f,   40f,   92f,   12f,   55f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
            new[] { 1.00f,    3f,  -12f,  3f,   42f,   96f,   12f,   55f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
        };

        // LowerHand (one-shot, 10 frames at 12 fps, smooth): the end of Ignite. The flame has died down; the raised hand sinks back to her side, the head comes up and she
        // stands as in Idle. The first key is Ignite's held pose, the last one its first pose, so Ignite -> LowerHand -> Idle never jumps.
        private static readonly float[][] LowerHandKeys =
        {
            //          u    body  head  hair   upN   loN   upF   loF   thN   shN   ftN   thF   shF   ftF  hop rX rY
            new[] { 0.00f,    3f,  -12f,  3f,   42f,   96f,   12f,   55f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
            new[] { 0.30f,    3f,  -10f,  2f,   38f,   70f,   10f,   42f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },   // the forearm drops first
            new[] { 0.60f,    1f,   -5f,  1f,   20f,   34f,    2f,   18f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
            new[] { 0.85f,    0f,   -1f,  0f,    8f,   18f,   -4f,    5f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
            new[] { 1.00f,    0f,    0f,  0f,    5f,   15f,   -5f,    3f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
        };

        // Dash (a fire dash; one-shot, 8 frames at 20 fps, the last frames are the held lunge): a short dip, then a running leap forward: the torso leans in, the near knee
        // is driven up and forward, the far leg stretches back, both arms are swept back, the hair streams behind. Lifted off the ground (hop) so the stretched back leg
        // does not pin the body down. The held frames flutter (hair and arms), so a dash that lasts longer than the clip still looks alive.
        private static readonly float[][] DashKeys =
        {
            //          u    body  head  hair   upN   loN   upF   loF   thN   shN   ftN   thF   shF   ftF  hop rX rY
            new[] { 0.00f,    0f,    0f,   0f,    5f,   15f,   -5f,    3f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },
            new[] { 0.15f,  -10f,   -6f,  -4f,   22f,   40f,   18f,   35f,   26f,  -18f,    0f,  -10f,  -32f,  -20f, 0f, 0f, 0f },   // anticipation: a dip, arms forward
            new[] { 0.35f,  -24f,  -14f, -22f,  -40f,  -20f,  -55f,  -40f,   62f,  -12f,    8f,  -34f,  -60f,  -55f, 2f, 2f, 0f },   // launch
            new[] { 0.55f,  -30f,  -18f, -32f,  -58f,  -36f,  -72f,  -52f,   74f,   -8f,   12f,  -44f,  -70f,  -62f, 3f, 3f, 0f },   // the lunge
            new[] { 1.00f,  -30f,  -18f, -36f,  -60f,  -38f,  -74f,  -54f,   72f,   -8f,   12f,  -44f,  -70f,  -62f, 3f, 3f, 0f },   // held
        };

        // ReachColumn (one-shot, 12 frames at 12 fps, smooth): the bridge from the end of WakeUp to LeanOnColumn. The hand leaves the forehead and the forearm unfolds
        // forward, she reaches out while the torso starts to tilt and the far leg slides back, then the palm settles on the stone and the head drops. The first key is
        // WakeUp's last pose and the last key LeanOnColumn's first pose, so WakeUp -> ReachColumn -> LeanOnColumn never jumps.
        private static readonly float[][] ReachColumnKeys =
        {
            //          u    body  head  hair   upN   loN   upF   loF   thN   shN   ftN   thF   shF   ftF  hop rX rY
            new[] { 0.00f,    0f,   -9f,   3f,  110f,  165f,   -5f,    3f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f, 0f, 0f },   // = WakeUp's last pose
            new[] { 0.30f,   -3f,  -13f,   1f,   88f,  110f,   -6f,    2f,    6f,    2f,    0f,   -8f,  -12f,  -12f, 0f, 0f, 0f },   // the hand comes off the forehead
            new[] { 0.60f,   -9f,  -22f,  -6f,   74f,   78f,  -10f,   -2f,    7f,    2f,    0f,  -15f,  -19f,  -16f, 0f, 0f, 0f },   // reaching out, tilting in
            new[] { 0.85f,  -14f,  -31f, -12f,   77f,   81f,  -13f,   -5f,    8f,    2f,    0f,  -20f,  -24f,  -19f, 0f, 0f, 0f },   // the palm on the stone
            new[] { 1.00f,  -16f,  -34f, -14f,   76f,   80f,  -14f,   -6f,    8f,    2f,    0f,  -22f,  -26f,  -20f, 0f, 0f, 0f },   // = LeanOnColumn's first pose
        };

        // LeanOnColumn (loop, 8 frames at 6 fps): she has just got up and leans on a column in front of her (the side she faces), the near arm straight out at
        // shoulder height with the palm flat on the stone, the body tilted towards it, the head hanging, the far leg a little back; one slow breath per loop
        // (the chest and head sink and rise, the arm takes it back so the palm stays on the column).
        private static readonly float[][] LeanOnColumnKeys =
        {
            //          u    body  head  hair   upN   loN   upF   loF   thN   shN   ftN   thF   shF   ftF  hop rX rY
            new[] { 0.00f,  -16f,  -34f, -14f,   76f,   80f,  -14f,   -6f,    8f,    2f,    0f,  -22f,  -26f,  -20f, 0f, 0f, 0f },
            new[] { 1.00f,  -16f,  -34f, -14f,   76f,   80f,  -14f,   -6f,    8f,    2f,    0f,  -22f,  -26f,  -20f, 0f, 0f, 0f },
        };

        // ClimbDown (loop, 8 frames at 8 fps), for a BACK-VIEW sprite: down a ladder that faces the camera, the character's back to the viewer (how platformers
        // show a front-facing ladder). "Near" = the arm and leg on the right of the screen, "far" = the left ones. The classic back-view climb (Mega Man and the like): one arm
        // straight up to a rung above the head, the other bent with the elbow out and the hand at the face, hands on the rungs at shoulder width; the opposite knee lifted (in a back view a raised knee only shortens the leg: the whole leg is moved up by whole
        // pixels, RigPose.shift, never re-sampled); each pose is held and the sides swap quickly. The renderer keeps the lowest foot on the ground line; the game moves the
        // character down. Versions 1-3 were side views (a side view does not fit a front-facing ladder) and version 4 spread the arms in a V to the rails; all rejected by the game's author, 2026-10-11.
        // Arm angles are world angles (positive = counter-clockwise on screen: the right arm rises outwards with +, the left one with -).
        private static readonly float[][] ClimbDownKeys =
        {
            //          u    body  head  hair   upN   loN   upF   loF   thN   shN   ftN   thF   shF   ftF  hop rX rY
            new[] { 0.00f,    2f,    3f,   0f,  172f,  178f,  -95f, -172f,    0f,    0f,    0f,   -8f,    2f,    0f, 0f, 0f, 0f },   // right hand straight up on a rung, left elbow out, hand at the face; left knee up
            new[] { 0.30f,    2f,    3f,   0f,  172f,  178f,  -95f, -172f,    0f,    0f,    0f,   -8f,    2f,    0f, 0f, 0f, 0f },   // held
            new[] { 0.50f,   -2f,   -3f,   0f,   95f,  172f, -172f, -178f,    8f,   -2f,    0f,    0f,    0f,    0f, 0f, 0f, 0f },   // swapped: left hand up, right knee up
            new[] { 0.80f,   -2f,   -3f,   0f,   95f,  172f, -172f, -178f,    8f,   -2f,    0f,    0f,    0f,    0f, 0f, 0f, 0f },   // held
            new[] { 1.00f,    2f,    3f,   0f,  172f,  178f,  -95f, -172f,    0f,    0f,    0f,   -8f,    2f,    0f, 0f, 0f, 0f },   // = the first
        };
        private const int ClimbKneeLift = 4;   // pixels the lifted leg rises

        private static RigPose ClimbDown(int t, int n)
        {
            RigPose p = FromWorldKeys(ClimbDownKeys, t, n + 1, false, true);   // a loop: n + 1 so the last frame stops one step short of the first
            // how much of the first pose is in this frame (1 while it is held, 0 while the second one is), so the knees swap with the hands
            float u = t / (float)Mathf.Max(1, n);
            float first = u <= 0.3f ? 1f : u <= 0.5f ? Mathf.SmoothStep(1f, 0f, (u - 0.3f) / 0.2f) : u <= 0.8f ? 0f : Mathf.SmoothStep(0f, 1f, (u - 0.8f) / 0.2f);
            p.shift[(int)RigPart.LegFarUpper] = new Vector2Int(0, -Mathf.RoundToInt(ClimbKneeLift * first));
            p.shift[(int)RigPart.LegNearUpper] = new Vector2Int(0, -Mathf.RoundToInt(ClimbKneeLift * (1f - first)));
            return p;
        }

        private static RigPose LeanOnColumn(int t, int n)
        {
            RigPose p = FromWorldKeys(LeanOnColumnKeys, t, n);
            float breath = Mathf.Sin(2f * Mathf.PI * t / Mathf.Max(1, n));   // one breath per loop
            p.angle[(int)RigPart.Body] += -2.5f * breath * Mathf.Deg2Rad;          // the chest sinks towards the column...
            p.angle[(int)RigPart.Head] += -2.5f * breath * Mathf.Deg2Rad;          // ...the head hangs a little lower...
            p.angle[(int)RigPart.Hair] += 1.5f * breath * Mathf.Deg2Rad;
            p.angle[(int)RigPart.ArmNearUpper] += 2.5f * breath * Mathf.Deg2Rad;   // ...and the arm takes it back, so the palm stays put
            p.angle[(int)RigPart.ArmNearLower] += 3f * breath * Mathf.Deg2Rad;     // the elbow gives a little
            p.angle[(int)RigPart.ArmFarLower] += 4f * breath * Mathf.Deg2Rad;      // the free hand hangs loose
            return p;
        }

        private static RigPose Dash(int t, int n)
        {
            RigPose p = FromWorldKeys(DashKeys, t, n, false, true);
            float u = n > 1 ? t / (float)(n - 1) : 0f;
            if (u > 0.5f)   // the held lunge flutters: the hair and the trailing arms catch the rush of air
            {
                float w = Mathf.Sin(t * 2.7f);
                p.angle[(int)RigPart.Hair] += 4f * w * Mathf.Deg2Rad;
                p.angle[(int)RigPart.ArmFarLower] += 3f * Mathf.Sin(t * 2.7f + 1f) * Mathf.Deg2Rad;
            }
            return p;
        }

        // Hurt (one-shot, 8 frames at 16 fps, smooth): she is hit from the front: the torso and head snap back, the near arm comes up to guard, the knees give, she is
        // pushed back a little, then recovers to the standing pose (first = last pose, so it blends into Idle).
        private static readonly float[][] HurtKeys =
        {
            //          u    body  head  hair   upN   loN   upF   loF   thN   shN   ftN   thF   shF   ftF  hop rX rY
            new[] { 0.00f,    0f,    0f,   0f,    5f,   15f,   -5f,    3f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f,  0f, 0f },
            new[] { 0.14f,   16f,   26f,  22f,   62f,  118f,   40f,   92f,   16f,  -10f,    0f,  -16f,  -26f,  -10f, 0f, -2f, 0f },   // the blow: snap back, arm up
            new[] { 0.35f,   12f,   18f,  14f,   58f,  110f,   34f,   80f,   20f,  -16f,    0f,  -18f,  -30f,  -10f, 0f, -2f, 0f },   // recoil, knees give
            new[] { 0.65f,    4f,    6f,   4f,   26f,   50f,   10f,   30f,   12f,   -4f,    0f,  -10f,  -16f,  -10f, 0f, -1f, 0f },
            new[] { 1.00f,    0f,    0f,   0f,    5f,   15f,   -5f,    3f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f,  0f, 0f },
        };

        // StaggerShock (one-shot, 8 frames at 12 fps, smooth, the last frame is the held pose): she sees something she cannot bear: a jolt, the torso and head jerk
        // back, both hands come half up in front of the chest, the far foot steps back half a step and the near knee gives; then she stays frozen like that. Unlike Hurt
        // nothing hit her (no guarding arm thrown up) and she does not recover: the game returns to Idle when it wants. The first key is Idle's rest pose.
        private static readonly float[][] StaggerShockKeys =
        {
            //          u    body  head  hair   upN   loN   upF   loF   thN   shN   ftN   thF   shF   ftF  hop rX rY
            new[] { 0.00f,    0f,    0f,   0f,    5f,   15f,   -5f,    3f,    6f,    2f,    0f,   -6f,  -10f,  -10f, 0f,  0f, 0f },
            new[] { 0.28f,   12f,   14f,  18f,   52f,  128f,   42f,  112f,   14f,  -10f,    0f,  -20f,  -18f,   -8f, 0f, -2f, 0f },   // the jolt: jerk back, hands fly up towards the face
            new[] { 0.60f,    9f,    9f,   9f,   48f,  122f,   38f,  106f,   12f,   -8f,    0f,  -18f,  -16f,   -8f, 0f, -3f, 0f },   // the half step back settles
            new[] { 1.00f,    7f,    7f,   6f,   46f,  120f,   36f,  104f,   10f,   -6f,    0f,  -16f,  -14f,   -6f, 0f, -3f, 0f },   // frozen, hands near the chin
        };

        // Spit (one-shot, 10 frames at 12 fps, smooth): a breath or spit attack, for beasts as well as people (a side-view quadruped is rigged with its front legs
        // as the arms, its hind legs as the legs and its torso pivoting at the hip): the head draws back and up with the body, the front legs ease back; then the head
        // snaps forward and down (the moment the projectile leaves), the body lunges in, the front legs brace forward, the hind legs push back; it settles to rest.
        private static readonly float[][] SpitKeys =
        {
            //          u    body  head  hair   upN   loN   upF   loF   thN   shN   ftN   thF   shF   ftF  hop  rX    rY
            new[] { 0.00f,    0f,    0f,   0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f, 0f,  0f,   0f },
            new[] { 0.35f,    5f,   20f,  20f,   -6f,   -6f,   -4f,   -4f,    3f,    3f,    0f,   -3f,   -3f,    0f, 0f, -0.6f, 0f },   // draw back
            new[] { 0.55f,   -6f,  -18f, -18f,   10f,    6f,    8f,    4f,   -6f,   -6f,    0f,   -8f,   -8f,    0f, 0f,  1.2f, 0f },   // the snap: spit
            new[] { 0.75f,   -4f,  -12f, -12f,    6f,    4f,    5f,    3f,   -4f,   -4f,    0f,   -5f,   -5f,    0f, 0f,  0.8f, 0f },   // follow-through
            new[] { 1.00f,    0f,    0f,   0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f, 0f,  0f,   0f },
        };

        // RearUp (one-shot, 14 frames at 12 fps, smooth): rearing up on the hind legs with a roar, then slamming the front down (a stomp). For a side-view quadruped
        // (front legs = arms, torso pivoting at the hip): a short crouch, the body pitches up about 22 degrees about the hip, the head throws back further, the front
        // legs fold under the chest (the paws hang back, version 2: version 1 swung them forward and read as a pounce), held for the roar; then the front comes down
        // hard past rest (the stomp, a little forward) and settles. The hind legs stay planted.
        private static readonly float[][] RearUpKeys =
        {
            //          u    body  head  hair   upN   loN   upF   loF   thN   shN   ftN   thF   shF   ftF  hop  rX    rY
            new[] { 0.00f,    0f,    0f,   0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f, 0f,  0f,   0f },
            new[] { 0.20f,   -4f,   -6f,  -6f,    6f,   -4f,    6f,   -4f,    6f,  -10f,    4f,    4f,  -10f,    6f, 0f,  0f,   0f },   // crouch
            new[] { 0.45f,   22f,   32f,  32f,   34f,  -38f,   28f,  -42f,    4f,   -4f,    0f,    0f,   -4f,    0f, 0f,  0f,   0f },   // rear up: the front legs fold
            new[] { 0.65f,   24f,   36f,  36f,   38f,  -34f,   32f,  -38f,    4f,   -4f,    0f,    0f,   -4f,    0f, 0f,  0f,   0f },   // the roar, held
            new[] { 0.82f,   -6f,  -10f, -10f,   -4f,   -2f,   -2f,    0f,   -4f,   -6f,    0f,   -6f,   -6f,    0f, 0f,  1.5f, 0f },   // the stomp
            new[] { 1.00f,    0f,    0f,   0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f, 0f,  0f,   0f },
        };

        // Recoil (one-shot, 8 frames at 16 fps, smooth): a hit taken by a side-view quadruped (front legs = arms): a jolt back, the front pitches up a little, the head
        // jerks back, the front legs pull in under the chest, half a step back; then it recovers to rest. Hurt is the humanoid version (a guarding arm).
        private static readonly float[][] RecoilKeys =
        {
            //          u    body  head  hair   upN   loN   upF   loF   thN   shN   ftN   thF   shF   ftF  hop  rX    rY
            new[] { 0.00f,    0f,    0f,   0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f, 0f,  0f,   0f },
            new[] { 0.25f,    8f,   20f,  20f,  -10f,  -18f,  -12f,  -20f,    6f,   -6f,    0f,    4f,   -6f,    0f, 0f, -1.5f, 0f },   // the jolt
            new[] { 0.50f,    6f,   14f,  14f,   -8f,  -14f,   -9f,  -15f,    5f,   -5f,    0f,    3f,   -5f,    0f, 0f, -2f,   0f },   // half a step back
            new[] { 1.00f,    0f,    0f,   0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f,    0f, 0f, -2f,   0f },   // recovered, where it ended up
        };

        // ---- crouch walk (goose-step walk): the held crouch pose, with the legs taking turns (loop) ----
        // Same posture as Crouch (hips near knee height, torso leaning 32 degrees with Head = -Body so the face is never re-sampled, arms hanging in front of the knees, sword low),
        // the legs alternate: the foot that moves forward lifts (knee folds more), the other stays flat and pushes. Both thighs stay beyond 65 degrees, so the renderer fills the
        // pelvis cells like in Crouch. The clip is in place (the game moves the character); the renderer pins the lowest foot to the ground line, so the hips bob by themselves.
        private static RigPose CrouchWalk(int t, int n, float k)
        {
            float ph = 2f * Mathf.PI * t / n;
            float s = Mathf.Sin(ph), c = Mathf.Cos(ph);
            const float lean = 32f;

            var p = new RigPose();
            float thighN = 96f + 15f * s, thighF = 90f - 15f * s;
            float shinN = -(132f + 26f * Mathf.Max(0f, c)), shinF = -(126f + 26f * Mathf.Max(0f, -c));
            p.SetDegrees(RigPart.LegNearUpper, thighN * k); p.SetDegrees(RigPart.LegNearLower, shinN * k);
            p.SetDegrees(RigPart.LegFarUpper, thighF * k); p.SetDegrees(RigPart.LegFarLower, shinF * k);
            p.SetDegrees(RigPart.FootNear, -(thighN + shinN) * 0.92f * k);     // feet stay roughly flat, the lifted one tips its toe down a little
            p.SetDegrees(RigPart.FootFar, -(thighF + shinF) * 0.92f * k);

            p.SetDegrees(RigPart.Body, -lean * k); p.SetDegrees(RigPart.Head, lean * k);   // exact negatives: the head's net rotation is 0
            p.SetDegrees(RigPart.Hair, -3f * k * Mathf.Cos(ph - 0.5f));
            p.SetDegrees(RigPart.ArmNearUpper, (42f - 5f * s) * k); p.SetDegrees(RigPart.ArmNearLower, 25f * k);
            p.SetDegrees(RigPart.ArmFarUpper, (32f + 5f * s) * k); p.SetDegrees(RigPart.ArmFarLower, 20f * k);
            p.SetDegrees(RigPart.Weapon, (-8f + 2.5f * s) * k);
            return p;
        }
    }
}
