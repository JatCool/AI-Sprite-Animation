using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Joint limits per animation type. Angles are relative to the parent bone, in degrees, with the <see cref="RigPose"/> sign convention
    /// (counter-clockwise on screen is positive: a hanging limb swings forward with +, a knee bends with a negative shin angle).
    /// </summary>
    public static class PoseLimits
    {
        // min, max per RigPart (full range, used by Run and Attack)
        private static readonly float[,] Full =
        {
            { -35,  25 },   // Body (forward lean is negative)
            { -40,  40 },   // Head
            { -90,  90 },   // Hair
            { -110, 175 },  // ArmNearUpper
            { -15,  150 },  // ArmNearLower (elbow flexes forward; little hyper-extension)
            { -140, 140 },  // Weapon (relative to the hand)
            { -110, 175 },  // ArmFarUpper
            { -15,  150 },  // ArmFarLower
            { -65,  80 },   // LegNearUpper
            { -150, 12 },   // LegNearLower (knee flexes backwards)
            { -65,  65 },   // FootNear
            { -65,  80 },   // LegFarUpper
            { -150, 12 },   // LegFarLower
            { -65,  65 },   // FootFar
        };

        /// <summary>Walk and Idle use a narrower range than Run and Attack.</summary>
        public static float RangeScale(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "idle": return 0.4f;
                case "walk": return 0.8f;
                default: return 1f;
            }
        }

        public static void Get(string kind, int part, out float min, out float max)
        {
            float k = RangeScale(kind);
            min = Full[part, 0] * k; max = Full[part, 1] * k;

            // A walker's arms swing about +-35 degrees and its thighs about +-30; poses far beyond that are not walks (an estimator
            // mixing up arms, a hand raised to the face, ...). Run and Attack use the full range.
            string name = (kind ?? "").ToLowerInvariant();
            var p = (RigPart)part;
            bool upperArm = p == RigPart.ArmNearUpper || p == RigPart.ArmFarUpper, thigh = p == RigPart.LegNearUpper || p == RigPart.LegFarUpper;
            bool foreArm = p == RigPart.ArmNearLower || p == RigPart.ArmFarLower;
            if (name == "walk")
            {
                if (upperArm) { min = Mathf.Max(min, -55f); max = Mathf.Min(max, 65f); }
                if (foreArm) { min = Mathf.Max(min, -5f); max = Mathf.Min(max, 100f); }
                if (thigh) { min = Mathf.Max(min, -50f); max = Mathf.Min(max, 55f); }
            }
            else if (name == "idle")
            {
                if (upperArm) { min = Mathf.Max(min, -30f); max = Mathf.Min(max, 40f); }
                if (foreArm) { min = Mathf.Max(min, -5f); max = Mathf.Min(max, 60f); }
            }
        }

        /// <summary>Only Run may leave the ground (a flight phase). Everything else keeps its feet on the ground line.</summary>
        public static bool AllowsAirborne(string kind) => string.Equals(kind, "run", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks a pose sequence before it may reach the rig renderer and repairs what can safely be repaired:
    /// non-finite values, angles that wrapped around (+-180), impossible joint angles, frames the estimator got completely wrong, 180-degree flips,
    /// vertical drift and flight phases where the feet must stay grounded.
    /// A sequence that needs too much repair is rejected instead of being silently "fixed" into something unrelated to the AI output.
    /// The pose constraints (spikes, foot jumps, ...) are a separate stage: <see cref="PoseConstraints"/>.
    /// </summary>
    public static class PoseValidator
    {
        /// <summary>Validates and repairs <paramref name="channels"/> in place (see <see cref="PoseChannels"/> for the layout).</summary>
        public static PoseReport Repair(RigDefinition rig, float[][] channels, string kind, bool loop, PoseCleanupSettings settings)
        {
            var report = new PoseReport { Kind = kind ?? "" };
            int n = channels.Length;
            report.TotalValues = n * PoseChannels.Count;
            report.BeginFrames(n);
            if (n == 0) { report.Errors.Add("The pose sequence is empty."); report.Rejected = true; return report; }
            for (int i = 0; i < n; i++)
                if (channels[i] == null || channels[i].Length != PoseChannels.Count)
                {
                    report.Errors.Add($"Frame {i} does not contain all {RigDefinition.PartCount} joints plus root.");
                    report.Rejected = true;
                    return report;
                }

            var invalid = new bool[n][];   // per frame and channel: not usable (undetected, or an impossible angle)
            var fallback = new float[n][]; // for a channel that is invalid in every frame: the clamped value
            var counted = new bool[n][];   // already counted as a repair (discarded angles), so interpolating them is not counted again
            for (int i = 0; i < n; i++) { invalid[i] = new bool[PoseChannels.Count]; fallback[i] = new float[PoseChannels.Count]; counted[i] = new bool[PoseChannels.Count]; }

            // 1. angles that wrapped around (an elbow measured as -226 deg is +134 deg) -> the equivalent angle nearest to the allowed range;
            //    an angle that is still far outside the range is an estimator failure, not a pose: it is discarded and interpolated, not clamped to the limit
            int wrapped = 0, discarded = 0;
            for (int i = 0; i < n; i++)
                for (int p = 0; p < RigDefinition.PartCount; p++)
                {
                    float v = channels[i][p];
                    if (!PoseChannels.IsFinite(v)) { invalid[i][p] = true; fallback[i][p] = 0f; continue; }
                    PoseLimits.Get(kind, p, out float lo, out float hi);
                    float centre = 0.5f * (lo + hi);
                    float w = v - 360f * Mathf.Round((v - centre) / 360f);
                    if (Mathf.Abs(w - v) > 0.01f) { channels[i][p] = w; wrapped++; report.MarkCorrected(i); }
                    float excess = Mathf.Max(lo - w, w - hi);
                    fallback[i][p] = Mathf.Clamp(w, lo, hi);
                    if (excess > Mathf.Max(20f, 0.25f * (hi - lo))) { invalid[i][p] = true; counted[i][p] = true; channels[i][p] = float.NaN; discarded++; report.MarkCorrected(i); }
                }
            if (wrapped > 0) { report.RepairedValues += wrapped; report.Repairs.Add($"{wrapped} joint angle(s) wrapped around +-180 deg normalised"); report.AddCorrection("angle wrap-around", wrapped, 0); }
            if (discarded > 0) { report.RepairedValues += discarded; report.Repairs.Add($"{discarded} impossible joint angle(s) (far outside the {kind ?? "default"} range) discarded and interpolated"); report.AddCorrection("impossible angle (discarded)", discarded, 0); }

            // 2. frames the estimator got mostly wrong are rejected as a whole and rebuilt from their neighbours
            if (settings.constraints == null || settings.constraints.rejectBrokenFrames)
            {
                float brokenFraction = settings.constraints?.brokenFrameFraction ?? 0.5f, maxRejected = settings.constraints?.maxRejectedFrames ?? 0.25f;
                for (int i = 0; i < n; i++)
                {
                    int bad = 0;
                    for (int p = 0; p < RigDefinition.PartCount; p++) if (invalid[i][p]) bad++;
                    if (bad / (float)RigDefinition.PartCount < brokenFraction) continue;
                    report.RejectedFrameList.Add(i);
                    report.MarkCorrected(i);
                    for (int c = 0; c < PoseChannels.Count; c++) { invalid[i][c] = true; channels[i][c] = float.NaN; }
                }
                if (report.RejectedFrameList.Count > 0)
                {
                    report.Repairs.Add($"{report.RejectedFrameList.Count} frame(s) rejected (more than {brokenFraction:P0} of their joints unusable) and rebuilt from the neighbouring frames");
                    if (report.RejectedFrameList.Count > maxRejected * n)
                    {
                        report.Rejected = true;
                        report.Errors.Add($"{report.RejectedFrameList.Count} of {n} frames are unusable (limit {maxRejected:P0}); the AI output is not usable.");
                        return report;
                    }
                }
            }

            // 3. non-finite values -> interpolated from the nearest usable neighbours in time
            int nanFixed = 0;
            for (int c = 0; c < PoseChannels.Count; c++)
            {
                var good = new List<int>();
                for (int i = 0; i < n; i++) if (PoseChannels.IsFinite(channels[i][c])) good.Add(i);
                if (good.Count == n) continue;
                if (good.Count == 0)
                {
                    // nothing usable in the whole channel: the clamped measurement if there was one, else the rig's rest pose
                    {
                        // no usable measurement of this joint in any frame (an elbow flickering between impossible values): hold its typical clamped value instead of flickering
                        float typical = 0f;
                        if (c < RigDefinition.PartCount) { var vals = new List<float>(); for (int i = 0; i < n; i++) vals.Add(fallback[i][c]); vals.Sort(); typical = vals[n / 2]; }
                        for (int i = 0; i < n; i++) { channels[i][c] = typical; report.MarkCorrected(i); }
                    }
                    nanFixed += n;
                    continue;
                }
                var source = PoseChannels.Clone(channels);
                for (int i = 0; i < n; i++)
                {
                    if (PoseChannels.IsFinite(source[i][c])) continue;
                    channels[i][c] = InterpolateFromGood(source, c, i, good, loop);
                    if (!counted[i][c]) nanFixed++;
                    report.MarkCorrected(i);
                }
            }
            if (nanFixed > 0) { report.RepairedValues += nanFixed; report.Repairs.Add($"{nanFixed} non-finite or unusable value(s) interpolated from neighbouring frames"); report.AddCorrection("missing/unusable values (interpolated)", nanFixed, 0); }

            // 4. joint range (what is slightly outside is clamped)
            int clamped = 0;
            for (int i = 0; i < n; i++)
                for (int p = 0; p < RigDefinition.PartCount; p++)
                {
                    PoseLimits.Get(kind, p, out float lo, out float hi);
                    float v = Mathf.Clamp(channels[i][p], lo, hi);
                    if (v != channels[i][p]) { channels[i][p] = v; clamped++; report.MarkCorrected(i); }
                }
            if (clamped > 0) { report.RepairedValues += clamped; report.Repairs.Add($"{clamped} joint angle(s) clamped to the {kind ?? "default"} range"); report.AddCorrection("joint range clamp", clamped, 0); }

            // 5. impossible jumps between consecutive frames (pose-estimator flips): the excess is spread over the neighbouring frames
            float maxStep = settings.maxDegreesPerFrame;
            int limited = 0;
            var beforeSteps = PoseChannels.Clone(channels);
            for (int p = 0; p < RigDefinition.PartCount; p++) PoseConstraints.RelaxSteps(channels, p, loop, maxStep);
            for (int i = 0; i < n; i++)
                for (int p = 0; p < RigDefinition.PartCount; p++)
                    if (Mathf.Abs(channels[i][p] - beforeSteps[i][p]) > 0.25f) { limited++; report.MarkCorrected(i); }
            if (limited > 0) { report.RepairedValues += limited; report.Repairs.Add($"{limited} joint value(s) changed to keep joint speed below {maxStep:0} deg/frame"); report.AddCorrection($"flip limit ({maxStep:0} deg/frame)", limited, 0); }

            // 6. root movement and grounding: the renderer puts the feet on the ground line; the AI may not move the character vertically
            //    (except an intentional flight phase in Run) and horizontal drift is bounded.
            float maxRootX = Mathf.Max(1f, rig.width * 0.12f), maxRootY = Mathf.Max(1f, rig.height * 0.04f), maxHop = Mathf.Max(1f, rig.height * 0.12f);
            bool airborne = PoseLimits.AllowsAirborne(kind);
            int rootFixed = 0;
            for (int i = 0; i < n; i++)
            {
                float x = Mathf.Clamp(channels[i][PoseChannels.RootX], -maxRootX, maxRootX);
                float y = airborne ? Mathf.Clamp(channels[i][PoseChannels.RootY], -maxRootY, maxRootY) : 0f;
                float h = airborne ? Mathf.Clamp(channels[i][PoseChannels.Hop], 0f, maxHop) : 0f;
                if (x != channels[i][PoseChannels.RootX] || y != channels[i][PoseChannels.RootY] || h != channels[i][PoseChannels.Hop]) { rootFixed++; report.MarkCorrected(i); }
                channels[i][PoseChannels.RootX] = x; channels[i][PoseChannels.RootY] = y; channels[i][PoseChannels.Hop] = h;
            }
            if (rootFixed > 0)
            {
                report.RepairedValues += rootFixed;
                report.Repairs.Add($"{rootFixed} frame(s): root/hop restricted ({(airborne ? "bounded flight phase" : "no vertical movement: feet stay on the ground line")})");
                report.AddCorrection("root/hop range", rootFixed, rootFixed);
            }

            Check(rig, channels, kind, loop, report);

            if (report.RepairFraction > settings.rejectRepairFraction)
            {
                report.Rejected = true;
                report.Errors.Add($"{report.RepairFraction:P0} of the pose values had to be repaired (limit {settings.rejectRepairFraction:P0}); the AI output is not usable.");
            }
            return report;
        }

        /// <summary>Read-only checks on the (already repaired) sequence: connectivity, foot travel, closing loops.</summary>
        public static void Check(RigDefinition rig, float[][] channels, string kind, bool loop, PoseReport report)
        {
            var poses = PoseChannels.ToPoses(channels);
            int n = poses.Length;

            // Connectivity: a child's pivot must be the same point whether seen through the child or its parent
            // (guaranteed by the bone hierarchy; verified so a future non-hierarchical provider cannot break it). Torso <-> root and head <-> torso are covered.
            float worstGap = 0f;
            foreach (var pose in poses)
            {
                var world = RigKinematics.WorldMatrices(rig, pose);
                for (int p = 0; p < RigDefinition.PartCount; p++)
                {
                    int parent = SpriteRig.Parent[p];
                    if (parent < 0) continue;
                    Vector2 pv = rig.GetJoint(SpriteRig.Pivot[p]);
                    worstGap = Mathf.Max(worstGap, (world[p].Apply(pv.x, pv.y) - world[parent].Apply(pv.x, pv.y)).magnitude);
                }
            }
            if (worstGap > 0.01f) report.Errors.Add($"A child bone is detached from its parent by {worstGap:0.00} px (weapon/head/limb connection broken).");

            // The weapon pivot must sit on the hand in the rig itself.
            float weaponToHand = Vector2.Distance(rig.GetJoint(RigJoint.WeaponPivot), rig.GetJoint(RigJoint.HandNear));
            if (weaponToHand > 4f) report.Warnings.Add($"Rig: the weapon pivot is {weaponToHand:0.0} px away from the hand joint.");

            // Feet: travel between frames relative to the hips must stay within a leg length.
            float legLength = Vector2.Distance(rig.GetJoint(RigJoint.Hip), rig.GetJoint(RigJoint.KneeNear)) + Vector2.Distance(rig.GetJoint(RigJoint.KneeNear), rig.GetJoint(RigJoint.FootNear));
            var feet = new Vector2[n][];
            for (int i = 0; i < n; i++)
            {
                var j = RigKinematics.JointPositions(rig, poses[i], false, false);
                Vector2 hip = j[(int)RigJoint.Hip];
                feet[i] = new[] { j[(int)RigJoint.FootNear] - hip, j[(int)RigJoint.FootFar] - hip };
            }
            float worstStep = 0f; int worstFrame = 0;
            for (int i = 1; i < n + (loop ? 1 : 0); i++)
                for (int f = 0; f < 2; f++)
                {
                    float step = Vector2.Distance(feet[i % n][f], feet[i - 1][f]);
                    if (step > worstStep) { worstStep = step; worstFrame = i % n; }
                }
            if (worstStep > legLength * 0.9f)
                report.Warnings.Add($"Feet move {worstStep:0.0} px between frames {(worstFrame + n - 1) % n} and {worstFrame} (leg length {legLength:0.0} px).");

            // Loops: the step from the last frame to the first must not be much larger than the largest step inside the sequence.
            if (loop && n > 2)
            {
                float closing = 0f, inside = 0f;
                for (int p = 0; p < RigDefinition.PartCount; p++)
                {
                    closing = Mathf.Max(closing, Mathf.Abs(channels[0][p] - channels[n - 1][p]));
                    for (int i = 1; i < n; i++) inside = Mathf.Max(inside, Mathf.Abs(channels[i][p] - channels[i - 1][p]));
                }
                if (closing > inside * 1.5f + 5f) report.Warnings.Add($"Loop does not close: last->first changes a joint by {closing:0} deg (largest step inside: {inside:0} deg).");
            }
        }

        private static float InterpolateFromGood(float[][] ch, int c, int i, List<int> good, bool loop)
        {
            int n = ch.Length;
            int prev = -1, next = -1;
            for (int k = i - 1; k >= 0; k--) if (good.Contains(k)) { prev = k; break; }
            for (int k = i + 1; k < n; k++) if (good.Contains(k)) { next = k; break; }
            if (loop)
            {
                if (prev < 0) prev = good[good.Count - 1] - n;
                if (next < 0) next = good[0] + n;
            }
            if (prev < 0) return ch[next][c];
            if (next < 0) return ch[prev][c];
            float a = ch[((prev % n) + n) % n][c], b = ch[next % n][c];
            return Mathf.Lerp(a, b, (i - prev) / (float)(next - prev));
        }
    }
}
