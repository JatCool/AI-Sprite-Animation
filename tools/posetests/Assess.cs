using System.Linq;
using AISpriteAnimation;
using UnityEngine;

/// <summary>
/// `posetests assess <src.raw> <joints.txt> <kind> <frames> <keypoints.json...>`: runs one dumped SDPose clip through analysis + the clean-up stage and prints
/// the quantitative report (procedural reference, source poses, final poses) and a per-frame table. For iterating on the constraint stage without ComfyUI.
/// </summary>
public static class Assess
{
    private static void WriteRaw(RigDefinition rig, Color32[] sprite, IReadOnlyList<RigPose> poses, string path)
    {
        var render = SpriteRig.Render(rig, sprite, poses, 102, 27, 27, false);
        using var o = new BinaryWriter(File.Create(path));
        o.Write(102); o.Write(render.Count);
        foreach (var f in render) foreach (var px in f) { o.Write(px.r); o.Write(px.g); o.Write(px.b); o.Write(px.a); }
    }

    public static void Run(RigDefinition rig, Color32[] sprite, string kind, int frames, string[] files)
    {
        bool loop = kind != "attack";
        var reference = ProceduralRigPoses.Instance.GetPoses(kind, frames, frames, 1f);
        bool table = Environment.GetEnvironmentVariable("TABLE") != null;
        bool noConstraints = Environment.GetEnvironmentVariable("NOCONSTRAINTS") != null;
        foreach (string file in files)
        {
            var kp = OpenPoseJson.Parse(File.ReadAllText(file));
            var cand = SDPoseAnalysis.Analyze(rig, kp, kind, loop, frames, false);
            Console.WriteLine($"=== {kind} {Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)}: {cand.Verdict}");
            if (cand.Extraction == null || !cand.Extraction.Ok) { Console.WriteLine("    no usable motion"); continue; }

            if (Environment.GetEnvironmentVariable("VIDEO") != null)
            {
                // what the whole generated clip contains, joint by joint (degrees): range, standard deviation, share of the variance in the slow (<= 1 cycle per clip x 3) part
                var vch = PoseChannels.FromPoses(cand.VideoPoses); PoseChannels.FillNonFinite(vch, false);
                Console.WriteLine($"    video: {vch.Length} frames; normalised keypoint motion (fraction of torso length):");
                double torso = 0; int tc = 0;
                foreach (var f in kp) if (f.Has(1, 0.3f) && (f.Has(8, 0.3f) || f.Has(11, 0.3f))) { var hip = f.Has(8, 0.3f) ? f.point[8] : f.point[11]; torso += Vector2.Distance(f.point[1], hip); tc++; }
                torso = tc > 0 ? torso / tc : 1;
                foreach (var (name, idx) in new[] { ("nose", 0), ("neck", 1), ("r.shoulder", 2), ("r.wrist", 4), ("l.wrist", 7), ("r.hip", 8), ("r.knee", 9), ("r.ankle", 10) })
                {
                    var ys = kp.Where(f => f.Has(idx, 0.3f)).Select(f => f.point[idx]).ToList();
                    if (ys.Count < 4) continue;
                    double minX = ys.Min(v => v.x), maxX = ys.Max(v => v.x), minY = ys.Min(v => v.y), maxY = ys.Max(v => v.y);
                    // detrend: remove the line between first and last third means (camera/person drift)
                    Console.WriteLine($"      {name,-11} x range {(maxX - minX) / torso,5:0.000}  y range {(maxY - minY) / torso,5:0.000}");
                }
                foreach (RigPart part in new[] { RigPart.Body, RigPart.Head, RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.ArmFarUpper, RigPart.ArmFarLower, RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.LegFarUpper, RigPart.LegFarLower })
                {
                    float lo = float.MaxValue, hi = float.MinValue; double m = 0; int c = (int)part;
                    foreach (var f in vch) { float v = f[c]; lo = Mathf.Min(lo, v); hi = Mathf.Max(hi, v); m += v; }
                    m /= vch.Length; double var2 = 0; foreach (var f in vch) var2 += (f[c] - m) * (f[c] - m);
                    // high-frequency share: variance of the second difference relative to the variance of the signal
                    double d2 = 0; for (int t = 1; t < vch.Length - 1; t++) { double d = vch[t - 1][c] - 2 * vch[t][c] + vch[t + 1][c]; d2 += d * d; }
                    Console.WriteLine($"      {part,-13} range {hi - lo,6:0.0}  std {Math.Sqrt(var2 / vch.Length),5:0.0}  roughness(2nd diff rms) {Math.Sqrt(d2 / Math.Max(1, vch.Length - 2)),5:0.0}");
                }
            }
            var settings = new PoseCleanupSettings();
            if (noConstraints) settings.constraints.enabled = false;
            PoseCleanup.Result result;
            try { result = PoseCleanup.Run(rig, cand.Extraction.Poses, kind, loop, settings); }
            catch (PoseRejectedException e) { Console.WriteLine("    REJECTED: " + e.Message.Replace("\n", "\n      ")); continue; }

            var mRef = PoseMetrics.Measure(rig, PoseChannels.FromPoses(reference), loop);
            var mSrc = PoseMetrics.Measure(rig, PoseChannels.FromPoses(cand.Extraction.Poses), loop);
            var mFin = PoseMetrics.Measure(rig, PoseChannels.FromPoses(result.Poses), loop);
            Console.WriteLine("  procedural : " + mRef.Describe());
            Console.WriteLine("  AI source  : " + mSrc.Describe());
            Console.WriteLine("  AI final   : " + mFin.Describe());
            var contribution = PoseContribution.Compute(result.Poses, reference, loop);
            var sourceContribution = PoseContribution.Compute(cand.Extraction.Poses, reference, loop);
            Console.WriteLine($"  contribution: source {sourceContribution.MeanDeviationDegrees:0.0} deg -> final {contribution.MeanDeviationDegrees:0.0} deg ({contribution.Level}); movement {contribution.MotionDeviationDegrees:0.0}, stance {contribution.StanceOffsetDegrees:0.0}; range {contribution.AiRangeDegrees:0} vs {contribution.ReferenceRangeDegrees:0}");
            Console.WriteLine("  " + result.Report.ToString().Replace("\n", "\n  "));
            if (Environment.GetEnvironmentVariable("ROWS") != null)
            {
                foreach (var (label, poses) in new[] { ("procedural", (IReadOnlyList<RigPose>)reference), ("AI final  ", result.Poses) })
                {
                    var frames8 = SpriteRig.Render(rig, sprite, poses, 102, 27, 27, false);
                    // Render returns bottom-up rows; the lowest opaque row of the character on the cell grid
                    var rows = frames8.Select(f => { int lo = 999, hi = -1; for (int y = 0; y < 102; y++) for (int x = 0; x < 102; x++) if (f[y * 102 + x].a > 0) { lo = Math.Min(lo, y); hi = Math.Max(hi, y); } return $"{lo}-{hi}"; }).ToArray();
                    // y = 0 is the bottom row of the cell (flipped on output): the smallest y with an opaque pixel is the lowest point
                    Console.WriteLine($"    lowest opaque row ({label}): " + string.Join(" ", rows));
                }
            }
            if (Environment.GetEnvironmentVariable("FEET") != null)
            {
                foreach (var (label, m) in new[] { ("procedural", mRef), ("AI source ", mSrc), ("AI final  ", mFin) })
                {
                    Console.WriteLine($"    {label} near x/y: " + string.Join(" ", Enumerable.Range(0, m.Frames).Select(i => $"{m.FootNear[i].x:0.0}/{m.FootNear[i].y:0.0}")));
                    Console.WriteLine($"    {label} far  x/y: " + string.Join(" ", Enumerable.Range(0, m.Frames).Select(i => $"{m.FootFar[i].x:0.0}/{m.FootFar[i].y:0.0}")));
                }
            }
            if (Environment.GetEnvironmentVariable("REAPPLY") != null)
            {
                // idempotence: constraints applied to the final poses again must change (almost) nothing
                var again = PoseChannels.FromPoses(result.Poses);
                var rep2 = new PoseReport { Kind = kind };
                PoseConstraints.Apply(rig, again, kind, loop, settings, rep2, true);
                var mAgain = PoseMetrics.Measure(rig, again, loop);
                Console.WriteLine($"  re-applied (final pass): foot step {mAgain.MaxFootStep:0.0} px, changed values: {string.Join(", ", rep2.Corrections.Select(c => c.name + " " + c.values))}");
            }
            string sheetDir = Environment.GetEnvironmentVariable("SHEETDIR");
            if (sheetDir != null)
            {
                Directory.CreateDirectory(sheetDir);
                string tag = kind + (Environment.GetEnvironmentVariable("TAG") ?? "");
                WriteRaw(rig, sprite, reference, Path.Combine(sheetDir, tag + "_1proc.raw"));
                WriteRaw(rig, sprite, cand.Extraction.Poses, Path.Combine(sheetDir, tag + "_2src.raw"));
                WriteRaw(rig, sprite, result.Poses, Path.Combine(sheetDir, tag + "_3final.raw"));
            }
            if (table)
            {
                var src = PoseChannels.FromPoses(cand.Extraction.Poses); var fin = PoseChannels.FromPoses(result.Poses);
                foreach (RigPart part in new[] { RigPart.Body, RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.ArmFarUpper, RigPart.ArmFarLower, RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.LegFarUpper, RigPart.LegFarLower, RigPart.Weapon })
                    Console.WriteLine($"    {part,-13} src " + string.Join(" ", src.Select(f => ((int)f[(int)part]).ToString().PadLeft(4))) + "   fin " + string.Join(" ", fin.Select(f => ((int)f[(int)part]).ToString().PadLeft(4))));
                {
                    var refCh = PoseChannels.FromPoses(reference); int sh = contribution.AlignmentShift;
                    Console.WriteLine($"    (procedural reference, shifted by {sh} frames to the best alignment:)");
                    foreach (RigPart part in new[] { RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.ArmFarUpper, RigPart.ArmFarLower, RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.LegFarUpper, RigPart.LegFarLower })
                        Console.WriteLine($"    {part,-13} ref " + string.Join(" ", Enumerable.Range(0, refCh.Length).Select(i => ((int)refCh[(i + sh) % refCh.Length][(int)part]).ToString().PadLeft(4))));
                }
                Console.WriteLine("    rootX         src " + string.Join(" ", src.Select(f => f[PoseChannels.RootX].ToString("0.0").PadLeft(4))) + "   fin " + string.Join(" ", fin.Select(f => f[PoseChannels.RootX].ToString("0.0").PadLeft(4))));
                Console.WriteLine("    hop           src " + string.Join(" ", src.Select(f => f[PoseChannels.Hop].ToString("0.0").PadLeft(4))) + "   fin " + string.Join(" ", fin.Select(f => f[PoseChannels.Hop].ToString("0.0").PadLeft(4))));
                Console.WriteLine("    hip y         src " + string.Join(" ", mSrc.Hip.Select(v => v.y.ToString("0.0").PadLeft(5))) + "   fin " + string.Join(" ", mFin.Hip.Select(v => v.y.ToString("0.0").PadLeft(5))));
                Console.WriteLine("    foot N (x,y)  fin " + string.Join(" ", mFin.FootNear.Select(v => $"{v.x:0}/{v.y:0}".PadLeft(6))));
                Console.WriteLine("    foot F (x,y)  fin " + string.Join(" ", mFin.FootFar.Select(v => $"{v.x:0}/{v.y:0}".PadLeft(6))));
                Console.WriteLine("    foot N (x,y)  src " + string.Join(" ", mSrc.FootNear.Select(v => $"{v.x:0}/{v.y:0}".PadLeft(6))));
                Console.WriteLine("    foot F (x,y)  src " + string.Join(" ", mSrc.FootFar.Select(v => $"{v.x:0}/{v.y:0}".PadLeft(6))));
                Console.WriteLine("    tip (x,y)     fin " + string.Join(" ", mFin.WeaponTip.Select(v => $"{v.x:0}/{v.y:0}".PadLeft(6))));
            }
        }
    }
}
