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
                case "sit": return Sit(t, n, k);
                case "crouch": return Crouch(t, n, k);
                case "crouchwalk": return CrouchWalk(t, n, k);
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
