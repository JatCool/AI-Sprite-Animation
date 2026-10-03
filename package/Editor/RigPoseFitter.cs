using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AISpriteAnimation
{
    public sealed class RigPoseFitOptions
    {
        /// <summary>Weight of a pixel whose colour differs between the AI frame and the rendered rig (a silhouette mismatch counts 1).</summary>
        public float colorWeight = 0.25f;
        /// <summary>Pull towards the guidance (sketch) pose, per (30 deg)^2. 0 = the AI evidence alone decides.</summary>
        public float priorWeight = 0f;
        /// <summary>Pull of every joint towards the neutral rest pose, per (30 deg)^2. Keeps joints the evidence cannot see (an arm inside the torso) quiet.</summary>
        public float restWeight = 0.004f;
        /// <summary>Pull towards the neighbouring frames, per (30 deg)^2. Stops the estimate from jumping between equally good solutions.</summary>
        public float temporalWeight = 0.01f;
        public float[] stepsDegrees = { 16f, 8f, 4f, 2f };
        public int maxSweepsPerStep = 5;
        public int refinementPasses = 2;
        public int maxRootPixels = 4;
        /// <summary>Called after each frame with (done, total).</summary>
        public Action<int, int> progress;
        public System.Threading.CancellationToken cancel;
    }

    public sealed class RigPoseFitResult
    {
        public RigPose[] Poses;
        public float[] FrameError;      // per frame: mismatching fraction of the character's pixels (0 = identical, silhouette + colour)
        public float MeanError;
        public int Renders;
    }

    /// <summary>
    /// Estimates a rig pose for each AI-generated frame by analysis-by-synthesis: the pose is adjusted until the rig, rendered from the original
    /// sprite pixels, matches the silhouette (and colours) the AI drew. The AI image itself is only used as evidence, never copied.
    /// Because the rig knows which limb is near and which is far, the left/right limb confusion of generic pose estimators on side views cannot happen.
    /// Pure math (runs on a worker thread).
    /// </summary>
    public static class RigPoseFitter
    {
        private const int Dofs = RigDefinition.PartCount + 1;   // joint angles + root x
        private const int RootDof = RigDefinition.PartCount;

        // The search works on WORLD angles (the absolute orientation of each bone) instead of the relative angles stored in poses: turning one bone then
        // does not drag its children along, so single-bone moves are meaningful steps for a coordinate search. Poses use relative angles.
        private static float[] ToRelative(float[] world)
        {
            var rel = (float[])world.Clone();
            for (int p = 0; p < RigDefinition.PartCount; p++)
            {
                int parent = SpriteRig.Parent[p];
                if (parent >= 0) rel[p] = world[p] - world[parent];
            }
            return rel;
        }

        private static float[] ToWorld(float[] rel)
        {
            var w = (float[])rel.Clone();
            for (int p = 0; p < RigDefinition.PartCount; p++)   // parents always precede their children
            {
                int parent = SpriteRig.Parent[p];
                if (parent >= 0) w[p] = rel[p] + w[parent];
            }
            return w;
        }

        public static RigPoseFitResult Fit(RigDefinition rig, Color32[] spritePixels, IReadOnlyList<Color32[]> targets, int cells, int left, int bottom,
            bool facingLeft, IReadOnlyList<RigPose> guidance, string kind, bool loop, RigPoseFitOptions opt)
        {
            int n = targets.Count;
            var result = new RigPoseFitResult { Poses = new RigPose[n], FrameError = new float[n] };
            float[][] theta = new float[n][];      // WORLD degrees per part, then root x
            float[][] prior = new float[n][];      // RELATIVE degrees per part, then root x
            var lo = new float[Dofs]; var hi = new float[Dofs];   // limits of the RELATIVE angles
            for (int p = 0; p < RigDefinition.PartCount; p++) PoseLimits.Get(kind, p, out lo[p], out hi[p]);
            lo[RootDof] = -opt.maxRootPixels; hi[RootDof] = opt.maxRootPixels;

            for (int i = 0; i < n; i++)
            {
                prior[i] = new float[Dofs];
                if (guidance != null && i < guidance.Count)
                {
                    for (int p = 0; p < RigDefinition.PartCount; p++) prior[i][p] = guidance[i].angle[p] * Mathf.Rad2Deg;
                    prior[i][RootDof] = guidance[i].root.x;
                }
                var rel = (float[])prior[i].Clone();
                for (int d = 0; d < Dofs; d++) rel[d] = Mathf.Clamp(rel[d], lo[d], hi[d]);
                theta[i] = ToWorld(rel);
            }

            int opaque = 0;
            foreach (var c in spritePixels) if (c.a != 0) opaque++;
            float norm = Mathf.Max(1, opaque);
            bool airborne = PoseLimits.AllowsAirborne(kind);
            float HopOf(int i) => airborne && guidance != null && guidance.Count > 0 ? guidance[Mathf.Min(i, guidance.Count - 1)].hop : 0f;

            // Distance fields of the AI silhouettes: a thin limb drawn somewhere else still pulls the rig towards it (a plain overlap count has no slope there).
            var targetDistance = new float[n][];
            for (int i = 0; i < n; i++) targetDistance[i] = DistanceField(targets[i], cells);

            // Evaluates candidate parameter vectors (world angles) for frame i; one Render call shares the sprite set-up.
            // A candidate outside the joint limits gets the worst possible loss.
            float[] Evaluate(int i, List<float[]> candidates, float[] prevT, float[] nextT)
            {
                var rels = new List<float[]>(candidates.Count);
                var poses = new List<RigPose>(candidates.Count);
                foreach (var t in candidates) { var r = ToRelative(t); rels.Add(r); poses.Add(ToPose(r, HopOf(i))); }
                var frames = SpriteRig.Render(rig, spritePixels, poses, cells, left, bottom, facingLeft);
                Interlocked.Add(ref result.Renders, candidates.Count);
                float[] prevRel = prevT != null ? ToRelative(prevT) : null, nextRel = nextT != null ? ToRelative(nextT) : null;
                var losses = new float[candidates.Count];
                for (int k = 0; k < candidates.Count; k++)
                {
                    float[] r = rels[k];
                    bool legal = true;
                    for (int d = 0; d < Dofs && legal; d++) legal = r[d] >= lo[d] - 0.01f && r[d] <= hi[d] + 0.01f;
                    if (!legal) { losses[k] = float.MaxValue; continue; }
                    float mism = Mismatch(frames[k], targets[i], targetDistance[i], cells, opt.colorWeight) / norm;
                    float reg = 0f;
                    for (int d = 0; d < Dofs; d++)
                    {
                        float scale = d == RootDof ? 4f : 30f;
                        reg += opt.restWeight * Sq(r[d] / scale) + opt.priorWeight * Sq((r[d] - prior[i][d]) / scale);
                        if (prevRel != null) reg += opt.temporalWeight * Sq((r[d] - prevRel[d]) / scale);
                        if (nextRel != null) reg += opt.temporalWeight * Sq((r[d] - nextRel[d]) / scale);
                    }
                    losses[k] = mism + reg;
                }
                return losses;
            }

            // Frames are optimised in parallel (each pass reads its neighbours from the previous pass, so the result does not depend on thread timing).
            // The rig mask is built lazily on first use: touch it once here so the worker threads only read it.
            rig.GetMask(0, 0);
            int framesDone = 0;
            var parallel = new ParallelOptions { CancellationToken = opt.cancel, MaxDegreeOfParallelism = Math.Max(1, Math.Min(n, Environment.ProcessorCount - 1)) };
            for (int pass = 0; pass < Math.Max(1, opt.refinementPasses); pass++)
            {
                float[][] before = null;
                if (pass > 0) { before = new float[n][]; for (int i = 0; i < n; i++) before[i] = (float[])theta[i].Clone(); }
                int currentPass = pass;
                Parallel.For(0, n, parallel, i =>
                {
                    float[] prevT = before == null ? null : (i > 0 ? before[i - 1] : (loop ? before[n - 1] : null));
                    float[] nextT = before == null ? null : (i < n - 1 ? before[i + 1] : (loop ? before[0] : null));
                    OptimiseFrame(i, theta[i], prevT, nextT, opt, Evaluate);
                    if (currentPass == 0) opt.progress?.Invoke(Interlocked.Increment(ref framesDone), n);
                });
            }

            // Final per-frame errors (pure mismatch).
            for (int i = 0; i < n; i++)
            {
                var pose = ToPose(ToRelative(theta[i]), HopOf(i));
                var frame = SpriteRig.Render(rig, spritePixels, new[] { pose }, cells, left, bottom, facingLeft)[0];
                result.FrameError[i] = Mismatch(frame, targets[i], targetDistance[i], cells, opt.colorWeight) / norm;
                result.Poses[i] = pose;
                result.MeanError += result.FrameError[i] / n;
            }
            return result;
        }

        private delegate float[] EvaluateFunc(int frame, List<float[]> candidates, float[] prev, float[] next);

        // Parts that move with a part when it is rotated "carrying" its children.
        private static readonly int[][] Subtree = BuildSubtrees();

        private static int[][] BuildSubtrees()
        {
            var result = new int[RigDefinition.PartCount][];
            for (int p = 0; p < result.Length; p++)
            {
                var list = new List<int> { p };
                for (int q = p + 1; q < RigDefinition.PartCount; q++)
                    for (int a = SpriteRig.Parent[q]; a >= 0; a = SpriteRig.Parent[a])
                        if (a == p) { list.Add(q); break; }
                result[p] = list.ToArray();
            }
            return result;
        }

        private struct Move
        {
            public int dof;          // part index, or RootDof
            public bool carry;       // true: rotate the part together with all its children (a relative-angle change)
            public float delta;
            public int touched;      // bit mask of the degrees of freedom this move changes
        }

        private static float[] Apply(float[] theta, Move m)
        {
            var c = (float[])theta.Clone();
            if (m.dof == RootDof || !m.carry) c[m.dof] += m.delta;
            else foreach (int q in Subtree[m.dof]) c[q] += m.delta;
            return c;
        }

        private static void OptimiseFrame(int frame, float[] theta, float[] prevT, float[] nextT, RigPoseFitOptions opt, EvaluateFunc eval)
        {
            float current = eval(frame, new List<float[]> { theta }, prevT, nextT)[0];
            foreach (float step in opt.stepsDegrees)
            {
                for (int sweep = 0; sweep < opt.maxSweepsPerStep; sweep++)
                {
                    // Candidate moves: turn one bone alone (world angle), or turn it together with everything attached to it (relative angle).
                    var moves = new List<Move>(Dofs * 4);
                    for (int d = 0; d < Dofs; d++)
                        for (int sign = -1; sign <= 1; sign += 2)
                        {
                            if (d == RootDof) { moves.Add(new Move { dof = d, delta = sign * Mathf.Max(1f, Mathf.Round(step / 8f)), touched = 1 << d }); continue; }
                            moves.Add(new Move { dof = d, delta = sign * step, touched = 1 << d });
                            if (Subtree[d].Length > 1)
                            {
                                int mask = 0; foreach (int q in Subtree[d]) mask |= 1 << q;
                                moves.Add(new Move { dof = d, carry = true, delta = sign * step, touched = mask });
                            }
                        }
                    var cands = new List<float[]>(moves.Count);
                    foreach (var m in moves) cands.Add(Apply(theta, m));
                    float[] losses = eval(frame, cands, prevT, nextT);

                    // the best single move, and a combination of the best non-overlapping improving moves
                    var order = new List<int>();
                    for (int k = 0; k < cands.Count; k++) if (losses[k] < current) order.Add(k);
                    if (order.Count == 0) break;
                    order.Sort((a, b) => losses[a].CompareTo(losses[b]));

                    float[] chosen = cands[order[0]]; float chosenLoss = losses[order[0]];
                    if (order.Count > 1)
                    {
                        var combined = (float[])theta.Clone();
                        int used = 0, applied = 0;
                        foreach (int k in order)
                        {
                            if ((moves[k].touched & used) != 0) continue;
                            combined = Apply(combined, moves[k]); used |= moves[k].touched; applied++;
                        }
                        if (applied > 1)
                        {
                            float combinedLoss = eval(frame, new List<float[]> { combined }, prevT, nextT)[0];
                            if (combinedLoss < chosenLoss) { chosen = combined; chosenLoss = combinedLoss; }
                        }
                    }
                    Array.Copy(chosen, theta, theta.Length);
                    current = chosenLoss;
                }
            }
        }

        private static RigPose ToPose(float[] t, float hop)
        {
            var p = new RigPose { hop = hop };
            for (int i = 0; i < RigDefinition.PartCount; i++) p.angle[i] = t[i] * Mathf.Deg2Rad;
            p.root = new Vector2(t[RigDefinition.PartCount], 0f);
            return p;
        }

        private const float DistanceCap = 6f;

        // Chamfer distance of a frame's silhouette (cells to the nearest opaque cell, capped), 3-4 weights.
        private static float[] DistanceField(Color32[] frame, int n)
        {
            var d = new float[n * n];
            const float big = 1e6f;
            for (int i = 0; i < d.Length; i++) d[i] = frame[i].a != 0 ? 0f : big;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x; float v = d[i];
                    if (x > 0) v = Mathf.Min(v, d[i - 1] + 1f);
                    if (y > 0)
                    {
                        v = Mathf.Min(v, d[i - n] + 1f);
                        if (x > 0) v = Mathf.Min(v, d[i - n - 1] + 1.414f);
                        if (x < n - 1) v = Mathf.Min(v, d[i - n + 1] + 1.414f);
                    }
                    d[i] = v;
                }
            for (int y = n - 1; y >= 0; y--)
                for (int x = n - 1; x >= 0; x--)
                {
                    int i = y * n + x; float v = d[i];
                    if (x < n - 1) v = Mathf.Min(v, d[i + 1] + 1f);
                    if (y < n - 1)
                    {
                        v = Mathf.Min(v, d[i + n] + 1f);
                        if (x < n - 1) v = Mathf.Min(v, d[i + n + 1] + 1.414f);
                        if (x > 0) v = Mathf.Min(v, d[i + n - 1] + 1.414f);
                    }
                    d[i] = Mathf.Min(v, DistanceCap);
                }
            return d;
        }

        /// <summary>The fitter's mismatch (as a fraction of the sprite's opaque pixels) between a pose rendered from the rig and an AI frame. For diagnostics and tests.</summary>
        public static float FrameError(RigDefinition rig, Color32[] spritePixels, RigPose pose, Color32[] target, int cells, int left, int bottom, bool facingLeft, float colorWeight = 0.25f)
        {
            int opaque = 0;
            foreach (var c in spritePixels) if (c.a != 0) opaque++;
            var frame = SpriteRig.Render(rig, spritePixels, new[] { pose }, cells, left, bottom, facingLeft)[0];
            return Mismatch(frame, target, DistanceField(target, cells), cells, colorWeight) / Mathf.Max(1, opaque);
        }

        private const int MaxShift = 3;
        private const float ShiftPenalty = 1.5f;

        // Disagreement between the rendered rig and the AI frame, in "pixels": every opaque cell of one silhouette costs its (capped) distance to
        // the other silhouette (so a mismatch of one pixel costs 1 and a far-away limb costs up to the cap), plus a colour term on shared cells.
        // The AI may stand a pixel or two higher or lower than the rig's grounding puts the feet, so the best vertical offset (small penalty) is used:
        // otherwise every leg change that moves the grounding would look like a whole-body mismatch.
        private static float Mismatch(Color32[] rendered, Color32[] target, float[] targetDistance, int n, float colorWeight)
        {
            var renderedDistance = DistanceField(rendered, n);
            float best = float.MaxValue;
            for (int dy = -MaxShift; dy <= MaxShift; dy++)
            {
                float sum = Math.Abs(dy) * ShiftPenalty;
                for (int y = Math.Max(0, -dy); y < Math.Min(n, n - dy); y++)
                    for (int x = 0; x < n; x++)
                    {
                        int ri = y * n + x, ti = (y + dy) * n + x;
                        bool a = rendered[ri].a != 0, b = target[ti].a != 0;
                        if (a && !b) sum += targetDistance[ti];
                        else if (b && !a) sum += renderedDistance[ri];
                        else if (a && (rendered[ri].r != target[ti].r || rendered[ri].g != target[ti].g || rendered[ri].b != target[ti].b)) sum += colorWeight;
                    }
                if (sum < best) best = sum;
            }
            return best;
        }

        private static float Sq(float v) => v * v;
    }
}
