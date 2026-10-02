using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Renders a rigged sprite: the source pixels are assigned to parts (<see cref="RigDefinition"/>), the parts form a bone hierarchy
    /// (rotating an upper arm carries the forearm and weapon along), and each frame is composed by moving the parts.
    /// Every output pixel is a pixel of the source sprite; nothing is blended, blurred or redrawn.
    /// Pure math on pixel arrays (no editor or scene dependencies).
    /// </summary>
    public static class SpriteRig
    {
        private const int Upscale = 4;   // sub-pixels per source pixel while composing; reduced to one pixel by majority vote

        // Bone hierarchy. -1 = attached to the root (which only translates). Parents always come before their children in RigPart order.
        private static readonly int[] Parent =
        {
            -1,                                  // Body
            (int)RigPart.Body,                   // Head
            (int)RigPart.Head,                   // Hair
            (int)RigPart.Body,                   // ArmNearUpper
            (int)RigPart.ArmNearUpper,           // ArmNearLower
            (int)RigPart.ArmNearLower,           // Weapon (child of the near hand)
            (int)RigPart.Body,                   // ArmFarUpper
            (int)RigPart.ArmFarUpper,            // ArmFarLower
            -1,                                  // LegNearUpper
            (int)RigPart.LegNearUpper,           // LegNearLower
            (int)RigPart.LegNearLower,           // FootNear
            -1,                                  // LegFarUpper
            (int)RigPart.LegFarUpper,            // LegFarLower
            (int)RigPart.LegFarLower,            // FootFar
        };

        // The joint each part rotates about.
        private static readonly RigJoint[] Pivot =
        {
            RigJoint.Hip, RigJoint.Neck, RigJoint.HairPivot,
            RigJoint.ShoulderNear, RigJoint.ElbowNear, RigJoint.WeaponPivot,
            RigJoint.ShoulderFar, RigJoint.ElbowFar,
            RigJoint.Hip, RigJoint.KneeNear, RigJoint.FootNear,
            RigJoint.Hip, RigJoint.KneeFar, RigJoint.FootFar,
        };

        // Back to front.
        private static readonly RigPart[] DrawOrder =
        {
            RigPart.LegFarUpper, RigPart.LegFarLower, RigPart.FootFar,
            RigPart.ArmFarUpper, RigPart.ArmFarLower,
            RigPart.Body, RigPart.Head, RigPart.Hair,
            RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.FootNear,
            RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.Weapon,
        };

        private static readonly RigPart[] GroundParts = { RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.FootNear, RigPart.LegFarUpper, RigPart.LegFarLower, RigPart.FootFar };
        private static readonly RigPart[] OverlayParts = { RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.ArmFarUpper, RigPart.ArmFarLower };

        // 2D affine transform: x' = a*x + b*y + tx ; y' = c*x + d*y + ty
        private struct Affine
        {
            public float a, b, c, d, tx, ty;
            public static Affine Identity => new Affine { a = 1, d = 1 };
            public static Affine Translate(float x, float y) => new Affine { a = 1, d = 1, tx = x, ty = y };
            // rotation about a pivot; angle positive = towards +x for points hanging below the pivot (y is down)
            public static Affine RotateAbout(Vector2 p, float angle)
            {
                float cs = Mathf.Cos(angle), sn = Mathf.Sin(angle);
                return new Affine { a = cs, b = sn, c = -sn, d = cs, tx = p.x - (cs * p.x + sn * p.y), ty = p.y - (-sn * p.x + cs * p.y) };
            }
            public Vector2 Apply(float x, float y) => new Vector2(a * x + b * y + tx, c * x + d * y + ty);
            // this(other(p))
            public Affine Multiply(Affine o) => new Affine
            {
                a = a * o.a + b * o.c, b = a * o.b + b * o.d, tx = a * o.tx + b * o.ty + tx,
                c = c * o.a + d * o.c, d = c * o.b + d * o.d, ty = c * o.tx + d * o.ty + ty,
            };
            public Affine Inverse()
            {
                float det = a * d - b * c;
                float ia = d / det, ib = -b / det, ic = -c / det, id = a / det;
                return new Affine { a = ia, b = ib, c = ic, d = id, tx = -(ia * tx + ib * ty), ty = -(ic * tx + id * ty) };
            }
        }

        /// <summary>
        /// Renders one frame per pose on a cell grid (one cell = one source pixel).
        /// </summary>
        /// <param name="sprite">Source pixels, bottom-left origin (Unity texture order), size rig.width x rig.height.</param>
        /// <param name="cells">Output grid size (cells x cells).</param>
        /// <param name="left">Grid column of the source sprite's left edge.</param>
        /// <param name="bottom">Grid row (from the bottom) of the source sprite's bottom edge.</param>
        /// <returns>Frames on the cell grid, bottom-left origin; transparent where empty.</returns>
        public static List<Color32[]> Render(RigDefinition rig, Color32[] sprite, IReadOnlyList<RigPose> poses, int cells, int left, int bottom, bool facingLeft)
        {
            int sw = rig.width, sh = rig.height;
            if (sprite.Length != sw * sh) throw new ArgumentException("Sprite size does not match the rig.");

            // Working copies facing right, y down. A left-facing sprite is mirrored first and the result mirrored back.
            var src = new Color32[sw * sh];
            var mask = new ushort[sw * sh];
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    int sx = facingLeft ? sw - 1 - x : x;
                    src[y * sw + x] = sprite[(sh - 1 - y) * sw + sx];
                    mask[y * sw + x] = src[y * sw + x].a == 0 ? (ushort)0 : rig.GetMask(sx, y);
                }
            var pivot = new Vector2[RigDefinition.PartCount];
            for (int i = 0; i < pivot.Length; i++)
            {
                Vector2 j = rig.GetJoint(Pivot[i]);
                pivot[i] = new Vector2(facingLeft ? sw - j.x : j.x, j.y);
            }
            float groundY = rig.groundY;
            int placeLeft = facingLeft ? cells - left - sw : left;
            int placeTop = cells - bottom - sh;

            // Pixel lists and rest bounds per part.
            var pixels = new List<int>[RigDefinition.PartCount];
            var minB = new Vector2[RigDefinition.PartCount];
            var maxB = new Vector2[RigDefinition.PartCount];
            for (int p = 0; p < RigDefinition.PartCount; p++) { pixels[p] = new List<int>(); minB[p] = new Vector2(float.MaxValue, float.MaxValue); maxB[p] = new Vector2(-1, -1); }
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] == 0) continue;
                int x = i % sw, y = i / sw;
                for (int p = 0; p < RigDefinition.PartCount; p++)
                {
                    if ((mask[i] & (1 << p)) == 0) continue;
                    pixels[p].Add(i);
                    minB[p] = Vector2.Min(minB[p], new Vector2(x, y));
                    maxB[p] = Vector2.Max(maxB[p], new Vector2(x + 1, y + 1));
                }
            }

            int hi = cells * Upscale;
            var hiColor = new Color32[hi * hi];
            var hiOwner = new byte[hi * hi];
            var result = new List<Color32[]>(poses.Count);
            var world = new Affine[RigDefinition.PartCount];

            foreach (RigPose pose in poses)
            {
                // Hierarchical transforms: world(part) = world(parent) o rotation about the part's pivot.
                for (int p = 0; p < RigDefinition.PartCount; p++)
                {
                    Affine local = Affine.RotateAbout(pivot[p], pose.angle[p]);
                    world[p] = Parent[p] < 0 ? local : world[Parent[p]].Multiply(local);
                }

                // Grounding: the lowest foot/leg pixel always lands on the ground line (minus an intentional hop), so the feet never drift.
                float maxBottom = float.MinValue;
                foreach (RigPart gp in GroundParts)
                    foreach (int idx in pixels[(int)gp])
                    {
                        // both bottom corners: a rotated foot's lowest point is a corner, not the middle of the pixel's bottom edge
                        float y1 = world[(int)gp].Apply(idx % sw, (idx / sw) + 1f).y;
                        float y2 = world[(int)gp].Apply((idx % sw) + 1f, (idx / sw) + 1f).y;
                        if (y1 > maxBottom) maxBottom = y1;
                        if (y2 > maxBottom) maxBottom = y2;
                    }
                float groundShift = maxBottom > float.MinValue ? groundY - maxBottom : 0f;
                Affine root = Affine.Translate(Mathf.Round(pose.root.x), Mathf.Round(groundShift + pose.root.y - pose.hop));
                var m = new Affine[RigDefinition.PartCount];
                for (int p = 0; p < m.Length; p++) m[p] = root.Multiply(world[p]);

                Array.Clear(hiColor, 0, hiColor.Length);
                Array.Clear(hiOwner, 0, hiOwner.Length);
                foreach (RigPart part in DrawOrder)
                {
                    int pi = (int)part;
                    if (pixels[pi].Count == 0) continue;
                    DrawPart(m[pi], pi, src, mask, sw, sh, minB[pi], maxB[pi], placeLeft, placeTop, hi, hiColor, hiOwner);
                }

                Color32[] grid = Downsample(hiColor, hiOwner, hi, cells, out byte[] owner);
                FillVacatedBody(grid, owner, cells, pixels, m[(int)RigPart.Body], sw, placeLeft, placeTop);
                CloseHoles(grid, cells);

                // y-down -> bottom-left origin, undoing the mirror for left-facing sprites.
                var outFrame = new Color32[cells * cells];
                for (int y = 0; y < cells; y++)
                    for (int x = 0; x < cells; x++)
                        outFrame[(cells - 1 - y) * cells + x] = grid[y * cells + (facingLeft ? cells - 1 - x : x)];
                result.Add(outFrame);
            }
            return result;
        }

        // Inverse mapping: every destination sub-pixel looks up the source pixel it came from, so rotated parts have no holes.
        private static void DrawPart(Affine m, int part, Color32[] src, ushort[] mask, int sw, int sh, Vector2 minB, Vector2 maxB,
            int placeLeft, int placeTop, int hi, Color32[] hiColor, byte[] hiOwner)
        {
            Affine inv = m.Inverse();
            // destination bounds of the part's rest rectangle
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int c = 0; c < 4; c++)
            {
                Vector2 q = m.Apply((c & 1) == 0 ? minB.x : maxB.x, (c & 2) == 0 ? minB.y : maxB.y);
                x0 = Mathf.Min(x0, q.x); x1 = Mathf.Max(x1, q.x); y0 = Mathf.Min(y0, q.y); y1 = Mathf.Max(y1, q.y);
            }
            int dx0 = Mathf.Max(0, Mathf.FloorToInt((x0 + placeLeft) * Upscale) - 1), dx1 = Mathf.Min(hi - 1, Mathf.CeilToInt((x1 + placeLeft) * Upscale) + 1);
            int dy0 = Mathf.Max(0, Mathf.FloorToInt((y0 + placeTop) * Upscale) - 1), dy1 = Mathf.Min(hi - 1, Mathf.CeilToInt((y1 + placeTop) * Upscale) + 1);
            int bit = 1 << part;
            for (int dy = dy0; dy <= dy1; dy++)
            {
                float qy = (dy + 0.5f) / Upscale - placeTop;
                for (int dx = dx0; dx <= dx1; dx++)
                {
                    float qx = (dx + 0.5f) / Upscale - placeLeft;
                    Vector2 p = inv.Apply(qx, qy);
                    int ix = Mathf.FloorToInt(p.x), iy = Mathf.FloorToInt(p.y);
                    if (ix < 0 || iy < 0 || ix >= sw || iy >= sh) continue;
                    int si = iy * sw + ix;
                    if ((mask[si] & bit) == 0) continue;
                    hiColor[dy * hi + dx] = src[si];
                    hiOwner[dy * hi + dx] = (byte)(part + 1);
                }
            }
        }

        // Majority vote per cell over the sub-pixels: exact source colours only, no blending.
        private static Color32[] Downsample(Color32[] hiColor, byte[] hiOwner, int hi, int cells, out byte[] owner)
        {
            var grid = new Color32[cells * cells];
            owner = new byte[cells * cells];
            var votes = new Dictionary<int, int>();
            var owners = new Dictionary<int, byte>();
            for (int cy = 0; cy < cells; cy++)
                for (int cx = 0; cx < cells; cx++)
                {
                    votes.Clear(); owners.Clear();
                    int opaque = 0;
                    for (int j = 0; j < Upscale; j++)
                        for (int i = 0; i < Upscale; i++)
                        {
                            int hIdx = (cy * Upscale + j) * hi + cx * Upscale + i;
                            Color32 c = hiColor[hIdx];
                            if (c.a == 0) continue;
                            opaque++;
                            int code = (c.r << 16) | (c.g << 8) | c.b;
                            votes.TryGetValue(code, out int n); votes[code] = n + 1;
                            owners[code] = hiOwner[hIdx];
                        }
                    if (opaque * 2 < Upscale * Upscale) continue;
                    int best = 0, bestN = -1;
                    foreach (var kv in votes) if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
                    grid[cy * cells + cx] = new Color32((byte)(best >> 16), (byte)(best >> 8), (byte)best, 255);
                    owner[cy * cells + cx] = owners[best];
                }
            return grid;
        }

        // An arm drawn in front of the torso leaves a hole in the torso when it swings away. Fill those cells with the colour of the
        // nearest body cells (the colour that was around the arm), so the torso stays solid. Only cells where a moved arm used to be are touched.
        private static void FillVacatedBody(Color32[] grid, byte[] owner, int cells, List<int>[] pixels, Affine bodyMatrix, int sw, int placeLeft, int placeTop)
        {
            var holes = new List<int>();
            foreach (RigPart part in OverlayParts)
                foreach (int idx in pixels[(int)part])
                {
                    Vector2 q = bodyMatrix.Apply((idx % sw) + 0.5f, (idx / sw) + 0.5f);
                    int cx = Mathf.FloorToInt(q.x + placeLeft), cy = Mathf.FloorToInt(q.y + placeTop);
                    if (cx < 0 || cy < 0 || cx >= cells || cy >= cells) continue;
                    int ci = cy * cells + cx;
                    if (grid[ci].a == 0) holes.Add(ci);
                }
            if (holes.Count == 0) return;

            byte bodyOwner = (byte)((int)RigPart.Body + 1);
            var fills = new List<(int, Color32)>();
            var counts = new Dictionary<int, int>();
            foreach (int ci in holes)
            {
                int cx = ci % cells, cy = ci / cells;
                for (int radius = 1; radius <= 3; radius++)
                {
                    counts.Clear();
                    for (int dy = -radius; dy <= radius; dy++)
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= cells || ny >= cells) continue;
                            int ni = ny * cells + nx;
                            if (grid[ni].a == 0 || owner[ni] != bodyOwner) continue;
                            int code = (grid[ni].r << 16) | (grid[ni].g << 8) | grid[ni].b;
                            counts.TryGetValue(code, out int n); counts[code] = n + 1;
                        }
                    if (counts.Count == 0) continue;
                    int best = 0, bestN = -1;
                    foreach (var kv in counts) if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
                    fills.Add((ci, new Color32((byte)(best >> 16), (byte)(best >> 8), (byte)best, 255)));
                    break;
                }
            }
            foreach (var (ci, col) in fills) { grid[ci] = col; owner[ci] = bodyOwner; }
        }

        // A transparent cell with at least 3 opaque 4-neighbours is a seam between parts: fill it with a neighbour's colour.
        private static void CloseHoles(Color32[] g, int n)
        {
            var fill = new List<(int, Color32)>();
            for (int y = 1; y < n - 1; y++)
                for (int x = 1; x < n - 1; x++)
                {
                    int i = y * n + x;
                    if (g[i].a != 0) continue;
                    Color32 a = g[i - 1], b = g[i + 1], c = g[i - n], d = g[i + n];
                    int opaque = (a.a != 0 ? 1 : 0) + (b.a != 0 ? 1 : 0) + (c.a != 0 ? 1 : 0) + (d.a != 0 ? 1 : 0);
                    if (opaque < 3) continue;
                    fill.Add((i, a.a != 0 ? a : b.a != 0 ? b : c));
                }
            foreach (var (i, col) in fill) g[i] = col;
        }
    }
}
