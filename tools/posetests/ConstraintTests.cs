using System.Linq;
using AISpriteAnimation;
using UnityEngine;

/// <summary>Tests of the constraint stage (PoseConstraints) and of the validation changes that go with it: synthetic faults with known answers, and real SDPose clips.</summary>
public static class ConstraintTests
{
    private static PoseCleanupSettings Settings() => new PoseCleanupSettings();

    private static float[][] Procedural(string kind, int n = 8) => PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses(kind, n, n, 1f));

    private static PoseCleanup.Result Clean(RigDefinition rig, float[][] ch, string kind, PoseCleanupSettings s = null) =>
        PoseCleanup.Run(rig, PoseChannels.ToPoses(ch), kind, kind != "attack", s ?? Settings());

    private static float MaxJointChange(float[][] a, float[][] b)
    {
        float worst = 0;
        for (int i = 0; i < a.Length; i++) for (int p = 0; p < RigDefinition.PartCount; p++)
        {
            var part = (RigPart)p;
            if (part == RigPart.Hair || part == RigPart.FootNear || part == RigPart.FootFar || part == RigPart.Weapon) continue;
            worst = Mathf.Max(worst, Mathf.Abs(a[i][p] - b[i][p]));
        }
        return worst;
    }

    public static void Run(RigDefinition rig, Action<bool, string> check, string fixtureDir)
    {
        Console.WriteLine("== Constraint stage");

        // ---- valid motion is left alone: the procedural animations (valid by definition) pass the constraint stage unchanged
        foreach (string kind in new[] { "idle", "walk", "run", "attack" })
        {
            var src = Procedural(kind);
            var ch = PoseChannels.Clone(src);
            var rep = new PoseReport { Kind = kind };
            var s = Settings();
            s.constraints.idleMotion = false;   // amplification of a too small idle is not a correction of invalid motion
            PoseConstraints.Apply(rig, ch, kind, kind != "attack", s, rep, false);
            PoseConstraints.Apply(rig, ch, kind, kind != "attack", s, rep, true);
            check(rep.Corrections.Count(c => c.values > 0) == 0 && MaxJointChange(src, ch) < 0.5f,
                  $"{kind}: the procedural animation passes the constraint stage unchanged (fired: {string.Join(", ", rep.Corrections.Where(c => c.values > 0).Select(c => c.name + " " + c.values))}; max change {MaxJointChange(src, ch):0.0} deg)");
        }

        // ---- a single-frame spike is pulled back, its neighbours stay
        {
            var src = Procedural("walk");
            float before = src[3][(int)RigPart.ArmFarUpper];
            src[3][(int)RigPart.ArmFarUpper] += 70f;
            var r = Clean(rig, src, "walk");
            var fin = PoseChannels.FromPoses(r.Poses);
            check(fin[3][(int)RigPart.ArmFarUpper] - before < 25f, $"spike: a +70 deg arm glitch in one frame is pulled back to {fin[3][(int)RigPart.ArmFarUpper] - before:0} deg above the real pose");
            check(Mathf.Abs(fin[2][(int)RigPart.ArmFarUpper] - Procedural("walk")[2][(int)RigPart.ArmFarUpper]) < 5f, "spike: the neighbouring frames keep their pose");
            check(r.Report.CorrectedFrames[3], "spike: the report marks frame 3 as corrected");
        }
        {
            // a genuine fast swing (sinusoid, +-50 deg) is not mistaken for spikes
            var src = Procedural("run");
            for (int i = 0; i < 8; i++) src[i][(int)RigPart.ArmFarUpper] = 50f * Mathf.Sin(2f * Mathf.PI * i / 8f);
            var r = Clean(rig, src, "run");
            var fin = PoseChannels.FromPoses(r.Poses);
            float lost = 0; for (int i = 0; i < 8; i++) lost = Mathf.Max(lost, Mathf.Abs(fin[i][(int)RigPart.ArmFarUpper] - src[i][(int)RigPart.ArmFarUpper]));
            check(lost < 6f, $"spike: a smooth +-50 deg swing keeps its peaks (largest change {lost:0.0} deg)");
        }

        // ---- foot jump (the 19 px jump of the attack): two legs flip in one frame
        {
            var src = Procedural("attack");
            src[5][(int)RigPart.LegNearUpper] += 95f; src[5][(int)RigPart.LegFarUpper] -= 80f;
            var settings = Settings();
            var before = PoseMetrics.Measure(rig, ValidatedCopy(rig, src, "attack"), false);
            var r = Clean(rig, src, "attack", settings);
            var limits = ConstraintLimits.For("attack", rig, settings.constraints);
            check(before.MaxFootStep > limits.FootStep * 1.05f, $"foot jump: the injected fault is a real jump ({before.MaxFootStep:0.0} px, limit {limits.FootStep:0.0})");
            check(r.Report.FinalMetrics.MaxFootStep <= limits.FootStep * 1.01f, $"foot jump: after the constraints no foot moves more than {limits.FootStep:0.0} px per frame (is {r.Report.FinalMetrics.MaxFootStep:0.0})");
            var fin = PoseChannels.FromPoses(r.Poses);
            check(fin[5][(int)RigPart.LegNearUpper] - src[5][(int)RigPart.LegNearUpper] < -10f || r.Report.Corrections.Any(c => c.name.StartsWith("foot step") && c.values > 0), "foot jump: the jump was spread over neighbouring frames, not cut off");
        }

        // ---- legs folded into the body
        {
            var src = Procedural("walk");
            src[2][(int)RigPart.LegNearUpper] = 50f; src[2][(int)RigPart.LegNearLower] = -150f;
            var r = Clean(rig, src, "walk");
            var limits = ConstraintLimits.For("walk", rig, Settings().constraints);
            check(r.Report.FinalMetrics.MinLegExtension >= limits.MinLegExtension - 0.02f, $"leg extension: a folded walking leg is opened up to {limits.MinLegExtension:P0} (is {r.Report.FinalMetrics.MinLegExtension:P0})");
            var run = Procedural("run");
            var rr = Clean(rig, run, "run");
            check(rr.Report.FinalMetrics.MinLegExtension <= 0.6f, $"leg extension: the deep knee flexion of a run is preserved (min extension {rr.Report.FinalMetrics.MinLegExtension:P0})");
        }

        // ---- root lurch
        {
            var src = Procedural("walk");
            for (int i = 4; i < 8; i++) src[i][PoseChannels.RootX] += 7f;
            var settings = Settings();
            var r = Clean(rig, src, "walk", settings);
            var fin = PoseChannels.FromPoses(r.Poses);
            float maxStep = 0; for (int i = 0; i < 8; i++) maxStep = Mathf.Max(maxStep, Mathf.Abs(fin[(i + 1) % 8][PoseChannels.RootX] - fin[i][PoseChannels.RootX]));
            var limits = ConstraintLimits.For("walk", rig, settings.constraints);
            check(maxStep <= limits.RootXStep * 1.05f, $"root: a 7 px lurch becomes steps of at most {limits.RootXStep:0.0} px (is {maxStep:0.00})");
        }

        // ---- lopsided stride: one leg lives 40 deg in front of the other, with a gentle swing
        {
            var src = Procedural("walk");
            for (int i = 0; i < 8; i++) { src[i][(int)RigPart.LegNearUpper] -= 18f; src[i][(int)RigPart.LegFarUpper] += 18f; }
            float Mean(float[][] c, RigPart p) => c.Average(f => f[(int)p]);
            var r = Clean(rig, src, "walk");
            var fin = PoseChannels.FromPoses(r.Poses);
            float diffBefore = Mathf.Abs(Mean(src, RigPart.LegNearUpper) - Mean(src, RigPart.LegFarUpper)), diffAfter = Mathf.Abs(Mean(fin, RigPart.LegNearUpper) - Mean(fin, RigPart.LegFarUpper));
            check(diffAfter < 8f && diffBefore > 30f, $"gait: legs that swung about centre lines {diffBefore:0} deg apart now differ by {diffAfter:0.0} deg");
            float range(float[][] c, RigPart p) => c.Max(f => f[(int)p]) - c.Min(f => f[(int)p]);
            check(range(fin, RigPart.LegNearUpper) > 0.7f * range(src, RigPart.LegNearUpper), "gait: the stride is still there (leg swing range kept)");
        }

        // ---- wrap-around and impossible angles
        {
            var src = Procedural("idle");
            for (int i = 0; i < 8; i++) src[i][(int)RigPart.ArmFarLower] = 30f - 360f;   // 30 deg, written as -330
            var r = Clean(rig, src, "idle");
            var fin = PoseChannels.FromPoses(r.Poses);
            check(Mathf.Abs(fin[0][(int)RigPart.ArmFarLower] - 30f) < 12f, $"wrap-around: -330 deg is read as 30 deg (is {fin[0][(int)RigPart.ArmFarLower]:0})");
            var src2 = Procedural("idle");
            for (int i = 0; i < 8; i++) src2[i][(int)RigPart.ArmFarLower] = (i % 2 == 0) ? 134f : -137f;   // an elbow flickering between two impossible values
            var r2 = Clean(rig, src2, "idle");
            var fin2 = PoseChannels.FromPoses(r2.Poses);
            float flick = 0; for (int i = 0; i < 8; i++) flick = Mathf.Max(flick, Mathf.Abs(fin2[(i + 1) % 8][(int)RigPart.ArmFarLower] - fin2[i][(int)RigPart.ArmFarLower]));
            check(flick < 8f, $"impossible angles: a flickering impossible elbow does not flicker in the result (largest step {flick:0.0} deg)");
        }

        // ---- frame rejection
        {
            var src = Procedural("walk");
            for (int c = 0; c < PoseChannels.Count; c++) src[3][c] = float.NaN;
            var r = Clean(rig, src, "walk");
            check(r.Report.RejectedFrameCount == 1 && r.Report.RejectedFrameList[0] == 3, "frame rejection: a frame without any usable joint is rejected and rebuilt from its neighbours");
            var fin = PoseChannels.FromPoses(r.Poses);
            var p = Procedural("walk");
            float dev = 0; for (int c = 0; c < RigDefinition.PartCount; c++) dev = Mathf.Max(dev, Mathf.Abs(fin[3][c] - 0.5f * (p[2][c] + p[4][c])));
            check(dev < 25f, $"frame rejection: the rebuilt frame lies between its neighbours (max {dev:0.0} deg off the midpoint)");
            var many = Procedural("walk");
            for (int i = 0; i < 8; i += 2) for (int c = 0; c < PoseChannels.Count; c++) many[i][c] = float.NaN;
            bool threw = false;
            try { Clean(rig, many, "walk"); } catch (PoseRejectedException) { threw = true; }
            check(threw, "frame rejection: when half of the frames are unusable the whole sequence is rejected");
        }

        // ---- idle
        {
            var tiny = Procedural("idle");
            for (int i = 0; i < 8; i++) for (int p = 0; p < RigDefinition.PartCount; p++) tiny[i][p] *= 0.5f;
            var settings = Settings();
            var r = Clean(rig, tiny, "idle", settings);
            check(r.Report.SourceMetrics.VisibleMotion < settings.constraints.idleMinMotionPixels, $"idle: the test sway is below the visible threshold ({r.Report.SourceMetrics.VisibleMotion:0.00} px)");
            check(r.Report.FinalMetrics.VisibleMotion >= settings.constraints.idleMinMotionPixels - 0.1f, $"idle: the sway is amplified until it is visible ({r.Report.FinalMetrics.VisibleMotion:0.00} px, gain x{r.Report.IdleMotionGain:0.0})");
            var again = new PoseReport { Kind = "idle" };
            var ch = PoseChannels.FromPoses(r.Poses);
            var before = PoseChannels.Clone(ch);
            PoseConstraints.Apply(rig, ch, "idle", true, settings, again, true);
            check(MaxJointChange(before, ch) < 0.05f, "idle: applying the constraints to the finished poses again changes nothing (idempotent)");
            var ch2 = PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses("idle", 8, 8, 0.1f));
            var snapshot = PoseChannels.Clone(ch2);
            PoseConstraints.Enforce(rig, ch2, "idle", true, settings, new PoseReport { Kind = "idle" });
            check(MaxJointChange(snapshot, ch2) < 0.05f, "Enforce (rebuild at another intensity) never amplifies or reshapes the motion");
            var off = Settings(); off.constraints.idleMotion = false;
            var rOff = Clean(rig, tiny, "idle", off);
            check(rOff.Report.FinalMetrics.VisibleMotion < settings.constraints.idleMinMotionPixels, "idle: the amplification can be switched off");
        }

        // ---- weapon and ground
        {
            var src = Procedural("walk");
            src[2][(int)RigPart.ArmNearUpper] = 60f; src[2][(int)RigPart.Weapon] = 140f; src[2][(int)RigPart.ArmNearLower] = 10f;
            var r = Clean(rig, src, "walk");
            var fin = r.Report.FinalMetrics;
            check(fin.MaxWeaponGap < 0.01f, $"weapon: stays in the hand in every frame (gap {fin.MaxWeaponGap:0.000} px)");
            check(fin.MaxWeaponBelowGround <= 1.05f, $"weapon: does not dig into the ground (tip {Mathf.Max(0f, fin.MaxWeaponBelowGround):0.0} px below the ground line)");
        }

        // ---- foot lock: planted feet may not skid as fast as swinging ones
        {
            var src = Procedural("walk");
            for (int i = 0; i < 8; i++)
            {
                src[i][(int)RigPart.LegNearLower] = 0f; src[i][(int)RigPart.LegFarLower] = 0f;   // straight legs: both feet stay on the ground while the legs swing
                src[i][(int)RigPart.LegNearUpper] = 46f * Mathf.Sin(2f * Mathf.PI * i / 8f); src[i][(int)RigPart.LegFarUpper] = -src[i][(int)RigPart.LegNearUpper];
            }
            var off = Settings(); off.constraints.footLock = false; off.constraints.gaitSymmetry = 0f;
            var on = Settings(); on.constraints.gaitSymmetry = 0f;
            var rOff = Clean(rig, src, "walk", off);
            var rOn = Clean(rig, src, "walk", on);
            var limits = ConstraintLimits.For("walk", rig, on.constraints);
            // a planted foot that skids: the far leg is lifted and stays bent while the near leg is straight and swings, so the near foot is always the one on the ground
            var skid = Procedural("walk");
            for (int i = 0; i < 8; i++)
            {
                skid[i][(int)RigPart.LegNearLower] = 0f; skid[i][(int)RigPart.LegNearUpper] = 46f * Mathf.Sin(2f * Mathf.PI * i / 8f);
                skid[i][(int)RigPart.LegFarUpper] = 55f; skid[i][(int)RigPart.LegFarLower] = -110f;
            }
            var sOff = Clean(rig, skid, "walk", off); var sOn = Clean(rig, skid, "walk", on);
            check(sOff.Report.FinalMetrics.MaxFootStep > limits.PlantedFootStep * 1.05f, $"foot lock: a planted foot skidding {sOff.Report.FinalMetrics.MaxFootStep:0.0} px per frame is a real problem (planted limit {limits.PlantedFootStep:0.0})");
            check(sOn.Report.FinalMetrics.MaxFootStep <= limits.PlantedFootStep * 1.03f, $"foot lock: with it the planted foot skids at most {sOn.Report.FinalMetrics.MaxFootStep:0.0} px per frame (limit {limits.PlantedFootStep:0.0})");
            var tilted = Procedural("walk");
            for (int i = 0; i < 8; i++) { tilted[i][(int)RigPart.LegNearLower] = 0f; tilted[i][(int)RigPart.LegFarLower] = 0f; tilted[i][(int)RigPart.LegNearUpper] = 5f; tilted[i][(int)RigPart.LegFarUpper] = -5f; tilted[i][(int)RigPart.FootNear] = 40f; tilted[i][(int)RigPart.FootFar] = 40f; }
            var rt = Clean(rig, tilted, "walk", on);
            var ft = PoseChannels.FromPoses(rt.Poses);
            float soleTilt = Mathf.Abs(ft[0][(int)RigPart.FootNear] + ft[0][(int)RigPart.LegNearUpper] + ft[0][(int)RigPart.LegNearLower]);
            check(soleTilt < 7f, $"foot lock: a foot planted with its sole tilted 40 deg is brought back to {soleTilt:0} deg (limit 5)");
        }

        // ---- constraints off: only validation runs
        {
            var src = Procedural("attack");
            src[5][(int)RigPart.LegNearUpper] += 95f;
            var off = Settings(); off.constraints.enabled = false;
            var r = Clean(rig, src, "attack", off);
            check(!r.Report.Corrections.Any(c => c.name.StartsWith("foot step") || c.name.StartsWith("single-frame")), "the constraint stage can be switched off");
        }

        // ---- real SDPose clips
        foreach (string kind in new[] { "walk", "run", "attack", "idle" })
        {
            string path = Path.Combine(fixtureDir, $"sdpose_chosen_{kind}.json");
            if (!File.Exists(path)) { check(false, $"fixture missing: {path}"); continue; }
            bool loop = kind != "attack";
            var kp = OpenPoseJson.Parse(File.ReadAllText(path));
            var cand = SDPoseAnalysis.Analyze(rig, kp, kind, loop, 8, false);
            var settings = Settings();
            var reference = ProceduralRigPoses.Instance.GetPoses(kind, 8, 8, 1f);
            var r = PoseCleanup.Run(rig, cand.Extraction.Poses, kind, loop, settings, reference);
            var limits = ConstraintLimits.For(kind, rig, settings.constraints);
            var rep = r.Report;
            check(rep.FinalMetrics.MaxFootStep <= limits.FootStep * 1.01f, $"real {kind}: foot step {rep.FinalMetrics.MaxFootStep:0.0} px (AI source {rep.SourceMetrics.MaxFootStep:0.0}) within the limit {limits.FootStep:0.0}");
            check(rep.FinalMetrics.MaxWeaponGap < 0.01f && rep.FinalMetrics.MaxWeaponBelowGround <= 1.05f, $"real {kind}: sword in the hand and above the ground");
            if (kind == "run") check(rep.FinalMetrics.MaxRootStep <= limits.HipStep * 1.02f, $"real run: the hip moves at most {rep.FinalMetrics.MaxRootStep:0.0} px per frame (AI source {rep.SourceMetrics.MaxRootStep:0.0}, limit {limits.HipStep:0.0})");
            check(rep.FinalMetrics.MinLegExtension >= limits.MinLegExtension - 0.02f, $"real {kind}: legs never folded below {limits.MinLegExtension:P0} (min {rep.FinalMetrics.MinLegExtension:P0})");
            check(rep.HasReference && rep.MaxDeviationDegrees >= rep.MeanDeviationDegrees, $"real {kind}: report holds AI-vs-procedural numbers (mean {rep.MeanDeviationDegrees:0.0}, max {rep.MaxDeviationDegrees:0.0} deg at {rep.MaxDeviationWhere})");
            check(rep.FrameCount == 8 && rep.CorrectedFrameCount <= 8 && rep.RejectedFrameCount == 0, $"real {kind}: {rep.CorrectedFrameCount}/8 frames corrected ({rep.CorrectedFramePercent:0}%), {rep.RejectedFrameCount} rejected");
            var ch = PoseChannels.FromPoses(r.Poses);
            var snapshot = PoseChannels.Clone(ch);
            PoseConstraints.Enforce(rig, ch, kind, loop, settings, new PoseReport { Kind = kind });
            check(MaxJointChange(snapshot, ch) < 0.05f, $"real {kind}: the finished poses already satisfy every limit (Enforce changes nothing)");
            var contribution = PoseContribution.Compute(r.Poses, reference, loop);
            check(contribution.MeanDeviationDegrees > 5f, $"real {kind}: the AI motion survives the constraints (mean difference to procedural {contribution.MeanDeviationDegrees:0.0} deg, {contribution.Level})");
        }
        {
            // the first idle clip (old prompt): its far forearm read -226..+136 deg (wrap-around and impossible values) and used to flicker between two clamp limits
            string path = Path.Combine(fixtureDir, "sdpose_idle_noisy.json");
            var kp = OpenPoseJson.Parse(File.ReadAllText(path));
            var cand = SDPoseAnalysis.Analyze(rig, kp, "idle", true, 8, false);
            var r = PoseCleanup.Run(rig, cand.Extraction.Poses, "idle", true, Settings());
            var fin = PoseChannels.FromPoses(r.Poses);
            float flick = 0; for (int i = 0; i < 8; i++) flick = Mathf.Max(flick, Mathf.Abs(fin[(i + 1) % 8][(int)RigPart.ArmFarLower] - fin[i][(int)RigPart.ArmFarLower]));
            check(flick < 8f, $"real noisy idle: the far forearm no longer flickers (largest step {flick:0.0} deg); {r.Report.Corrections.Where(c => c.name.StartsWith("impossible")).Sum(c => c.values)} impossible angles discarded");
            check(r.Report.FinalMetrics.VisibleMotion >= 1.1f, $"real noisy idle: the sway is visible ({r.Report.FinalMetrics.VisibleMotion:0.0} px)");
        }
        {
            // the walk: the exaggerated sword-arm frame is reduced, the stride is not lopsided any more
            var kp = OpenPoseJson.Parse(File.ReadAllText(Path.Combine(fixtureDir, "sdpose_chosen_walk.json")));
            var cand = SDPoseAnalysis.Analyze(rig, kp, "walk", true, 8, false);
            var r = PoseCleanup.Run(rig, cand.Extraction.Poses, "walk", true, Settings());
            check(r.Report.FinalMetrics.MaxSpike < 0.4f * r.Report.SourceMetrics.MaxSpike, $"real walk: largest single-frame spike {r.Report.SourceMetrics.MaxSpike:0} -> {r.Report.FinalMetrics.MaxSpike:0} deg");
            var fin = PoseChannels.FromPoses(r.Poses);
            float armPeak = fin.Max(f => Mathf.Abs(f[(int)RigPart.ArmNearUpper]));
            check(armPeak < 25f, $"real walk: the sword arm swings at most {armPeak:0} deg (source {PoseChannels.FromPoses(cand.Extraction.Poses).Max(f => Mathf.Abs(f[(int)RigPart.ArmNearUpper])):0})");
            float mn = fin.Average(f => f[(int)RigPart.LegNearUpper]), mf = fin.Average(f => f[(int)RigPart.LegFarUpper]);
            check(Mathf.Abs(mn - mf) < 10f, $"real walk: the legs swing about the same line (centre angles differ by {Mathf.Abs(mn - mf):0.0} deg)");
        }
    }

    // channels after plain validation only (what the constraint stage receives)
    private static float[][] ValidatedCopy(RigDefinition rig, float[][] src, string kind)
    {
        var ch = PoseChannels.Clone(src);
        PoseValidator.Repair(rig, ch, kind, kind != "attack", Settings());
        return ch;
    }
}
