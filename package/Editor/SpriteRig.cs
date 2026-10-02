using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Options for the part-based rig. Everything is expressed as fractions of the character's body height,
    /// so the same preset works for any sprite size.
    /// </summary>
    [Serializable]
    public struct RigOptions
    {
        /// <summary>Scales all limb angles of the skeleton (1 = as drawn for the OpenPose images).</summary>
        public float swing;
        /// <summary>Also move the forward-protruding arm/weapon (needed for attacks; off for walk/idle where arms stay with the body).</summary>
        public bool moveForwardArm;
        /// <summary>Half width of the body column (fraction of body height); sprite pixels further out on the facing side count as the arm/weapon.</summary>
        public float bodyHalfWidth;
        /// <summary>Half width of the leg column (fraction of body height). Pixels further out in the leg band (a weapon tip, a cape) stay with the body.</summary>
        public float legHalfWidth;
        /// <summary>Where the neck is, as a fraction of body height from the top (everything above moves as the head).</summary>
        public float neckLine;
        /// <summary>Where the hips are, as a fraction of body height from the top (everything below is legs).</summary>
        public float hipLine;
        /// <summary>How much of the skeleton's arm swing the forward arm/weapon follows (it pivots at its base, so 1 would be a full-circle swing).</summary>
        public float armGain;

        public static RigOptions Default => new RigOptions { swing = 0.6f, moveForwardArm = false, bodyHalfWidth = 0.14f, legHalfWidth = 0.12f, neckLine = 0.15f, hipLine = 0.52f, armGain = 0.3f };
    }

    /// <summary>
    /// Part-based sprite rig: the source sprite is cut into rigid regions (head, body, near/far legs, optional forward arm)
    /// and the regions are moved by the procedural skeleton. Every output pixel is a pixel of the source sprite,
    /// so palette, shading and design stay exactly the same; only pose changes.
    /// </summary>
    public static class SpriteRig
    {
        private const int Upscale = 4;

        // Bones as keypoint pairs (OpenPose body-18 indices): start joint, end joint.
        private enum Bone { FarThigh, FarShin, NearThigh, NearShin, Body, Head, Arm, Count }

        private static readonly int[,] BoneKeypoints =
        {
            {11, 12}, // far thigh
            {12, 13}, // far shin
            {8, 9},   // near thigh
            {9, 10},  // near shin
            {8, 1},   // body: hip -> neck
            {1, 0},   // head: neck -> nose
            {2, 4},   // arm: shoulder -> wrist
        };

        // Back-to-front drawing order.
        private static readonly Bone[] DrawOrder = { Bone.FarThigh, Bone.FarShin, Bone.NearThigh, Bone.NearShin, Bone.Body, Bone.Head, Bone.Arm };

        /// <summary>
        /// Renders <paramref name="frameCount"/> posed frames of the sprite on a cell grid.
        /// </summary>
        /// <param name="sprite">Source pixels, bottom-left origin (Unity texture order), size sw x sh.</param>
        /// <param name="cells">Output grid size (cells x cells). One cell = one source pixel.</param>
        /// <param name="left">Grid column where the source sprite's left edge sits.</param>
        /// <param name="bottom">Grid row (from the bottom) where the source sprite's bottom edge sits.</param>
        /// <param name="cycle">Frames per motion cycle (frames beyond it continue the cycle).</param>
        /// <returns>Frames on the cell grid, bottom-left origin; transparent where empty.</returns>
        public static List<Color32[]> Animate(Color32[] sprite, int sw, int sh, string kind, int frameCount, int cycle,
            int cells, int left, int bottom, bool facingLeft, RigOptions options)
        {
            // Work with a right-facing sprite in y-down coordinates.
            var src = new Color32[sw * sh];
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                    src[y * sw + x] = sprite[(sh - 1 - y) * sw + (facingLeft ? sw - 1 - x : x)];
            int placeLeft = facingLeft ? cells - left - sw : left;
            int placeTop = cells - bottom - sh;   // grid row (from the top) of the sprite's top edge

            // Body geometry in sprite coordinates (y down).
            int minRow = int.MaxValue, maxRow = -1;
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                    if (src[y * sw + x].a >= 128) { if (y < minRow) minRow = y; if (y > maxRow) maxRow = y; }
            if (maxRow < 0) throw new InvalidOperationException("The sprite is fully transparent.");
            float top = minRow, bottomEdge = maxRow + 1;
            float H = bottomEdge - top;
            int midLo = minRow + Mathf.RoundToInt((maxRow - minRow) * 0.3f), midHi = minRow + Mathf.RoundToInt((maxRow - minRow) * 0.7f);
            double sumX = 0; int count = 0;
            for (int y = midLo; y <= midHi; y++)
                for (int x = 0; x < sw; x++)
                    if (src[y * sw + x].a >= 128) { sumX += x + 0.5; count++; }
            float cx = (float)(sumX / Mathf.Max(1, count));

            Vector2[] rest = SkeletonPoses.Evaluate("rest", 0, 1, cx, top, bottomEdge, options.swing, options.neckLine, options.hipLine);
            float neckY = rest[1].y, hipY = rest[8].y, kneeY = rest[9].y;
            float halfBody = options.bodyHalfWidth * H;

            // Label every source pixel with its bone.
            var label = new ushort[sw * sh];   // bit mask of bones: leg pixels belong to BOTH legs (full-width copies; the near leg is drawn over the far one)
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    if (src[y * sw + x].a == 0) { label[y * sw + x] = 0; continue; }
                    float px = x + 0.5f, py = y + 0.5f;
                    Bone b;
                    if (py < neckY) b = Bone.Head;
                    else if (py > hipY && Mathf.Abs(px - cx) > options.legHalfWidth * H) b = (options.moveForwardArm && px > cx) ? Bone.Arm : Bone.Body;   // weapon / cape: not a leg
                    else if (py > hipY)
                    {
                        label[y * sw + x] = py < kneeY ? (ushort)((1 << (int)Bone.NearThigh) | (1 << (int)Bone.FarThigh))
                                                       : (ushort)((1 << (int)Bone.NearShin) | (1 << (int)Bone.FarShin));
                        continue;
                    }
                    else if (options.moveForwardArm && px > cx + halfBody && py > neckY + 0.12f * H) b = Bone.Arm;   // below the shoulders only: hair tips stay with the head/body
                    else b = Bone.Body;
                    label[y * sw + x] = (ushort)(1 << (int)b);
                }

            // The forward arm / weapon pivots where it leaves the body (its base), not at the distant shoulder.
            Vector2 grip = new Vector2(cx, hipY);
            {
                float minX = float.MaxValue; double sy = 0; int n = 0;
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < sw; x++)
                        if ((label[y * sw + x] & (1 << (int)Bone.Arm)) != 0 && x + 0.5f < minX) minX = x + 0.5f;
                if (minX < float.MaxValue)
                    for (int y = 0; y < sh; y++)
                        for (int x = 0; x < sw; x++)
                            if ((label[y * sw + x] & (1 << (int)Bone.Arm)) != 0 && x + 0.5f < minX + 2f) { sy += y + 0.5f; n++; }
                if (n > 0) grip = new Vector2(minX, (float)(sy / n));
            }

            var frames = new List<Color32[]>(frameCount);
            int hi = cells * Upscale;
            var hiBuf = new Color32[hi * hi];
            var rot = new float[(int)Bone.Count];
            var cosv = new float[(int)Bone.Count];
            var sinv = new float[(int)Bone.Count];
            var q0 = new Vector2[(int)Bone.Count];
            var p0 = new Vector2[(int)Bone.Count];

            for (int f = 0; f < frameCount; f++)
            {
                Vector2[] pose = SkeletonPoses.Evaluate(kind, f, cycle, cx, top, bottomEdge, options.swing, options.neckLine, options.hipLine);
                for (int b = 0; b < (int)Bone.Count; b++)
                {
                    Vector2 P0 = rest[BoneKeypoints[b, 0]], P1 = rest[BoneKeypoints[b, 1]];
                    Vector2 Q0 = pose[BoneKeypoints[b, 0]], Q1 = pose[BoneKeypoints[b, 1]];
                    float theta = Mathf.Atan2(Q1.y - Q0.y, Q1.x - Q0.x) - Mathf.Atan2(P1.y - P0.y, P1.x - P0.x);
                    if (b == (int)Bone.Head) theta = Mathf.Atan2(pose[1].y - pose[8].y, pose[1].x - pose[8].x) - Mathf.Atan2(rest[1].y - rest[8].y, rest[1].x - rest[8].x); // head follows the body lean
                    if (b == (int)Bone.Arm)
                    {
                        theta *= options.armGain;
                        P0 = grip;
                        Q0 = grip + (pose[2] - rest[2]);   // the base follows the shoulder; the blade swings about it
                    }
                    rot[b] = theta; cosv[b] = Mathf.Cos(theta); sinv[b] = Mathf.Sin(theta);
                    p0[b] = P0; q0[b] = Q0;
                }

                Array.Clear(hiBuf, 0, hiBuf.Length);
                foreach (Bone bone in DrawOrder)
                {
                    int b = (int)bone;
                    if (bone == Bone.Arm && !options.moveForwardArm) continue;
                    // inverse transform: dest (sprite coords) -> source: p = P0 + R(-theta) (q - Q0)
                    float c = cosv[b], s = -sinv[b];
                    for (int dy = 0; dy < hi; dy++)
                    {
                        float qy = (dy + 0.5f) / Upscale - placeTop;
                        for (int dx = 0; dx < hi; dx++)
                        {
                            float qx = (dx + 0.5f) / Upscale - placeLeft;
                            float vx = qx - q0[b].x, vy = qy - q0[b].y;
                            float sx = p0[b].x + c * vx - s * vy;
                            float sy = p0[b].y + s * vx + c * vy;
                            int ix = Mathf.FloorToInt(sx), iy = Mathf.FloorToInt(sy);
                            if (ix < 0 || iy < 0 || ix >= sw || iy >= sh) continue;
                            if ((label[iy * sw + ix] & (1 << b)) == 0) continue;
                            hiBuf[dy * hi + dx] = src[iy * sw + ix];
                        }
                    }
                }
                frames.Add(Downsample(hiBuf, hi, cells, facingLeft));
            }
            return frames;
        }

        // Majority vote per cell over the hi-res subpixels (exact source colours, no blending), then close 1-cell holes.
        private static Color32[] Downsample(Color32[] hiBuf, int hi, int cells, bool mirror)
        {
            var grid = new Color32[cells * cells];       // y-down for now
            var votes = new Dictionary<int, int>();
            for (int cy = 0; cy < cells; cy++)
                for (int cx = 0; cx < cells; cx++)
                {
                    votes.Clear();
                    int opaque = 0;
                    for (int j = 0; j < Upscale; j++)
                        for (int i = 0; i < Upscale; i++)
                        {
                            Color32 c = hiBuf[(cy * Upscale + j) * hi + cx * Upscale + i];
                            if (c.a == 0) continue;
                            opaque++;
                            int code = (c.r << 16) | (c.g << 8) | c.b;
                            votes.TryGetValue(code, out int n); votes[code] = n + 1;
                        }
                    if (opaque * 2 < Upscale * Upscale) continue;
                    int best = 0, bestN = -1;
                    foreach (var kv in votes) if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
                    grid[cy * cells + cx] = new Color32((byte)(best >> 16), (byte)(best >> 8), (byte)best, 255);
                }

            CloseHoles(grid, cells);

            // y-down -> bottom-left origin (and undo the mirror used for left-facing sprites)
            var result = new Color32[cells * cells];
            for (int y = 0; y < cells; y++)
                for (int x = 0; x < cells; x++)
                    result[(cells - 1 - y) * cells + x] = grid[y * cells + (mirror ? cells - 1 - x : x)];
            return result;
        }

        // A transparent cell with at least 3 opaque 4-neighbours is a seam between parts: fill it with the most common neighbour colour.
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
