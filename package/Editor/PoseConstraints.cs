using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Limits of the constraint stage for one animation type. Everything is expressed in the rig's own units (fractions of its leg length) or in degrees,
    /// so the same numbers work for any sprite size. <see cref="PoseConstraintSettings.limitScale"/> loosens or tightens them all.
    /// The values are chosen so that the procedural animations of the Rig method (valid by definition) pass unchanged.
    /// </summary>
    public sealed class ConstraintLimits
    {
        public float SpikeDegrees, SpikeRootPixels, JointStepDegrees, MinLegExtension, FootStep, RootXStep, RootYStep, RootXRange;
        /// <summary>A foot that rests on the ground may not skid further than this between two frames (px); a swinging foot is limited by <see cref="FootStep"/>.</summary>
        public float PlantedFootStep, HipStep;

        public static ConstraintLimits For(string kind, RigDefinition rig, PoseConstraintSettings s)
        {
            float leg = Mathf.Max(4f, PoseMetrics.LegLengthOf(rig));
            float k = Mathf.Clamp(s.limitScale, 0.5f, 3f);
            var l = new ConstraintLimits();
            float spikeFactor, minExt;
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "idle":
                    spikeFactor = 0.5f; l.JointStepDegrees = 14f; minExt = 0.92f; l.FootStep = 0.12f * leg; l.RootXStep = 0.6f; l.RootYStep = 0.5f; l.RootXRange = 1.5f;
                    l.PlantedFootStep = 0.08f * leg; break;
                case "walk":
                    spikeFactor = 0.8f; l.JointStepDegrees = 50f; minExt = 0.75f; l.FootStep = 0.55f * leg; l.RootXStep = 1.2f; l.RootYStep = 0.5f; l.RootXRange = 3f;
                    l.PlantedFootStep = 0.45f * leg; break;
                case "attack":
                    spikeFactor = 1.7f; l.JointStepDegrees = 110f; minExt = 0.5f; l.FootStep = 0.75f * leg; l.RootXStep = 3f; l.RootYStep = 0.5f; l.RootXRange = 8f;
                    l.PlantedFootStep = 0.6f * leg; break;
                default: // run and anything else
                    spikeFactor = 1.9f; l.JointStepDegrees = 85f; minExt = 0.44f; l.FootStep = 1.0f * leg; l.RootXStep = 2f; l.RootYStep = 4f; l.RootXRange = 4f;
                    l.PlantedFootStep = float.MaxValue; l.HipStep = 0.3f * leg; break;
            }
            l.SpikeDegrees = s.spikeDegrees * spikeFactor * k;
            l.SpikeRootPixels = s.spikeRootPixels * spikeFactor * k;
            l.JointStepDegrees *= k; l.FootStep *= k; l.PlantedFootStep *= k; l.HipStep *= k; l.RootXStep *= k; l.RootYStep *= k; l.RootXRange *= k;
            l.MinLegExtension = Mathf.Clamp(1f - (1f - minExt) * k, 0.3f, 1f);
            return l;
        }
    }

    /// <summary>
    /// The constraint stage: after the AI's motion was retargeted to the rig and before it is saved, it pulls poses back inside the limits they broke and leaves
    /// everything that is valid alone. It is NOT a procedural animation: no motion is added except where physics needs it (a swinging foot has to leave the ground),
    /// and every change is counted in the <see cref="PoseReport"/>.
    ///
    ///   spikes          a joint or root value that sticks out of both neighbouring frames (estimator glitch) is pulled back to the allowed overshoot
    ///   gait balance    walk/run: both legs swing about the same centre line (an estimator leaning one leg forward makes the stride lopsided)
    ///   joint speed     no joint turns faster than a person can between two frames (the excess is spread over neighbouring frames, not cut off)
    ///   leg extension   a leg is never folded into the body (hip-to-foot distance as a fraction of the leg)
    ///   foot step       a foot never jumps further between frames than the animation type allows (rendered distance, spread over neighbouring frames)
    ///   root step       the body does not lurch: limits on root sway and hop speed and range
    ///   foot lock       the planted foot is flat on the ground; a foot that moves is lifted off it (no skating)
    ///   weapon          the sword stays in the hand (child bone) and does not dig into the ground
    /// </summary>
    public static class PoseConstraints
    {
        private const float AngleTolerance = 1f, RootTolerance = 0.2f;   // below this a step did not really "correct" a frame

        private static readonly RigPart[] Measured =
        {
            RigPart.Body, RigPart.Head, RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.ArmFarUpper, RigPart.ArmFarLower,
            RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.LegFarUpper, RigPart.LegFarLower,
        };

        /// <param name="finalPass">After smoothing and loop closing: only the guarantees (limits) run again, not the corrections that shape the motion.</param>
        public static void Apply(RigDefinition rig, float[][] ch, string kind, bool loop, PoseCleanupSettings cleanup, PoseReport report, bool finalPass, bool shape = true)
        {
            var s = cleanup.constraints;
            if (s == null || !s.enabled || ch.Length < 3) return;
            var c = new Context(rig, ch, kind, loop, s, report, finalPass);
            report.Limits = new PoseLimitsInfo
            {
                FootStep = c.Limits.FootStep, RootXStep = c.Limits.RootXStep, SpikeDegrees = c.Limits.SpikeDegrees,
                JointStepDegrees = c.Limits.JointStepDegrees, MinLegExtension = c.Limits.MinLegExtension,
            };

            if (!finalPass)
            {
                c.Step("single-frame spikes", c.Spikes);
                c.Step("gait balance (lopsided stride)", c.GaitBalance);
                c.Step("joint speed limit", c.JointSteps);
            }
            // After smoothing (which flattens small movements) the idle must still be visible.
            if (finalPass && shape) c.Step("idle sway made visible (coherent AI movement amplified)", c.IdleMotion);
            // Ranges first: the steps below interpolate between valid angles, so they cannot leave the range again.
            c.Step("joint ranges", c.ClampRanges);
            c.Step("leg extension limit", c.LegExtension);
            c.Step("root movement limit", c.RootSteps);   // before the feet: the root position is part of where a foot is rendered
            c.Step("hip step limit (flight phase)", c.HipSteps);
            c.Step("foot step limit", c.FootSteps, false);   // moves the derived foot angles itself
            c.Step("foot lock (planted sole level, planted foot not skidding)", c.FootLock);   // after the foot step: smoothing re-tilts feet, so this runs in both passes
            c.Step("weapon kept in hand and above ground", c.Weapon);
        }

        /// <summary>
        /// Only the guarantees (joint ranges, leg extension, root and foot step limits, weapon) on poses that were already finalised, e.g. after the frame count
        /// or the intensity of a build changed them. Never shapes the motion (no spike removal, gait balance, idle amplification, foot lock).
        /// </summary>
        public static void Enforce(RigDefinition rig, float[][] ch, string kind, bool loop, PoseCleanupSettings cleanup, PoseReport report) =>
            Apply(rig, ch, kind, loop, cleanup, report, true, false);

        /// <summary>
        /// Limits the change of one channel between two consecutive frames (cyclic for loops) by spreading the excess over the neighbouring frames, so a fast
        /// movement becomes a slightly wider, slower one instead of being cut off. The first and last frame of a one-shot stay where they are.
        /// </summary>
        public static void RelaxSteps(float[][] ch, int c, bool loop, float limit)
        {
            int n = ch.Length, pairs = loop ? n : n - 1;
            bool anchorEnds = !loop;
            for (int it = 0; it < 400; it++)
            {
                bool any = false;
                for (int a = 0; a < pairs; a++)
                {
                    int b = (a + 1) % n;
                    float d = ch[b][c] - ch[a][c], ad = Mathf.Abs(d);
                    if (ad <= limit * 1.0005f) continue;
                    any = true;
                    float wa = anchorEnds && a == 0 ? 0f : 1f, wb = anchorEnds && b == n - 1 ? 0f : 1f;
                    if (wa + wb <= 0f) continue;
                    float move = (ad - limit) * 0.5f * Mathf.Sign(d);
                    ch[a][c] += move * wa / (wa + wb);
                    ch[b][c] -= move * wb / (wa + wb);
                }
                if (!any) break;
            }
        }

        // ------------------------------------------------------------------------------------------------------------------------------
        private sealed class Context
        {
            public readonly RigDefinition Rig;
            public readonly float[][] Ch;
            public readonly string Kind;
            public readonly bool Loop, FinalPass;
            public readonly int N;
            public readonly PoseConstraintSettings S;
            public readonly ConstraintLimits Limits;
            public readonly PoseReport Report;
            public readonly OpenPoseMapperSettings Mapper = new OpenPoseMapperSettings();
            public readonly float LegLength;
            public readonly bool Grounded;

            public Context(RigDefinition rig, float[][] ch, string kind, bool loop, PoseConstraintSettings s, PoseReport report, bool finalPass)
            {
                Rig = rig; Ch = ch; Kind = (kind ?? "").ToLowerInvariant(); Loop = loop; S = s; Report = report; FinalPass = finalPass; N = ch.Length;
                Limits = ConstraintLimits.For(Kind, rig, s);
                LegLength = PoseMetrics.LegLengthOf(rig);
                Grounded = !PoseLimits.AllowsAirborne(Kind);
                report.BeginFrames(N);
            }

            // ---- bookkeeping
            public void Step(string name, Action op, bool syncDerived = true)
            {
                float[][] before = PoseChannels.Clone(Ch);
                op();
                if (syncDerived) SyncDerived(before);
                int values = 0, frames = 0;
                for (int i = 0; i < N; i++)
                {
                    bool touched = false;
                    foreach (RigPart p in Measured) if (Mathf.Abs(Ch[i][(int)p] - before[i][(int)p]) > AngleTolerance) { values++; touched = true; }
                    for (int r = PoseChannels.RootX; r < PoseChannels.Count; r++) if (Mathf.Abs(Ch[i][r] - before[i][r]) > RootTolerance) { values++; touched = true; }
                    if (touched) { frames++; Report.MarkCorrected(i); }
                }
                Report.AddCorrection(name, values, frames);
            }

            // Feet and weapon are derived from the limbs (OpenPose has neither): keep them consistent with what a step changed.
            private void SyncDerived(float[][] before)
            {
                float follow = OpenPoseMapper.WeaponFollow(Kind, Mapper);
                for (int i = 0; i < N; i++)
                {
                    float[] a = Ch[i], b = before[i];
                    float dN = (a[(int)RigPart.LegNearUpper] - b[(int)RigPart.LegNearUpper]) + (a[(int)RigPart.LegNearLower] - b[(int)RigPart.LegNearLower]);
                    float dF = (a[(int)RigPart.LegFarUpper] - b[(int)RigPart.LegFarUpper]) + (a[(int)RigPart.LegFarLower] - b[(int)RigPart.LegFarLower]);
                    a[(int)RigPart.FootNear] += -Mapper.footFlatten * dN;
                    a[(int)RigPart.FootFar] += -Mapper.footFlatten * dF;
                    float dArm = (a[(int)RigPart.Body] - b[(int)RigPart.Body]) + (a[(int)RigPart.ArmNearUpper] - b[(int)RigPart.ArmNearUpper]) + (a[(int)RigPart.ArmNearLower] - b[(int)RigPart.ArmNearLower]);
                    a[(int)RigPart.Weapon] += (follow - 1f) * dArm;
                }
            }

            // ---- 1. spikes: a value that disagrees with what its neighbours predict by more than the allowed overshoot is pulled back to that overshoot
            public void Spikes()
            {
                if (N < 4) return;
                foreach (RigPart p in Measured) DeSpike((int)p, Limits.SpikeDegrees);
                DeSpike(PoseChannels.RootX, Limits.SpikeRootPixels);
                DeSpike(PoseChannels.RootY, Limits.SpikeRootPixels);
                DeSpike(PoseChannels.Hop, Limits.SpikeRootPixels);
            }

            private void DeSpike(int c, float threshold)
            {
                // Greedy: fix the worst spike, predict again (a spike contaminates the prediction of the frames two steps away), until none is left.
                for (int guard = 0; guard < 2 * N; guard++)
                {
                    int worst = -1; float worstExcess = 0f, worstPred = 0f, worstSlack = 1f;
                    for (int i = 0; i < N; i++)
                    {
                        if (!Predict(c, i, out float pred, out float slack)) continue;
                        float excess = Mathf.Abs(Ch[i][c] - pred) - threshold * slack;
                        if (excess > worstExcess) { worst = i; worstExcess = excess; worstPred = pred; worstSlack = slack; }
                    }
                    if (worst < 0) return;
                    Ch[worst][c] = worstPred + Mathf.Sign(Ch[worst][c] - worstPred) * threshold * worstSlack;
                }
            }

            // Predicts frame i from its neighbours: cubic through i-2, i-1, i+1, i+2 (exact for smooth motion, so the peaks of a swing are not mistaken for spikes);
            // at the ends of a one-shot, the mean of both neighbours with a more generous slack (its error at a genuine peak is larger).
            private bool Predict(int c, int i, out float pred, out float slack)
            {
                pred = 0f; slack = 1f;
                if (Loop)
                {
                    float m2 = Ch[(i - 2 + 2 * N) % N][c], m1 = Ch[(i - 1 + N) % N][c], p1 = Ch[(i + 1) % N][c], p2 = Ch[(i + 2) % N][c];
                    if (N >= 5) { pred = (-m2 + 4f * m1 + 4f * p1 - p2) / 6f; return true; }
                    pred = 0.5f * (m1 + p1); slack = 1.5f; return true;
                }
                if (i >= 2 && i <= N - 3) { pred = (-Ch[i - 2][c] + 4f * Ch[i - 1][c] + 4f * Ch[i + 1][c] - Ch[i + 2][c]) / 6f; return true; }
                if (i >= 1 && i <= N - 2) { pred = 0.5f * (Ch[i - 1][c] + Ch[i + 1][c]); slack = 1.5f; return true; }
                return false;
            }

            // ---- 2. gait balance: a walking/running person swings both legs about the same centre line
            public void GaitBalance()
            {
                if (!Loop || N < 4 || (Kind != "walk" && Kind != "run")) return;
                if (S.gaitSymmetry <= 0f) return;
                int tn = (int)RigPart.LegNearUpper, tf = (int)RigPart.LegFarUpper, sn = (int)RigPart.LegNearLower, sf = (int)RigPart.LegFarLower;
                // Only a lopsided stride is touched: the two thighs swing about centre lines that differ by more than 8 degrees (full effect from 24 degrees).
                // A gait whose legs already swing about the same line is the AI's own and stays as it is.
                float k = Mathf.Clamp01((Mathf.Abs(Mean(tn) - Mean(tf)) - 8f) / 16f);   // how lopsided: 0 = not at all, 1 = fully
                float w = S.gaitSymmetry * k;
                if (k <= 0f) return;
                // The master leg is the one that really strides (how far its foot travels): it sets the waveform of both legs.
                bool nearMaster = FootTravel(RigJoint.FootNear) >= FootTravel(RigJoint.FootFar);
                int mT = nearMaster ? tn : tf, mS = nearMaster ? sn : sf, oT = nearMaster ? tf : tn, oS = nearMaster ? sf : sn;
                float meanThigh = 0.5f * (Mean(tn) + Mean(tf));   // both thighs swing about the common centre line of the two measured legs
                float masterThighMean = Mean(mT), otherThighMean = Mean(oT), masterShin = Mean(mS), otherShin = Mean(oS);

                var master = new float[N][];
                for (int i = 0; i < N; i++) master[i] = new[] { Ch[i][mT], Ch[i][mS] };
                for (int i = 0; i < N; i++)
                {
                    // the other leg does what the master did half a cycle earlier (a walk/run is mirror-symmetric in time), blended with its own measurement
                    float t = (i + N * 0.5f) % N;
                    int a = Mathf.FloorToInt(t) % N, b = (a + 1) % N;
                    float f = t - Mathf.Floor(t);
                    float symThigh = Mathf.Lerp(master[a][0], master[b][0], f) - masterThighMean + meanThigh;
                    float symShin = Mathf.Lerp(master[a][1], master[b][1], f);
                    float ownThigh = Mathf.Lerp(Ch[i][oT], Ch[i][oT] - otherThighMean + meanThigh, k);
                    float ownShin = Mathf.Lerp(Ch[i][oS], Ch[i][oS] - otherShin + masterShin, k);
                    Ch[i][oT] = Mathf.Lerp(ownThigh, symThigh, w);
                    Ch[i][oS] = Mathf.Lerp(ownShin, symShin, w);
                    Ch[i][mT] = Mathf.Lerp(master[i][0], master[i][0] - masterThighMean + meanThigh, k);
                }
            }

            private float Mean(int c) { float m = 0; for (int i = 0; i < N; i++) m += Ch[i][c]; return m / N; }

            // Horizontal range of a foot over the sequence (ungrounded: the stride, not the bobbing).
            private float FootTravel(RigJoint foot)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = 0; i < N; i++)
                {
                    float x = RigKinematics.JointPositions(Rig, PoseChannels.ToPose(Ch[i]), false, false)[(int)foot].x;
                    lo = Mathf.Min(lo, x); hi = Mathf.Max(hi, x);
                }
                return hi - lo;
            }

            // ---- 2b. idle: make the AI's own sway big enough to be seen
            // A 48-pixel sprite needs a few degrees before its upper body moves a whole pixel, and the generated idle video barely sways (its keypoints
            // wobble by less than a degree or two, mostly estimator noise). The coherent part of that sway (the first harmonics of the loop, noise removed)
            // is amplified, as little as needed, until the neck and hands move about a pixel. Nothing is added that the AI did not do: same joints, same
            // timing, same coupling; only the size changes, and the gain is reported.
            public void IdleMotion()
            {
                if (!Loop || Kind != "idle" || !S.idleMotion || N < 6) return;
                float target = S.idleMinMotionPixels, current = PoseMetrics.VisibleMotionOf(Rig, Ch);
                Report.VisibleMotionBefore = current;
                if (current >= target) return;
                RigPart[] parts = { RigPart.Body, RigPart.Head, RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.ArmFarUpper, RigPart.ArmFarLower };
                var mean = new float[parts.Length]; var coherent = new float[parts.Length][];
                int harmonics = Mathf.Min(2, N / 2 - 1);
                for (int k = 0; k < parts.Length; k++)
                {
                    int c = (int)parts[k];
                    mean[k] = Mean(c);
                    coherent[k] = new float[N];
                    for (int h = 1; h <= harmonics; h++)
                    {
                        float a = 0, b = 0;
                        for (int t = 0; t < N; t++)
                        {
                            float ang = 2f * Mathf.PI * h * t / N, x = Ch[t][c] - mean[k];
                            a += x * Mathf.Cos(ang); b += x * Mathf.Sin(ang);
                        }
                        a *= 2f / N; b *= 2f / N;
                        float weight = h == 1 ? 1f : 0.5f;   // slow movement counts fully, the second harmonic (a 4-frame wobble) only half
                        for (int t = 0; t < N; t++) coherent[k][t] += weight * (a * Mathf.Cos(2f * Mathf.PI * h * t / N) + b * Mathf.Sin(2f * Mathf.PI * h * t / N));
                    }
                }
                float VisibleAt(float gain)
                {
                    var tmp = PoseChannels.Clone(Ch);
                    for (int k = 0; k < parts.Length; k++)
                    {
                        PoseLimits.Get(Kind, (int)parts[k], out float pLo, out float pHi);
                        for (int t = 0; t < N; t++) tmp[t][(int)parts[k]] = Mathf.Clamp(mean[k] + gain * coherent[k][t], pLo, pHi);   // as it will be after the range limit
                    }
                    return PoseMetrics.VisibleMotionOf(Rig, tmp);
                }
                float lo = 1f, hi = Mathf.Max(1f, S.idleMaxGain), gain = hi;
                if (VisibleAt(hi) >= target)
                    for (int it = 0; it < 14; it++)
                    {
                        float mid = 0.5f * (lo + hi);
                        if (VisibleAt(mid) >= target) hi = mid; else lo = mid;
                        gain = hi;
                    }
                for (int k = 0; k < parts.Length; k++) for (int t = 0; t < N; t++) Ch[t][(int)parts[k]] = mean[k] + gain * coherent[k][t];
                Report.IdleMotionGain = gain;
                Report.Repairs.Add($"idle: the AI's sway moved the upper body only {current:0.0} px; its coherent part was amplified x{gain:0.0} to about {target:0.0} px");
            }

            // ---- 3. joint speed: spread the excess over neighbouring frames instead of cutting the movement off
            public void JointSteps()
            {
                foreach (RigPart p in Measured) RelaxScalar((int)p, Limits.JointStepDegrees);
            }

            public void RelaxScalar(int c, float limit) => RelaxSteps(Ch, c, Loop, limit);

            // ---- 4. leg extension: never fold the leg into the body (reduce the knee bend of the offending frame only)
            public void LegExtension()
            {
                for (int i = 0; i < N; i++)
                {
                    LimitExtension(i, RigPart.LegNearUpper, RigPart.LegNearLower, RigJoint.KneeNear, RigJoint.FootNear);
                    LimitExtension(i, RigPart.LegFarUpper, RigPart.LegFarLower, RigJoint.KneeFar, RigJoint.FootFar);
                }
            }

            private float Extension(int i, RigJoint knee, RigJoint foot)
            {
                Vector2[] j = RigKinematics.JointPositions(Rig, PoseChannels.ToPose(Ch[i]), false, false);
                float full = Vector2.Distance(Rig.GetJoint(RigJoint.Hip), Rig.GetJoint(knee)) + Vector2.Distance(Rig.GetJoint(knee), Rig.GetJoint(foot));
                return Vector2.Distance(j[(int)RigJoint.Hip], j[(int)foot]) / Mathf.Max(0.01f, full);
            }

            private void LimitExtension(int i, RigPart upper, RigPart lower, RigJoint knee, RigJoint foot)
            {
                if (Extension(i, knee, foot) >= Limits.MinLegExtension) return;
                float shin = Ch[i][(int)lower], lo = 0f, hi = 1f;   // scale of the knee bend: 1 = as is, 0 = straight leg
                for (int it = 0; it < 14; it++)
                {
                    float mid = 0.5f * (lo + hi);
                    Ch[i][(int)lower] = shin * mid;
                    if (Extension(i, knee, foot) >= Limits.MinLegExtension) lo = mid; else hi = mid;
                }
                Ch[i][(int)lower] = shin * lo;
            }

            // ---- 5. foot step: rendered foot displacement between frames, spread over neighbouring frames.
            //         A foot that rests on the ground (it is the lowest one) may not skid as fast as a foot that swings: that is the foot lock.
            public void FootSteps()
            {
                // The two legs share the grounding shift, so one leg's correction can move the other leg's rendered foot: repeat until both are within the limit.
                for (int round = 0; round < 6; round++)
                {
                    RelaxFoot(RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.FootNear, RigJoint.FootNear, RigJoint.FootFar);
                    RelaxFoot(RigPart.LegFarUpper, RigPart.LegFarLower, RigPart.FootFar, RigJoint.FootFar, RigJoint.FootNear);
                    if (WorstFootStepRatio() <= 1.002f) break;
                }
            }

            private Vector2 FootAt(int i, RigJoint foot) => RigKinematics.JointPositions(Rig, PoseChannels.ToPose(Ch[i]), false, true)[(int)foot];

            // The foot rests on the ground when it is the lowest of the two (within 0.6 px).
            private const float PlantedTolerance = 0.6f;

            private float PairLimit(bool planted) => planted && S.footLock && Grounded ? Mathf.Min(Limits.FootStep, Limits.PlantedFootStep) : Limits.FootStep;

            // Largest step / limit over both feet and all pairs (<= 1 means every foot is within its limit).
            private float WorstFootStepRatio()
            {
                float worst = 0f;
                var near = new Vector2[N]; var far = new Vector2[N];
                for (int i = 0; i < N; i++) { near[i] = FootAt(i, RigJoint.FootNear); far[i] = FootAt(i, RigJoint.FootFar); }
                for (int a = 0; a < (Loop ? N : N - 1); a++)
                {
                    int b = (a + 1) % N;
                    bool plantedN = near[a].y >= far[a].y - PlantedTolerance && near[b].y >= far[b].y - PlantedTolerance;
                    bool plantedF = far[a].y >= near[a].y - PlantedTolerance && far[b].y >= near[b].y - PlantedTolerance;
                    worst = Mathf.Max(worst, Vector2.Distance(near[a], near[b]) / PairLimit(plantedN), Vector2.Distance(far[a], far[b]) / PairLimit(plantedF));
                }
                return worst;
            }

            private void RelaxFoot(RigPart upper, RigPart lower, RigPart footPart, RigJoint foot, RigJoint otherFoot)
            {
                var pos = new Vector2[N]; var other = new Vector2[N];
                for (int i = 0; i < N; i++) { pos[i] = FootAt(i, foot); other[i] = FootAt(i, otherFoot); }
                int pairs = Loop ? N : N - 1;
                // which pairs have this foot on the ground (decided once: the relaxation only moves feet a little)
                var planted = new bool[N];
                for (int a = 0; a < pairs; a++)
                {
                    int b = (a + 1) % N;
                    planted[a] = pos[a].y >= other[a].y - PlantedTolerance && pos[b].y >= other[b].y - PlantedTolerance;
                }
                bool anchorEnds = !Loop;
                int u = (int)upper, l = (int)lower;
                for (int it = 0; it < 300; it++)
                {
                    bool any = false;
                    for (int a = 0; a < pairs; a++)
                    {
                        int b = (a + 1) % N;
                        float limit = PairLimit(planted[a]);
                        float d = Vector2.Distance(pos[a], pos[b]);
                        if (d <= limit * 1.001f) continue;
                        any = true;
                        float wa = anchorEnds && a == 0 ? 0f : 1f, wb = anchorEnds && b == N - 1 ? 0f : 1f;
                        if (wa + wb <= 0f) continue;
                        float t = (1f - limit / d) * 0.6f;   // fraction of the angle difference to give up in total
                        foreach (int ci in new[] { u, l, (int)footPart })
                        {
                            float va = Ch[a][ci], vb = Ch[b][ci];
                            Ch[a][ci] = va + (vb - va) * t * wa / (wa + wb);
                            Ch[b][ci] = vb + (va - vb) * t * wb / (wa + wb);
                        }
                        pos[a] = FootAt(a, foot); pos[b] = FootAt(b, foot);
                    }
                    if (!any) break;
                }
            }

            // ---- 6. root movement: sway/lurch limits, bounded range
            public void RootSteps()
            {
                RelaxScalar(PoseChannels.RootX, Limits.RootXStep);
                RelaxScalar(PoseChannels.RootY, Limits.RootYStep);
                RelaxScalar(PoseChannels.Hop, Limits.RootYStep);
                for (int i = 0; i < N; i++) Ch[i][PoseChannels.RootX] = Mathf.Clamp(Ch[i][PoseChannels.RootX], -Limits.RootXRange, Limits.RootXRange);
            }

            // ---- 6b. hip step: in a flight phase (run) the rendered hip must not jump either. The renderer places the hip from root, hop and the grounding shift of
            //          the legs; the hop of the two frames is pulled together until the rendered step is within the limit.
            public void HipSteps()
            {
                if (Grounded || Limits.HipStep <= 0f) return;
                int pairs = Loop ? N : N - 1;
                bool anchorEnds = !Loop;
                for (int round = 0; round < 40; round++)
                {
                    var hip = new Vector2[N];
                    for (int i = 0; i < N; i++) hip[i] = RigKinematics.JointPositions(Rig, PoseChannels.ToPose(Ch[i]), false, true)[(int)RigJoint.Hip];
                    bool any = false;
                    for (int a = 0; a < pairs; a++)
                    {
                        int b = (a + 1) % N;
                        float d = Vector2.Distance(hip[a], hip[b]);
                        if (d <= Limits.HipStep * 1.001f) continue;
                        any = true;
                        float wa = anchorEnds && a == 0 ? 0f : 1f, wb = anchorEnds && b == N - 1 ? 0f : 1f;
                        if (wa + wb <= 0f) continue;
                        float t = (1f - Limits.HipStep / d) * 0.6f;
                        float ha = Ch[a][PoseChannels.Hop], hb = Ch[b][PoseChannels.Hop];
                        Ch[a][PoseChannels.Hop] = ha + (hb - ha) * t * wa / (wa + wb);
                        Ch[b][PoseChannels.Hop] = hb + (ha - hb) * t * wb / (wa + wb);
                    }
                    if (!any) break;
                }
            }

            // ---- 7. foot lock, the part that works on the foot angle: a planted foot keeps its sole within 5 degrees of the ground
            //         (the skid limit of planted feet is part of the foot step limit above)
            public void FootLock()
            {
                if (!S.footLock || S.footLockStrength <= 0f || !Grounded) return;
                FlattenPlantedFeet();
            }

            private void FootHeights(int i, out Vector2 near, out Vector2 far)
            {
                Vector2[] j = RigKinematics.JointPositions(Rig, PoseChannels.ToPose(Ch[i]), false, true);
                near = j[(int)RigJoint.FootNear]; far = j[(int)RigJoint.FootFar];
            }

            // The foot (or feet) resting on the ground is flat on it; a foot hovering above is left as the AI/derivation made it.
            private void FlattenPlantedFeet()
            {
                for (int i = 0; i < N; i++)
                {
                    FootHeights(i, out Vector2 n, out Vector2 f);
                    float low = Mathf.Max(n.y, f.y);
                    FlattenFoot(i, RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.FootNear, low - n.y);
                    FlattenFoot(i, RigPart.LegFarUpper, RigPart.LegFarLower, RigPart.FootFar, low - f.y);
                }
            }

            private void FlattenFoot(int i, RigPart upper, RigPart lower, RigPart foot, float heightAboveGround)
            {
                float weight = Mathf.Clamp01(1f - heightAboveGround / 1.5f) * S.footLockStrength;
                if (weight <= 0f) return;
                // A planted foot may roll a little (heel strike, toe push) but its sole stays within 5 degrees of the ground (a more tilted sole touches the ground row with one corner pixel only, which the renderer drops: the character would hover by a pixel): tilt it back only beyond that.
                const float maxTilt = 5f;
                float flat = -(Ch[i][(int)upper] + Ch[i][(int)lower]);   // foot angle that makes the sole level
                float tilt = Ch[i][(int)foot] - flat;                    // current tilt of the sole against the ground
                float excess = Mathf.Abs(tilt) - maxTilt;
                if (excess > 0f) Ch[i][(int)foot] -= Mathf.Sign(tilt) * excess * weight;
            }

            // ---- 8. weapon: it is a child bone of the hand (verified by PoseValidator.Check); here it is also kept out of the ground
            public void Weapon()
            {
                if (!Grounded) return;
                for (int i = 0; i < N; i++)
                {
                    if (TipBelowGround(i) <= 1f) continue;
                    float original = Ch[i][(int)RigPart.Weapon], lo = 0f, hi = 1f;
                    for (int it = 0; it < 12; it++)
                    {
                        float mid = 0.5f * (lo + hi);
                        Ch[i][(int)RigPart.Weapon] = original * mid;
                        if (TipBelowGround(i) <= 1f) lo = mid; else hi = mid;
                    }
                    Ch[i][(int)RigPart.Weapon] = original * lo;
                }
            }

            private float TipBelowGround(int i) => RigKinematics.JointPositions(Rig, PoseChannels.ToPose(Ch[i]), false, true)[(int)RigJoint.WeaponTip].y - Rig.groundY;

            public void ClampRanges()
            {
                for (int i = 0; i < N; i++)
                    for (int p = 0; p < RigDefinition.PartCount; p++)
                    {
                        PoseLimits.Get(Kind, p, out float lo, out float hi);
                        Ch[i][p] = Mathf.Clamp(Ch[i][p], lo, hi);
                    }
            }
        }
    }
}
