using System.Linq;
using AISpriteAnimation;
using UnityEngine;

// Pure-math tests for the AI Pose + Rig layers (no Unity editor needed).
// usage: posetests <src.raw> <joints.txt>
var inv = System.Globalization.CultureInfo.InvariantCulture;
bool assessMode = args.Length > 0 && args[0] == "assess";
bool analyzeMode = args.Length > 0 && (args[0] == "analyze" || assessMode);
string analyzeKind = "walk"; int analyzeFrames = 8; string[] analyzeFiles = Array.Empty<string>();
if (analyzeMode)
{
    analyzeKind = args[3]; analyzeFrames = int.Parse(args[4]); analyzeFiles = args.Skip(5).ToArray();
    args = new[] { args[1], args[2] };
}
var raw = File.ReadAllBytes(args[0]);
int sw = BitConverter.ToInt32(raw, 0), sh = BitConverter.ToInt32(raw, 4);
var sprite = new Color32[sw * sh];
for (int i = 0; i < sprite.Length; i++) sprite[i] = new Color32(raw[8 + i * 4], raw[9 + i * 4], raw[10 + i * 4], raw[11 + i * 4]);
var rig = RigAutoBuilder.Build(sprite, sw, sh, RigAutoParams.Default);
foreach (var line in File.ReadAllLines(args[1]))
{
    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    if (t.Length == 3 && Enum.TryParse<RigJoint>(t[0], out var j)) rig.SetJoint(j, new Vector2(float.Parse(t[1], inv), float.Parse(t[2], inv)));
    if (t.Length == 2 && t[0] == "ground") rig.groundY = float.Parse(t[1], inv);
}
RigAutoBuilder.AssignParts(rig, sprite, sw, sh, RigAutoParams.Default);

if (assessMode)
{
    Assess.Run(rig, sprite, analyzeKind, analyzeFrames, analyzeFiles);
    return 0;
}
if (analyzeMode)
{
    bool loopKind = analyzeKind != "attack";
    var refPoses = ProceduralRigPoses.Instance.GetPoses(analyzeKind, analyzeFrames, analyzeFrames, 1f);
    foreach (string file in analyzeFiles)
    {
        var frames = OpenPoseJson.Parse(File.ReadAllText(file));
        var cand = SDPoseAnalysis.Analyze(rig, frames, analyzeKind, loopKind, analyzeFrames, false);
        string name = Path.GetFileName(file);
        if (cand.Extraction == null || !cand.Extraction.Ok) { Console.WriteLine($"{name,-22} NO CYCLE: {cand.Verdict}"); continue; }
        if (Environment.GetEnvironmentVariable("KPV") != null)
            foreach (RigPart part in new[] { RigPart.Body, RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.LegFarUpper, RigPart.LegFarLower, RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.ArmFarUpper, RigPart.ArmFarLower })
                Console.WriteLine($"      {part,-13}: " + string.Join(" ", cand.VideoPoses.Select(p => ((int)(p[part] * Mathf.Rad2Deg)).ToString().PadLeft(4))));
        var c = PoseContribution.Compute(cand.Extraction.Poses, refPoses, loopKind);
        Console.WriteLine($"{name,-22} q={cand.Quality:0.00} {cand.Extraction.Method.Split('(')[0].Trim(),-34} start={cand.Extraction.Start,4:0.#} len={cand.Extraction.Length,4:0.#} | {cand.Verdict} | AI-vs-procedural {c.MeanDeviationDegrees,5:0.0} deg ({c.Level})");
        string raw2 = file.Replace(".json", ".raw");
        var render = SpriteRig.Render(rig, sprite, cand.Extraction.Poses, 102, 27, 27, false);
        using (var o = new BinaryWriter(File.Create(raw2))) { o.Write(102); o.Write(render.Count); foreach (var f in render) foreach (var px in f) { o.Write(px.r); o.Write(px.g); o.Write(px.b); o.Write(px.a); } }
    }
    return 0;
}

int fails = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) fails++; }
var settings = new PoseCleanupSettings();

// ---------------------------------------------------------------- 1. OpenPose mapper round trip: rig pose -> skeleton -> rig pose
Console.WriteLine("== OpenPose mapper round trip (rig pose -> OpenPose keypoints -> rig pose)");
RigPart[] recoverable = { RigPart.Body, RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.ArmFarUpper, RigPart.ArmFarLower,
    RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.LegFarUpper, RigPart.LegFarLower };
foreach (string kind in new[] { "idle", "walk", "run", "attack" })
{
    var poses = ProceduralRigPoses.Instance.GetPoses(kind, 8, 8, 1f);
    var frames = new List<OpenPoseFrame>();
    foreach (var p in poses) frames.Add(OpenPoseMapper.Synthesize(rig, p));
    var back = OpenPoseMapper.Map(rig, frames, kind, false, kind != "attack");
    double worst = 0;
    foreach (RigPart part in recoverable)
        for (int i = 0; i < poses.Length; i++)
            worst = Math.Max(worst, Math.Abs(poses[i][part] - back[i][part]) * Mathf.Rad2Deg);
    // head: only the shape (mean-free) is recoverable
    double hm0 = 0, hm1 = 0;
    for (int i = 0; i < poses.Length; i++) { hm0 += poses[i][RigPart.Head]; hm1 += back[i][RigPart.Head]; }
    hm0 /= poses.Length; hm1 /= poses.Length;
    double worstHead = 0;
    for (int i = 0; i < poses.Length; i++) worstHead = Math.Max(worstHead, Math.Abs((poses[i][RigPart.Head] - hm0) - (back[i][RigPart.Head] - hm1)) * Mathf.Rad2Deg);
    Check(worst < 0.5, $"{kind}: limbs/body recovered within {worst:0.000} deg");
    Check(worstHead < 6, $"{kind}: head motion (median-centred) within {worstHead:0.00} deg");
}

// mirrored (left-facing) input must give the same result as right-facing
{
    var poses = ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f);
    var right = new List<OpenPoseFrame>(); var left = new List<OpenPoseFrame>();
    foreach (var p in poses)
    {
        var f = OpenPoseMapper.Synthesize(rig, p); right.Add(f);
        var m = new OpenPoseFrame();
        for (int i = 0; i < OpenPoseFrame.Count; i++) { m.point[i] = new Vector2(-f.point[i].x, f.point[i].y); m.confidence[i] = f.confidence[i]; }
        left.Add(m);
    }
    var a = OpenPoseMapper.Map(rig, right, "walk", false, true);
    // left-facing sprite: mirror the rig itself
    var mirrored = rig.Clone();
    for (int i = 0; i < mirrored.joints.Length; i++) mirrored.joints[i] = new Vector2(rig.width - rig.joints[i].x, rig.joints[i].y);
    for (int y = 0; y < rig.height; y++) for (int x = 0; x < rig.width; x++) mirrored.SetMask(rig.width - 1 - x, y, rig.GetMask(x, y));
    var b = OpenPoseMapper.Map(mirrored, left, "walk", true, true);
    double worst = 0;
    for (int i = 0; i < a.Length; i++) foreach (RigPart part in recoverable) worst = Math.Max(worst, Math.Abs(a[i][part] - b[i][part]) * Mathf.Rad2Deg);
    Check(worst < 0.01, $"left-facing sprite + mirrored skeleton gives identical angles (diff {worst:0.0000} deg)");
}

// OpenPose JSON (ComfyUI POSE_KEYPOINT, normalised coordinates) parses
{
    var f = OpenPoseMapper.Synthesize(rig, new RigPose());
    var sb = new System.Text.StringBuilder("[{\"canvas_width\":100,\"canvas_height\":200,\"people\":[{\"pose_keypoints_2d\":[");
    for (int i = 0; i < 18; i++) sb.Append((f.point[i].x / 100f).ToString(inv)).Append(',').Append((f.point[i].y / 200f).ToString(inv)).Append(",1").Append(i < 17 ? "," : "");
    sb.Append("]}]}]");
    var parsed = OpenPoseJson.Parse(sb.ToString());
    Check(parsed.Count == 1 && Vector2.Distance(parsed[0].point[9], f.point[9]) < 0.01f, "OpenPose JSON (normalised) parses back to pixel coordinates");
}

// ---------------------------------------------------------------- 2. validation and repair
Console.WriteLine("== Validation / repair");
{
    var good = ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f);
    var ch = PoseChannels.FromPoses(good);
    var rep = PoseValidator.Repair(rig, ch, "walk", true, settings);
    Check(rep.Ok && rep.RepairedValues == 0, "clean procedural walk passes without repairs");
}
{
    var ch = PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f));
    ch[3][(int)RigPart.ArmNearUpper] = float.NaN; ch[5][PoseChannels.RootX] = float.PositiveInfinity;
    var rep = PoseValidator.Repair(rig, ch, "walk", true, settings);
    bool finite = true; foreach (var f in ch) foreach (float v in f) finite &= PoseChannels.IsFinite(v);
    Check(rep.Ok && finite && rep.RepairedValues >= 2, "NaN / infinity are interpolated from neighbouring frames");
}
{
    var ch = PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f));
    ch[2][(int)RigPart.LegNearLower] = 170f; ch[4][(int)RigPart.Head] = -140f;
    var rep = PoseValidator.Repair(rig, ch, "walk", true, settings);
    PoseLimits.Get("walk", (int)RigPart.LegNearLower, out _, out float hi);
    Check(rep.Ok && ch[2][(int)RigPart.LegNearLower] <= hi + 0.01f && ch[4][(int)RigPart.Head] >= -32.01f, "impossible joint angles are clamped to the range");
}
{
    var ch = PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f));
    for (int i = 0; i < 8; i++) { ch[i][PoseChannels.RootY] = 6f; ch[i][PoseChannels.Hop] = 5f; }
    var rep = PoseValidator.Repair(rig, ch, "walk", true, settings);
    bool grounded = true; foreach (var f in ch) grounded &= f[PoseChannels.RootY] == 0f && f[PoseChannels.Hop] == 0f;
    Check(grounded, "walk: vertical drift / hop from the AI is removed (feet stay on the ground line)");
    var chRun = PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses("run", 8, 8, 1f));
    PoseValidator.Repair(rig, chRun, "run", true, settings);
    float maxHop = 0; foreach (var f in chRun) maxHop = Math.Max(maxHop, f[PoseChannels.Hop]);
    Check(maxHop > 0.5f, $"run: intentional flight phase is kept (hop up to {maxHop:0.0} px)");
}
{
    var ch = PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f));
    for (int i = 0; i < 8; i++) for (int p = 0; p < RigDefinition.PartCount; p++) if ((i + p) % 2 == 0) ch[i][p] = float.NaN;
    var rep = PoseValidator.Repair(rig, ch, "walk", true, settings);
    Check(rep.Rejected, "garbage (half of all values NaN) is rejected, not repaired into something unrelated");
}
{
    var ch = PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f));
    var bad = new float[8][]; for (int i = 0; i < 8; i++) bad[i] = i == 3 ? new float[5] : ch[i];
    Check(PoseValidator.Repair(rig, bad, "walk", true, settings).Rejected, "a frame with missing joints is rejected");
}
{
    bool threw = false;
    var poses = new RigPose[8]; for (int i = 0; i < 8; i++) { poses[i] = new RigPose(); poses[i].angle[0] = float.NaN; poses[i].angle[3] = float.NaN; poses[i].angle[4] = float.NaN; poses[i].angle[5] = float.NaN; poses[i].angle[8] = float.NaN; poses[i].angle[9] = float.NaN; }
    try { PoseCleanup.Run(rig, poses, "walk", true, settings); } catch (PoseRejectedException) { threw = true; }
    Check(threw, "PoseCleanup throws PoseRejectedException for unusable sequences (never returns them)");
}

// ---------------------------------------------------------------- 3. smoothing
Console.WriteLine("== Smoothing");
var rng = new System.Random(5);
double Jitter(float[][] c, int ch) { double s = 0; for (int i = 1; i < c.Length - 1; i++) { double d2 = c[i - 1][ch] - 2 * c[i][ch] + c[i + 1][ch]; s += d2 * d2; } return Math.Sqrt(s / (c.Length - 2)); }
{
    var clean = PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses("walk", 12, 12, 1f));
    var noisy = PoseChannels.Clone(clean);
    foreach (var f in noisy) for (int p = 0; p < RigDefinition.PartCount; p++) f[p] += (float)(rng.NextDouble() * 2 - 1) * 4f;
    var sm = PoseChannels.Clone(noisy);
    PoseSmoother.Smooth(sm, true, settings);
    double e0 = 0, e1 = 0; int cnt = 0;
    for (int i = 0; i < 12; i++) for (int p = 0; p < RigDefinition.PartCount; p++) { e0 += Math.Pow(noisy[i][p] - clean[i][p], 2); e1 += Math.Pow(sm[i][p] - clean[i][p], 2); cnt++; }
    Check(Math.Sqrt(e1 / cnt) < Math.Sqrt(e0 / cnt) * 0.9, $"smoothing reduces jitter: RMS error to the true motion {Math.Sqrt(e0 / cnt):0.00} -> {Math.Sqrt(e1 / cnt):0.00} deg");
    var smOff = PoseChannels.Clone(noisy);
    var off = settings.Clone(); off.smoothing = 0f; PoseSmoother.Smooth(smOff, true, off);
    Check(Jitter(smOff, 3) == Jitter(noisy, 3), "smoothing = 0 changes nothing (fully configurable)");
}
{
    var attack = PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses("attack", 10, 10, 1f));
    var sm = PoseChannels.Clone(attack);
    PoseSmoother.Smooth(sm, false, settings);
    float peak0 = 0, peak1 = 0;
    for (int i = 0; i < 10; i++) { peak0 = Math.Max(peak0, Math.Abs(attack[i][(int)RigPart.ArmNearUpper])); peak1 = Math.Max(peak1, Math.Abs(sm[i][(int)RigPart.ArmNearUpper])); }
    Check(peak1 > peak0 * 0.9f, $"attack swing is preserved (arm peak {peak0:0} -> {peak1:0} deg)");
    Check(sm[0][(int)RigPart.ArmNearUpper] == attack[0][(int)RigPart.ArmNearUpper] && sm[9][(int)RigPart.ArmNearUpper] == attack[9][(int)RigPart.ArmNearUpper], "one-shot first/last pose pinned");
    var q = PoseChannels.Clone(attack); PoseSmoother.Quantize(q, 5f);
    bool stepped = true; foreach (var f in q) for (int p = 0; p < RigDefinition.PartCount; p++) stepped &= Math.Abs(f[p] / 5f - Math.Round(f[p] / 5f)) < 1e-4;
    Check(stepped, "stepped poses: angles snap to 5 deg");
}
{
    // loop closing: a periodic cycle is left alone, a cycle with a seam is closed
    var periodic = PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f));
    var periodicCopy = PoseChannels.Clone(periodic);
    PoseSmoother.CloseLoop(periodicCopy);
    bool untouched = true; for (int i = 0; i < 8; i++) for (int c = 0; c < PoseChannels.Count; c++) untouched &= periodicCopy[i][c] == periodic[i][c];
    Check(untouched, "loop closing leaves an already periodic cycle untouched (no distortion of a clean sine)");
    var ch = PoseChannels.Clone(periodic);
    for (int i = 0; i < 8; i++) ch[i][(int)RigPart.LegNearUpper] += i * 8f;   // a drift that opens the cycle
    float Seam(float[][] c) { float inner = 0; for (int i = 1; i < 8; i++) inner = Math.Max(inner, Math.Abs(c[i][(int)RigPart.LegNearUpper] - c[i - 1][(int)RigPart.LegNearUpper])); return Math.Abs(c[0][(int)RigPart.LegNearUpper] - c[7][(int)RigPart.LegNearUpper]) - inner; }
    float before = Seam(ch);
    PoseSmoother.CloseLoop(ch);
    Check(before > 5f && Seam(ch) < before * 0.3f, $"loop closing removes a seam (excess over the largest inner step {before:0.0} -> {Seam(ch):0.00} deg)");
}

// ---------------------------------------------------------------- 4. resampling and the provider math
Console.WriteLine("== Resampling");
{
    var ch = PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f));
    var r8 = PoseChannels.Resample(ch, 8, true);
    var r16 = PoseChannels.Resample(ch, 16, true);
    bool same = true; for (int i = 0; i < 8; i++) for (int c = 0; c < PoseChannels.Count; c++) same &= r8[i][c] == ch[i][c];
    Check(same && r16.Length == 16 && Math.Abs(r16[2][(int)RigPart.LegNearUpper] - ch[1][(int)RigPart.LegNearUpper]) < 1e-4, "resampling to the same count is identity; a loop doubles by sampling the cycle");
    var one = PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses("attack", 10, 10, 1f));
    var a6 = PoseChannels.Resample(one, 6, false);
    Check(a6[0][3] == one[0][3] && Math.Abs(a6[5][3] - one[9][3]) < 1e-4, "one-shot resampling keeps both end poses");
}

// ---------------------------------------------------------------- 5. kinematics vs the renderer
Console.WriteLine("== Kinematics");
{
    var pose = ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f)[2];
    var j = RigKinematics.JointPositions(rig, pose, false, true);
    float lowest = Math.Max(j[(int)RigJoint.FootNear].y, j[(int)RigJoint.FootFar].y);
    Check(Math.Abs(lowest - (rig.groundY - 2f)) < 3.5f, $"grounded foot joints sit just above the ground line (lowest foot joint y {lowest:0.0}, ground {rig.groundY})");
    var t0 = System.Diagnostics.Stopwatch.StartNew();
    var res = SpriteRig.Render(rig, sprite, ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f), 102, 27, 27, false);
    Console.WriteLine($"      Render 8 frames on a 102x102 grid: {t0.ElapsedMilliseconds} ms");
    t0.Restart();
    var many = new List<RigPose>(); for (int i = 0; i < 100; i++) many.Add(ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f)[i % 8]);
    SpriteRig.Render(rig, sprite, many, 102, 27, 27, false);
    Console.WriteLine($"      Render 100 poses: {t0.ElapsedMilliseconds} ms");
}

// ---------------------------------------------------------------- 6. analysis-by-synthesis fitter on synthetic "AI frames"
Console.WriteLine("== Rig pose fitter (known ground truth -> render -> fit -> compare)");
{
    int cells = 102, left = 27, bottom = 27;
    var gt = ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f);
    foreach (var p in gt)
    {
        foreach (var part in new[] { RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.LegFarUpper, RigPart.LegFarLower }) p[part] *= 1.3f;
        foreach (var part in new[] { RigPart.ArmNearUpper, RigPart.ArmFarUpper }) p[part] *= 0.6f;
        p[RigPart.Body] -= 4f * Mathf.Deg2Rad;
    }
    var truth = SpriteRig.Render(rig, sprite, gt, cells, left, bottom, false);
    var rngN = new System.Random(3);
    var noisy = new List<Color32[]>();
    foreach (var f in truth)
    {
        var c = (Color32[])f.Clone();
        for (int i = 0; i < c.Length; i++) if (rngN.NextDouble() < 0.02) c[i] = c[i].a == 0 ? new Color32(90, 40, 40, 255) : new Color32(0, 0, 0, 0);
        noisy.Add(c);
    }
    var guidance = ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f);   // deliberately different from the truth
    foreach (var (label, targets) in new[] { ("exact frames", truth), ("2% pixel noise", noisy) })
    {
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        var fit = RigPoseFitter.Fit(rig, sprite, targets, cells, left, bottom, false, guidance, "walk", true, new RigPoseFitOptions());
        double imgFit = 0, imgGuide = 0;
        for (int i = 0; i < 8; i++)
        {
            imgFit += RigPoseFitter.FrameError(rig, sprite, fit.Poses[i], truth[i], cells, left, bottom, false) / 8;
            imgGuide += RigPoseFitter.FrameError(rig, sprite, guidance[i], truth[i], cells, left, bottom, false) / 8;
        }
        Console.WriteLine($"      {label}: image-space error to the noise-free truth {imgGuide:P1} (guidance) -> {imgFit:P1} (fit); {fit.Renders} renders, {sw0.ElapsedMilliseconds} ms");
        Check(imgFit < imgGuide * 0.3 && imgFit < 0.15, $"fit from {label} reproduces the true silhouette ({imgFit:P1} vs {imgGuide:P1} for the guidance)");
    }
}

// ---------------------------------------------------------------- 7a. real SDPose output (walking man generated by AnimateDiff, keypoints by SDPose)
Console.WriteLine("== Real SDPose keypoints -> rig motion");
{
    string fixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures");
    if (!Directory.Exists(fixtureDir)) fixtureDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(args[1]), "..", "posetests", "fixtures"));
    var reference = ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f);
    foreach (string name in new[] { "sdpose_walk_a.json", "sdpose_walk_b.json" })
    {
        var frames = OpenPoseJson.Parse(File.ReadAllText(Path.Combine(fixtureDir, name)));
        int facing = OpenPoseTracking.DetectFacing(frames);
        if (facing < 0) OpenPoseTracking.Mirror(frames);
        int swaps = OpenPoseTracking.FixLimbSwaps(frames);
        var video = OpenPoseMapper.Map(rig, frames, "walk", false, true);
        var ext = MotionCycleExtractor.Extract(video, 8, "walk", true);
        Console.WriteLine($"      {name}: {frames.Count} frames, facing {(facing >= 0 ? "right" : "left (mirrored)")}, {swaps} limb label swaps repaired");
        Console.WriteLine($"        thigh near (video): " + string.Join(" ", video.Select(p => ((int)(p[RigPart.LegNearUpper] * Mathf.Rad2Deg)).ToString())));
        Console.WriteLine($"        thigh far  (video): " + string.Join(" ", video.Select(p => ((int)(p[RigPart.LegFarUpper] * Mathf.Rad2Deg)).ToString())));
        Check(ext.Ok, $"{name}: a walking cycle was found ({ext.Notes})");
        if (!ext.Ok) continue;
        Console.WriteLine($"        cycle thigh near: " + string.Join(" ", ext.Poses.Select(p => ((int)(p[RigPart.LegNearUpper] * Mathf.Rad2Deg)).ToString())));
        Console.WriteLine($"        cycle thigh far : " + string.Join(" ", ext.Poses.Select(p => ((int)(p[RigPart.LegFarUpper] * Mathf.Rad2Deg)).ToString())));
        Console.WriteLine($"        cycle arm near  : " + string.Join(" ", ext.Poses.Select(p => ((int)(p[RigPart.ArmNearUpper] * Mathf.Rad2Deg)).ToString())));
        var contribution = PoseContribution.Compute(ext.Poses, reference, true);
        Console.WriteLine("        " + contribution.Summary);
        Check(contribution.Level != ContributionLevel.Negligible && contribution.AiMotionShare > 0.4f, $"{name}: the AI walk is clearly different from the procedural walk ({contribution.MeanDeviationDegrees:0.0} deg, {contribution.AiMotionShare:P0} unexplained)");
        var render = SpriteRig.Render(rig, sprite, ext.Poses, 102, 27, 27, false);
        using (var o = new BinaryWriter(File.Create("C:/AI/exp/sd/" + name + ".raw")))
        {
            o.Write(102); o.Write(render.Count);
            foreach (var f in render) foreach (var c in f) { o.Write(c.r); o.Write(c.g); o.Write(c.b); o.Write(c.a); }
        }
    }
    // the procedural reference compared with itself must be negligible, with a shifted phase too
    var shifted = new RigPose[8]; for (int i = 0; i < 8; i++) shifted[i] = reference[(i + 3) % 8];
    Check(PoseContribution.Compute(shifted, reference, true).Level == ContributionLevel.Negligible, "contribution: the procedural walk with another start phase is negligible, not AI");
    var tweaked = ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1.1f);
    Check(PoseContribution.Compute(tweaked, reference, true).Level == ContributionLevel.Negligible, "contribution: 10% more amplitude is negligible");
}

// ---------------------------------------------------------------- 7b. tracking, cycle extraction and facing on synthetic videos with known ground truth
Console.WriteLine("== Keypoint tracking and cycle extraction (synthetic videos with known motion)");
{
    // A 26-frame "video" of a walker: 1.3 gait cycles, hips drifting to the left (the walker travels), right/left limb labels swapped on some frames.
    int T = 26;
    var truth = ProceduralRigPoses.Instance.GetPoses("walk", 20, 20, 1f);        // one cycle = 20 frames
    var videoPoses = new RigPose[T];
    for (int i = 0; i < T; i++) videoPoses[i] = truth[i % 20];
    List<OpenPoseFrame> MakeVideo(bool swapSome, bool faceLeft)
    {
        var list = new List<OpenPoseFrame>();
        for (int i = 0; i < T; i++)
        {
            var f = OpenPoseMapper.Synthesize(rig, videoPoses[i]);
            for (int k = 0; k < OpenPoseFrame.Count; k++) f.point[k] += new Vector2(-1.5f * i + 200f, 100f);   // travelling
            if (swapSome && (i % 5 == 2 || i % 7 == 3))
                foreach (var (a, b) in new[] { (2, 5), (3, 6), (4, 7), (8, 11), (9, 12), (10, 13) }) { (f.point[a], f.point[b]) = (f.point[b], f.point[a]); }
            if (faceLeft) for (int k = 0; k < OpenPoseFrame.Count; k++) f.point[k] = new Vector2(-f.point[k].x, f.point[k].y);
            list.Add(f);
        }
        return list;
    }
    // facing
    Check(OpenPoseTracking.DetectFacing(MakeVideo(false, false)) == 1 && OpenPoseTracking.DetectFacing(MakeVideo(false, true)) == -1, "facing direction is detected from the face points");
    // limb swaps: in a synthetic walk legs only coincide at the crossing, swaps must be undone
    var swapped = MakeVideo(true, false);
    int fixedSwaps = OpenPoseTracking.FixLimbSwaps(swapped);
    var clean = MakeVideo(false, false);
    int wrongLabels = 0;
    for (int i = 0; i < T; i++) foreach (int k in new[] { 3, 4, 9, 10, 6, 7, 12, 13 }) if (Vector2.Distance(swapped[i].point[k], clean[i].point[k]) > 0.5f) wrongLabels++;
    Check(fixedSwaps > 0 && wrongLabels <= T * 8 / 30, $"left/right limb label swaps are repaired ({fixedSwaps} swaps made, {wrongLabels} of {T * 8} labels still wrong)");

    // cycle extraction from the clean video: full cycle exists (26 frames > 20)
    var mapped = OpenPoseMapper.Map(rig, MakeVideo(false, false), "walk", false, true);
    var ext = MotionCycleExtractor.Extract(mapped, 8, "walk", true);
    var refWalk = ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f);
    var contr = PoseContribution.Compute(ext.Poses, refWalk, true);
    Check(ext.Ok && contr.MeanDeviationDegrees < 5f, $"a gait cycle is cut out of a drifting 1.3-cycle clip and equals the true walk ({contr.MeanDeviationDegrees:0.0} deg, {ext.Method.Split('(')[0].Trim()})");

    // only 0.7 cycles of video: the mirror symmetry of the gait must complete the cycle
    var shortVideo = new List<OpenPoseFrame>(); var allKp = MakeVideo(false, false);
    for (int i = 3; i < 3 + 14; i++) shortVideo.Add(allKp[i]);
    var extShort = MotionCycleExtractor.Extract(OpenPoseMapper.Map(rig, shortVideo, "walk", false, true), 8, "walk", true);
    var contrShort = PoseContribution.Compute(extShort.Poses, refWalk, true);
    Check(extShort.Ok && contrShort.MeanDeviationDegrees < 6f && extShort.Method.Contains("mirror"), $"0.7 cycles of video are completed by the gait's mirror symmetry ({contrShort.MeanDeviationDegrees:0.0} deg, {extShort.Method.Split('(')[0].Trim()})");

    // a person standing still contains no walk
    var still = new List<OpenPoseFrame>(); for (int i = 0; i < 16; i++) still.Add(OpenPoseMapper.Synthesize(rig, new RigPose()));
    Check(!MotionCycleExtractor.Extract(OpenPoseMapper.Map(rig, still, "walk", false, true), 8, "walk", true).Ok, "a clip of a person standing still is not accepted as a walk");

    // mirrored sprite: the same video must give the mirrored (= identical in rig angles) motion
    var mirroredRig = rig.Clone();
    for (int i = 0; i < mirroredRig.joints.Length; i++) mirroredRig.joints[i] = new Vector2(rig.width - rig.joints[i].x, rig.joints[i].y);
    for (int y = 0; y < rig.height; y++) for (int x = 0; x < rig.width; x++) mirroredRig.SetMask(rig.width - 1 - x, y, rig.GetMask(x, y));
    var rightCand = SDPoseAnalysis.Analyze(rig, MakeVideo(false, false), "walk", true, 8, false);
    var leftCand = SDPoseAnalysis.Analyze(mirroredRig, MakeVideo(false, false), "walk", true, 8, true);
    double worst = 0;
    for (int i = 0; i < 8; i++) foreach (RigPart part in recoverable) worst = Math.Max(worst, Math.Abs(rightCand.Extraction.Poses[i][part] - leftCand.Extraction.Poses[i][part]) * Mathf.Rad2Deg);
    Check(rightCand.Quality > 0.3f && worst < 0.5, $"left-facing sprite gets the same motion as the right-facing one (max diff {worst:0.00} deg, quality {rightCand.Quality:0.00})");

    // one-shot: an attack clip with a quiet start and end yields a window with the strike inside
    var attackClip = new List<OpenPoseFrame>();
    var attackTruth = ProceduralRigPoses.Instance.GetPoses("attack", 12, 12, 1f);
    for (int i = 0; i < 3; i++) attackClip.Add(OpenPoseMapper.Synthesize(rig, attackTruth[0]));
    foreach (var p in attackTruth) attackClip.Add(OpenPoseMapper.Synthesize(rig, p));
    for (int i = 0; i < 3; i++) attackClip.Add(OpenPoseMapper.Synthesize(rig, attackTruth[11]));
    var extAttack = MotionCycleExtractor.Extract(OpenPoseMapper.Map(rig, attackClip, "attack", false, false), 8, "attack", false);
    float armRange = 0; if (extAttack.Ok) { float lo = 999, hi = -999; foreach (var p in extAttack.Poses) { lo = Math.Min(lo, p[RigPart.ArmNearUpper] * Mathf.Rad2Deg); hi = Math.Max(hi, p[RigPart.ArmNearUpper] * Mathf.Rad2Deg); } armRange = hi - lo; }
    Check(extAttack.Ok && armRange > 60f, $"an attack window with the sword swing inside is cut out of a padded clip (near arm range {armRange:0} deg)");
}

// ---------------------------------------------------------------- 8. constraint stage
{
    string dir = Path.Combine(AppContext.BaseDirectory, "fixtures");
    if (!File.Exists(Path.Combine(dir, "sdpose_chosen_walk.json"))) dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(args[1]), "..", "posetests", "fixtures"));
    ConstraintTests.Run(rig, Check, dir);
}

// ---------------------------------------------------------------- 9. Rotate (side -> front, held -> needed side)
Console.WriteLine("== Rotate (side view -> front view held for a second -> the needed side)");
{
    string frontFile = Path.Combine(AppContext.BaseDirectory, "fixtures", "player_front_48.raw");
    if (!File.Exists(frontFile)) frontFile = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(args[1]), "..", "posetests", "fixtures", "player_front_48.raw"));
    var fraw = File.ReadAllBytes(frontFile);
    var frontSprite = new Color32[48 * 48];                 // file rows are top-down, the package loads sprites bottom-up
    for (int y = 0; y < 48; y++) for (int x = 0; x < 48; x++) { int o = ((47 - y) * 48 + x) * 4; frontSprite[y * 48 + x] = new Color32(fraw[o], fraw[o + 1], fraw[o + 2], fraw[o + 3]); }
    var side = SpriteRig.Render(rig, sprite, new[] { new RigPose() }, 102, 27, 27, false)[0];
    var front = TurnThroughFront.PlaceOnGrid(frontSprite, 48, 48, 102, 27, 27, 48);
    var seq = TurnThroughFront.Build(side, front, 102, 12, 1f, 1);

    int OpaqueWidth(Color32[] f) { int lo = 102, hi = -1; for (int y = 0; y < 102; y++) for (int x = 0; x < 102; x++) if (f[y * 102 + x].a > 0) { lo = Math.Min(lo, x); hi = Math.Max(hi, x); } return hi - lo + 1; }
    int LowestRow(Color32[] f) { for (int y = 0; y < 102; y++) for (int x = 0; x < 102; x++) if (f[y * 102 + x].a > 0) return y; return -1; }
    float axis = TurnThroughFront.FeetAxis(side, 102);

    Check(seq.Count == 18, $"Rotate: 18 frames for a 1 s hold at 12 FPS (1 side + 2 turning + 12 front + 2 turning + 1 side), got {seq.Count}");
    Check(seq[0].SequenceEqual(side), "Rotate: starts on the side view exactly as rendered");
    bool holdIdentical = true; for (int i = 3; i <= 14; i++) holdIdentical &= seq[i].SequenceEqual(front);
    Check(holdIdentical, "Rotate: the character faces the viewer for 12 frames = 1 second, every frame the character's own front sprite unchanged");
    Check(seq[17].SequenceEqual(TurnThroughFront.Squash(side, 102, axis, -1f)), "Rotate: ends on the opposite side (the side view mirrored about the feet axis)");
    var sideColours = new HashSet<(byte, byte, byte)>(); foreach (var px in side) if (px.a > 0) sideColours.Add((px.r, px.g, px.b));
    foreach (var px in front) if (px.a > 0) sideColours.Add((px.r, px.g, px.b));
    bool paletteOk = true, alphaOk = true;
    foreach (var f in seq) foreach (var px in f) { if (px.a > 0) paletteOk &= sideColours.Contains((px.r, px.g, px.b)); alphaOk &= px.a == 0 || px.a == 255; }
    Check(paletteOk && alphaOk, "Rotate: only the character's own colours (side and front art), alpha only 0/255");
    Check(OpaqueWidth(seq[1]) < OpaqueWidth(side) && OpaqueWidth(seq[2]) < OpaqueWidth(front), $"Rotate: the turning frames are narrower than the views they come from ({OpaqueWidth(seq[1])} px vs {OpaqueWidth(side)}, {OpaqueWidth(seq[2])} px vs {OpaqueWidth(front)})");
    Check(OpaqueWidth(seq[1]) == OpaqueWidth(seq[16]) && OpaqueWidth(seq[2]) == OpaqueWidth(seq[15]) || Math.Abs(OpaqueWidth(seq[2]) - OpaqueWidth(seq[15])) <= 1, "Rotate: the way back is as wide as the way in (same turning frames)");
    bool grounded = true; int ground = LowestRow(side); foreach (var f in seq) grounded &= LowestRow(f) == ground;
    Check(grounded, $"Rotate: the feet stay on the same ground row in every frame (row {ground})");
    var long2 = TurnThroughFront.Build(side, front, 102, 12, 2f, 2);
    Check(long2.Count == 1 + 4 + 24 + 4 + 1, $"Rotate: a 2 s hold with 2 squashed frames per half gives {long2.Count} frames");
    var placed = TurnThroughFront.PlaceOnGrid(frontSprite, 48, 48, 102, 27, 27, 48);
    Check(placed.SequenceEqual(front), "Rotate: the front sprite lands on the grid exactly where the side sprite's canvas is (same canvas, same ground line)");
}

// ---------------------------------------------------------------- 10. Jump (Rig, one-shot, no travel height)
Console.WriteLine("== Jump (crouch -> launch -> rise -> apex tuck -> fall -> landing crouch -> recover)");
{
    var jump = ProceduralRigPoses.Instance.GetPoses("jump", 10, 10, 1f);
    bool finite = true, inLimits = true;
    foreach (var p in jump)
    {
        finite &= float.IsFinite(p.hop) && float.IsFinite(p.root.x) && float.IsFinite(p.root.y);
        foreach (float a in p.angle) { finite &= float.IsFinite(a); inLimits &= Mathf.Abs(a) * Mathf.Rad2Deg <= 120f; }
    }
    Check(jump.Length == 10 && finite, "Jump: 10 finite poses");
    Check(inLimits, "Jump: every joint within +-120 degrees");
    Check(jump.All(p => p.hop >= 0f && p.hop <= 4f && p.root == Vector2.zero), $"Jump: carries no travel height or drift (max hop {jump.Max(p => p.hop):0.0} px, only keeps the hips at standing height)");
    bool endsAtRest = true; for (int i = 0; i < RigDefinition.PartCount; i++) endsAtRest &= Mathf.Abs(jump[0].angle[i] - jump[9].angle[i]) < 1e-5f;
    Check(endsAtRest && jump[0].hop == 0f && jump[9].hop == 0f, "Jump: starts and ends on the same standing pose (clean exit to the Animator's idle)");
    Check(jump[5].angle[(int)RigPart.LegNearUpper] > jump[0].angle[(int)RigPart.LegNearUpper] + 0.4f, "Jump: the near knee is lifted in the air");
    Check(jump[2].angle[(int)RigPart.ArmNearUpper] < 0f && jump[4].angle[(int)RigPart.ArmNearUpper] > 0.8f, "Jump: arms swing back for the crouch, forward and up in the air");

    int Bottom(Color32[] f) { for (int y = 0; y < 102; y++) for (int x = 0; x < 102; x++) if (f[y * 102 + x].a > 0) return y; return -1; }
    int Top(Color32[] f) { for (int y = 101; y >= 0; y--) for (int x = 0; x < 102; x++) if (f[y * 102 + x].a > 0) return y; return -1; }
    var frames = SpriteRig.Render(rig, sprite, jump, 102, 27, 27, false);
    var palette = new HashSet<(byte, byte, byte)>(); foreach (var px in sprite) if (px.a > 0) palette.Add((px.r, px.g, px.b));
    bool paletteOk = true, alphaOk = true, border = false;
    foreach (var f in frames)
    {
        foreach (var px in f) { if (px.a > 0) paletteOk &= palette.Contains((px.r, px.g, px.b)); alphaOk &= px.a == 0 || px.a == 255; }
        for (int i = 0; i < 102; i++) border |= f[i].a > 0 || f[101 * 102 + i].a > 0 || f[i * 102].a > 0 || f[i * 102 + 101].a > 0;
    }
    Check(paletteOk && alphaOk, "Jump: only the source colours, alpha only 0/255");
    Check(!border, "Jump: nothing touches the canvas border (no clipping)");
    int ground = Bottom(frames[0]);
    Check(Bottom(frames[9]) == ground && frames.All(f => Bottom(f) >= ground), $"Jump: the feet never go below the ground row {ground}; the first and last frames stand on it");
    // the hip's height as the renderer places it (y down; the silhouette top is no measure: ponytail and lean move it)
    float[] hipY = jump.Select(p => RigKinematics.JointPositions(rig, p)[(int)RigJoint.Hip].y).ToArray();
    Console.WriteLine("      hip y per frame: " + string.Join(" ", hipY.Select(y => y.ToString("0.0", inv))));
    Check(hipY[2] >= hipY[0] + 2f && hipY[8] >= hipY[0] + 2f, $"Jump: the body sinks at least 2 px in both crouches (hip {hipY[0]:0.0} -> {hipY[2]:0.0} and {hipY[8]:0.0})");
    Check(hipY.Skip(3).Take(5).All(y => Math.Abs(y - hipY[0]) <= 2.5f), "Jump: in the air the hips stay near standing height (the game supplies the height)");
    Check(frames[0].Zip(frames[9], (a, b) => a.a == b.a && a.r == b.r && a.g == b.g && a.b == b.b).All(x => x), "Jump: the last frame is pixel-identical to the first (rest pose)");
}

// ---------------------------------------------------------------- 11. Sit (Rig, one-shot, the last frame is the held pose)
Console.WriteLine("== Sit (dip -> lower into a deep crouch -> settle and hold)");
{
    var sit = ProceduralRigPoses.Instance.GetPoses("sit", 8, 8, 1f);
    bool finite = true, inLimits = true;
    foreach (var p in sit)
    {
        finite &= float.IsFinite(p.hop) && float.IsFinite(p.root.x) && float.IsFinite(p.root.y);
        foreach (float a in p.angle) { finite &= float.IsFinite(a); inLimits &= Mathf.Abs(a) * Mathf.Rad2Deg <= 155f; }
    }
    Check(sit.Length == 8 && finite, "Sit: 8 finite poses");
    Check(inLimits, "Sit: every joint within +-155 degrees (a deep knee bend)");
    Check(sit.All(p => p.hop == 0f && p.root == Vector2.zero), "Sit: no hop and no root drift (the renderer lowers the hips by pinning the lowest foot)");

    int Bottom(Color32[] f) { for (int y = 0; y < 102; y++) for (int x = 0; x < 102; x++) if (f[y * 102 + x].a > 0) return y; return -1; }
    var frames = SpriteRig.Render(rig, sprite, sit, 102, 27, 27, false);
    var palette = new HashSet<(byte, byte, byte)>(); foreach (var px in sprite) if (px.a > 0) palette.Add((px.r, px.g, px.b));
    bool paletteOk = true, alphaOk = true, border = false;
    foreach (var f in frames)
    {
        foreach (var px in f) { if (px.a > 0) paletteOk &= palette.Contains((px.r, px.g, px.b)); alphaOk &= px.a == 0 || px.a == 255; }
        for (int i = 0; i < 102; i++) border |= f[i].a > 0 || f[101 * 102 + i].a > 0 || f[i * 102].a > 0 || f[i * 102 + 101].a > 0;
    }
    Check(paletteOk && alphaOk, "Sit: only the source colours, alpha only 0/255");
    Check(!border, "Sit: nothing touches the canvas border (no clipping)");
    int ground = Bottom(frames[0]);
    Console.WriteLine("      lowest opaque row per frame: " + string.Join(" ", frames.Select(f => Bottom(f))));
    Check(frames.All(f => Math.Abs(Bottom(f) - ground) <= 1), $"Sit: the feet stay on the ground row {ground} in every frame (renderer rounding allows +-1 px, see HANDOVER)");
    float[] hipY = sit.Select(p => RigKinematics.JointPositions(rig, p)[(int)RigJoint.Hip].y).ToArray();
    Console.WriteLine("      hip y per frame: " + string.Join(" ", hipY.Select(y => y.ToString("0.0", inv))));
    Check(hipY[7] >= hipY[0] + 6f, $"Sit: the hips end clearly lower than standing (hip {hipY[0]:0.0} -> {hipY[7]:0.0})");
    Check(hipY[4] > hipY[1] && hipY[7] >= hipY[4] - 1.5f, "Sit: the body keeps lowering and stays down (no standing up again)");
    Check(Mathf.Abs(sit[6].angle[(int)RigPart.LegNearUpper] - sit[7].angle[(int)RigPart.LegNearUpper]) * Mathf.Rad2Deg < 8f, "Sit: the last two frames are close, so the held pose does not pop");
}

// ---------------------------------------------------------------- 12. Crouch (Rig, one-shot, standing low stance, the last frame is the held pose)
Console.WriteLine("== Crouch (dip -> sink into a low ready stance -> hold)");
{
    var cr = ProceduralRigPoses.Instance.GetPoses("crouch", 6, 6, 1f);
    bool finite = true, inLimits = true;
    foreach (var p in cr)
    {
        finite &= float.IsFinite(p.hop) && float.IsFinite(p.root.x) && float.IsFinite(p.root.y);
        foreach (float a in p.angle) { finite &= float.IsFinite(a); inLimits &= Mathf.Abs(a) * Mathf.Rad2Deg <= 150f; }
    }
    Check(cr.Length == 6 && finite, "Crouch: 6 finite poses");
    Check(inLimits, "Crouch: every joint within +-150 degrees (a deep squat)");
    Check(cr.All(p => p.hop == 0f && p.root == Vector2.zero), "Crouch: no hop and no root drift (the renderer lowers the hips by pinning the lowest foot)");

    int Bottom(Color32[] f) { for (int y = 0; y < 102; y++) for (int x = 0; x < 102; x++) if (f[y * 102 + x].a > 0) return y; return -1; }
    var frames = SpriteRig.Render(rig, sprite, cr, 102, 27, 27, false);
    var palette = new HashSet<(byte, byte, byte)>(); foreach (var px in sprite) if (px.a > 0) palette.Add((px.r, px.g, px.b));
    bool paletteOk = true, alphaOk = true, border = false;
    foreach (var f in frames)
    {
        foreach (var px in f) { if (px.a > 0) paletteOk &= palette.Contains((px.r, px.g, px.b)); alphaOk &= px.a == 0 || px.a == 255; }
        for (int i = 0; i < 102; i++) border |= f[i].a > 0 || f[101 * 102 + i].a > 0 || f[i * 102].a > 0 || f[i * 102 + 101].a > 0;
    }
    Check(paletteOk && alphaOk, "Crouch: only the source colours, alpha only 0/255");
    Check(!border, "Crouch: nothing touches the canvas border (no clipping)");
    int ground = Bottom(frames[0]);
    Console.WriteLine("      lowest opaque row per frame: " + string.Join(" ", frames.Select(f => Bottom(f))));
    Check(frames.All(f => Math.Abs(Bottom(f) - ground) <= 1), $"Crouch: the feet stay on the ground row {ground} in every frame (renderer rounding allows +-1 px)");
    float[] hipY = cr.Select(p => RigKinematics.JointPositions(rig, p)[(int)RigJoint.Hip].y).ToArray();
    Console.WriteLine("      hip y per frame: " + string.Join(" ", hipY.Select(y => y.ToString("0.0", inv))));
    Check(hipY[5] >= hipY[0] + 5f, $"Crouch: the hips end clearly lower than standing (hip {hipY[0]:0.0} -> {hipY[5]:0.0})");
    Check(hipY[5] >= hipY[3] - 1.5f && hipY[4] > hipY[1], "Crouch: the body sinks and stays down (no standing up again)");
    Check(Mathf.Abs(cr[4].angle[(int)RigPart.LegNearUpper] - cr[5].angle[(int)RigPart.LegNearUpper]) * Mathf.Rad2Deg < 4f, "Crouch: the last two frames are close, so the held pose does not pop");
    Check(cr.All(p => p[RigPart.Head] + p[RigPart.Body] == 0f), "Crouch: the head's net rotation is exactly 0 in every frame (Head = -Body: a tilted head re-samples the face pixels and distorts it)");
    Check(cr[5][RigPart.Body] * Mathf.Rad2Deg <= -25f, "Crouch: the held pose leans the torso forward at least 25 degrees (compact, head low and forward)");
    // no piece of the character may float free of the rest (a gap behind a rotated thigh leaves the cloak tail detached); 4-connected, so a diagonal touch does not count as attached
    int Components(Color32[] f)
    {
        var seen = new bool[102 * 102]; int comps = 0;
        for (int s0 = 0; s0 < seen.Length; s0++)
        {
            if (seen[s0] || f[s0].a == 0) continue;
            comps++; var stack = new Stack<int>(); stack.Push(s0); seen[s0] = true;
            while (stack.Count > 0)
            {
                int c = stack.Pop(); int cx = c % 102, cy = c / 102;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) if (dx == 0 || dy == 0)
                {
                    int nx = cx + dx, ny = cy + dy; if (nx < 0 || ny < 0 || nx >= 102 || ny >= 102) continue;
                    int ni = ny * 102 + nx; if (!seen[ni] && f[ni].a > 0) { seen[ni] = true; stack.Push(ni); }
                }
            }
        }
        return comps;
    }
    int[] comps = frames.Select(Components).ToArray();
    Console.WriteLine("      separate pieces per frame (the standing frame has " + comps[0] + "): " + string.Join(" ", comps));
    Check(comps.All(c => c <= comps[0]), "Crouch: no frame has more separate pieces than the standing pose (nothing floats free)");
}

// ---------------------------------------------------------------- 13. CrouchWalk (Rig, loop, in place, the Crouch posture with alternating legs)
Console.WriteLine("== CrouchWalk (goose-step walk: the crouch posture, legs taking turns)");
{
    var cw = ProceduralRigPoses.Instance.GetPoses("crouchwalk", 8, 8, 1f);
    var stand = ProceduralRigPoses.Instance.GetPoses("idle", 1, 1, 1f)[0];
    bool finite = true;
    foreach (var p in cw) { finite &= float.IsFinite(p.hop) && float.IsFinite(p.root.x) && float.IsFinite(p.root.y); foreach (float a in p.angle) { finite &= float.IsFinite(a); finite &= Mathf.Abs(a) * Mathf.Rad2Deg <= 165f; } }
    Check(cw.Length == 8 && finite, "CrouchWalk: 8 finite poses, every joint within +-165 degrees (a folded knee)");
    Check(cw.All(p => p.hop == 0f && p.root == Vector2.zero), "CrouchWalk: in place (no hop, no root movement)");
    Check(cw.All(p => p[RigPart.Head] + p[RigPart.Body] == 0f), "CrouchWalk: the head's net rotation is exactly 0 in every frame (Head = -Body)");
    Check(cw.All(p => p[RigPart.LegNearUpper] * Mathf.Rad2Deg > 65f && p[RigPart.LegFarUpper] * Mathf.Rad2Deg > 65f), "CrouchWalk: both thighs stay beyond 65 degrees (the crouch posture; the renderer fills the pelvis)");
    float nMin = cw.Min(p => p[RigPart.LegNearUpper]), nMax = cw.Max(p => p[RigPart.LegNearUpper]);
    Check((nMax - nMin) * Mathf.Rad2Deg > 20f, $"CrouchWalk: the legs actually swing (near thigh range {(nMax - nMin) * Mathf.Rad2Deg:0} degrees)");
    Check(Mathf.Abs(cw[2][RigPart.LegNearUpper] + cw[2][RigPart.LegFarUpper] - cw[6][RigPart.LegNearUpper] - cw[6][RigPart.LegFarUpper]) < 1e-4f && cw[2][RigPart.LegNearUpper] > cw[6][RigPart.LegNearUpper], "CrouchWalk: the legs alternate (a half cycle later the near and far leg swap roles)");
    var cycle1 = ProceduralRigPoses.Instance.GetPoses("crouchwalk", 9, 8, 1f);
    bool closes = true; for (int i = 0; i < RigDefinition.PartCount; i++) closes &= Mathf.Abs(cycle1[0].angle[i] - cycle1[8].angle[i]) < 1e-4f;
    Check(closes, "CrouchWalk: frame 0 and the frame after the last one are the same pose (the loop closes)");

    int Bottom(Color32[] f) { for (int y = 0; y < 102; y++) for (int x = 0; x < 102; x++) if (f[y * 102 + x].a > 0) return y; return -1; }
    int Components(Color32[] f)
    {
        var seen = new bool[102 * 102]; int comps = 0;
        for (int s0 = 0; s0 < seen.Length; s0++)
        {
            if (seen[s0] || f[s0].a == 0) continue;
            comps++; var stack = new Stack<int>(); stack.Push(s0); seen[s0] = true;
            while (stack.Count > 0)
            {
                int c = stack.Pop(); int cx = c % 102, cy = c / 102;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) if (dx == 0 || dy == 0)
                {
                    int nx = cx + dx, ny = cy + dy; if (nx < 0 || ny < 0 || nx >= 102 || ny >= 102) continue;
                    int ni = ny * 102 + nx; if (!seen[ni] && f[ni].a > 0) { seen[ni] = true; stack.Push(ni); }
                }
            }
        }
        return comps;
    }
    var frames = SpriteRig.Render(rig, sprite, cw, 102, 27, 27, false);
    var standing = SpriteRig.Render(rig, sprite, new[] { stand }, 102, 27, 27, false)[0];
    var palette = new HashSet<(byte, byte, byte)>(); foreach (var px in sprite) if (px.a > 0) palette.Add((px.r, px.g, px.b));
    bool paletteOk = true, alphaOk = true, border = false;
    foreach (var f in frames)
    {
        foreach (var px in f) { if (px.a > 0) paletteOk &= palette.Contains((px.r, px.g, px.b)); alphaOk &= px.a == 0 || px.a == 255; }
        for (int i = 0; i < 102; i++) border |= f[i].a > 0 || f[101 * 102 + i].a > 0 || f[i * 102].a > 0 || f[i * 102 + 101].a > 0;
    }
    Check(paletteOk && alphaOk, "CrouchWalk: only the source colours, alpha only 0/255");
    Check(!border, "CrouchWalk: nothing touches the canvas border (no clipping)");
    int ground = Bottom(standing);
    Console.WriteLine("      lowest opaque row per frame: " + string.Join(" ", frames.Select(f => Bottom(f))));
    Check(frames.All(f => Math.Abs(Bottom(f) - ground) <= 1), $"CrouchWalk: the feet stay on the ground row {ground} in every frame (renderer rounding allows +-1 px)");
    float[] hipY = cw.Select(p => RigKinematics.JointPositions(rig, p)[(int)RigJoint.Hip].y).ToArray();
    float standHip = RigKinematics.JointPositions(rig, stand)[(int)RigJoint.Hip].y;
    Console.WriteLine("      hip y per frame (standing " + standHip.ToString("0.0", inv) + "): " + string.Join(" ", hipY.Select(y => y.ToString("0.0", inv))));
    Check(hipY.All(y => y >= standHip + 4f), "CrouchWalk: the hips stay low in every frame (at least 4 px below standing)");
    int[] comps = frames.Select(Components).ToArray();
    Console.WriteLine("      separate pieces per frame (standing " + Components(standing) + "): " + string.Join(" ", comps));
    Check(comps.All(c => c <= Components(standing)), "CrouchWalk: no frame has more separate pieces than the standing pose (nothing floats free)");
    int Diff(Color32[] a, Color32[] b) { int d = 0; for (int i = 0; i < a.Length; i++) if (a[i].a != b[i].a || a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b) d++; return d; }
    int maxStep = 0; for (int i = 0; i < frames.Count; i++) maxStep = Math.Max(maxStep, Diff(frames[i], frames[(i + 1) % frames.Count]));
    Console.WriteLine("      largest frame-to-frame change (pixels, including the loop seam): " + maxStep);
    Check(maxStep <= 600, "CrouchWalk: no frame-to-frame jump (the loop seam included); for reference Walk changes up to about 570 pixels per step and Run up to about 750");
}

// ---------------------------------------------------------------- 14. reduced leg swing (robed rigs): legs scaled, Run leans less, nothing else changes
Console.WriteLine("== Leg swing scale / robed Run lean (SpriteRig.ApplyLegSwingScale)");
{
    float Deg(float r) => r * Mathf.Rad2Deg;
    var runPoses = ProceduralRigPoses.Instance.GetPoses("run", 8, 8, 1f);
    var walkPoses = ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f);
    var attackPoses = ProceduralRigPoses.Instance.GetPoses("attack", 10, 10, 1f);
    Check(ReferenceEquals(SpriteRig.ApplyLegSwingScale(rig, runPoses, "run"), runPoses) && ReferenceEquals(SpriteRig.ApplyLegSwingScale(rig, walkPoses, "walk"), walkPoses),
        "scale 1 (every hand-made / auto-built rig): the poses come back unchanged (same array), Run included");
    var unset = rig.Clone(); unset.legSwingScale = 0f;
    Check(ReferenceEquals(SpriteRig.ApplyLegSwingScale(unset, runPoses, "run"), runPoses), "scale 0 (a rig asset saved before 1.7.0) counts as 1");

    var robed = rig.Clone(); robed.legSwingScale = 0.3f;
    float before = runPoses[3][RigPart.Body];
    var runR = SpriteRig.ApplyLegSwingScale(robed, runPoses, "run");
    Check(runPoses[3][RigPart.Body] == before && !ReferenceEquals(runR, runPoses), "the input poses are not modified");
    bool lean = true, world = true, legs = true, rest = true;
    for (int i = 0; i < runR.Length; i++)
    {
        RigPose a = runPoses[i], b = runR[i];
        lean &= Mathf.Abs(b[RigPart.Body] - a[RigPart.Body] * SpriteRig.ReducedSwingRunLeanScale) < 1e-6f;
        foreach (var c in new[] { RigPart.Head, RigPart.ArmNearUpper, RigPart.ArmFarUpper })
            world &= Mathf.Abs((b[c] + b[RigPart.Body]) - (a[c] + a[RigPart.Body])) < 1e-5f;     // world angle unchanged
        foreach (RigPart leg in new[] { RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.FootNear, RigPart.LegFarUpper, RigPart.LegFarLower, RigPart.FootFar })
            legs &= Mathf.Abs(b[leg] - a[leg] * 0.3f) < 1e-6f;
        foreach (RigPart q in new[] { RigPart.Hair, RigPart.ArmNearLower, RigPart.Weapon, RigPart.ArmFarLower })
            rest &= b[q] == a[q];
        rest &= b.root == a.root && b.hop == a.hop;
    }
    Check(lean && Mathf.Abs(Deg(runR[0][RigPart.Body]) + 5.5f) < 0.01f, $"robed Run: torso lean x{SpriteRig.ReducedSwingRunLeanScale} (11 -> {-Deg(runR[0][RigPart.Body]):0.0} degrees)");
    Check(world, "robed Run: head and upper arms keep their world angles (the face is not re-sampled, the arm swing is unchanged)");
    Check(legs, "robed Run: leg angles x0.3");
    Check(rest, "robed Run: forearms, weapon, hair, root and hop unchanged");

    bool walkOk = true;
    foreach (var (kind, src) in new[] { ("walk", walkPoses), ("attack", attackPoses) })
    {
        var r = SpriteRig.ApplyLegSwingScale(robed, src, kind);
        for (int i = 0; i < r.Length; i++)
            for (int q = 0; q < RigDefinition.PartCount; q++)
            {
                bool isLeg = q >= (int)RigPart.LegNearUpper;
                walkOk &= isLeg ? Mathf.Abs(r[i].angle[q] - src[i].angle[q] * 0.3f) < 1e-6f : r[i].angle[q] == src[i].angle[q];
            }
    }
    Check(walkOk, "robed Walk and Attack: only the legs are scaled (the lean change is Run only)");
}

// ---------------------------------------------------------------- 7. fixtures for the Unity-side import tests (written when a folder is given)
if (args.Length > 2)
{
    Directory.CreateDirectory(args[2]);
    var rngF = new System.Random(9);
    // a) OpenPose keypoint JSON of a walk, as an estimator would output it: pixel coordinates, noise, one frame without a person, one missing wrist
    var walk = ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f);
    var sb = new System.Text.StringBuilder("[");
    for (int i = 0; i < walk.Length; i++)
    {
        var f = OpenPoseMapper.Synthesize(rig, walk[i]);
        sb.Append("{\"canvas_width\":160,\"canvas_height\":160,\"people\":[");
        if (i != 5)
        {
            sb.Append("{\"pose_keypoints_2d\":[");
            for (int k = 0; k < 18; k++)
            {
                float nx = (float)(rngF.NextDouble() - 0.5) * 0.8f, ny = (float)(rngF.NextDouble() - 0.5) * 0.8f;
                float c = (i == 2 && k == 4) ? 0f : 0.9f;
                sb.Append((f.point[k].x + 56 + nx).ToString("0.###", inv)).Append(',').Append((f.point[k].y + 56 + ny).ToString("0.###", inv)).Append(',').Append(c.ToString("0.##", inv));
                if (k < 17) sb.Append(',');
            }
            sb.Append("]}");
        }
        sb.Append("]}"); if (i < walk.Length - 1) sb.Append(',');
    }
    sb.Append("]");
    File.WriteAllText(Path.Combine(args[2], "openpose_walk.json"), sb.ToString());

    // b) pose JSON with problems that must be repaired: null (NaN) joints, impossible angles, vertical drift
    var good = PoseChannels.FromPoses(ProceduralRigPoses.Instance.GetPoses("walk", 8, 8, 1f));
    string PoseJson(float[][] ch, bool nanSome)
    {
        var o = new System.Text.StringBuilder("{\"animation\":\"Walk\",\"fps\":12,\"loop\":true,\"frames\":[ ");
        for (int i = 0; i < ch.Length; i++)
        {
            o.Append("{\"root\":{\"x\":").Append(ch[i][PoseChannels.RootX].ToString("0.###", inv)).Append(",\"y\":").Append(ch[i][PoseChannels.RootY].ToString("0.###", inv)).Append(",\"hop\":").Append(ch[i][PoseChannels.Hop].ToString("0.###", inv)).Append('}');
            for (int p = 0; p < RigDefinition.PartCount; p++)
            {
                string v = float.IsNaN(ch[i][p]) ? "null" : ch[i][p].ToString("0.###", inv);
                o.Append(",\"").Append(((RigPart)p).ToString()).Append("\":{\"rotation\":").Append(v).Append('}');
            }
            o.Append(i < ch.Length - 1 ? "}, " : "} ");
        }
        return o.Append("]}").ToString();
    }
    var dirty = PoseChannels.Clone(good);
    dirty[2][(int)RigPart.ArmNearUpper] = float.NaN; dirty[4][(int)RigPart.LegFarLower] = 400f; dirty[3][(int)RigPart.Head] = -170f;
    for (int i = 0; i < 8; i++) { dirty[i][PoseChannels.RootY] = 5f; dirty[i][PoseChannels.Hop] = 4f; }
    File.WriteAllText(Path.Combine(args[2], "pose_dirty.json"), PoseJson(dirty, true));
    // c) garbage: most joints missing
    var garbage = PoseChannels.Clone(good);
    for (int i = 0; i < 8; i++) for (int p = 0; p < RigDefinition.PartCount; p++) if (p % 3 != 0) garbage[i][p] = float.NaN;
    File.WriteAllText(Path.Combine(args[2], "pose_garbage.json"), PoseJson(garbage, true));
    Console.WriteLine("fixtures written to " + args[2]);
}

Console.WriteLine(fails == 0 ? "\nALL TESTS PASSED" : $"\n{fails} TEST(S) FAILED");
return fails;
