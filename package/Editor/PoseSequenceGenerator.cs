using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Draws procedural OpenPose-style skeleton images (one per frame) for the OpenPose ControlNet.
    /// Side view, character facing right (mirrored for left-facing sprites). Kinds: idle, walk, run, attack, none.
    /// The skeleton is scaled to the sprite's body box so the same preset works for any character size.
    /// </summary>
    public static class PoseSequenceGenerator
    {
        public static readonly string[] Kinds = { "idle", "walk", "run", "attack", "none" };

        // OpenPose body-18 limb pairs (0-based) and colours.
        private static readonly int[,] Limbs =
        {
            {1,2},{1,5},{2,3},{3,4},{5,6},{6,7},{1,8},{8,9},{9,10},{1,11},{11,12},{12,13},{1,0},{0,14},{14,16},{0,15},{15,17}
        };

        private static readonly Color32[] Colors =
        {
            new Color32(255,0,0,255), new Color32(255,85,0,255), new Color32(255,170,0,255), new Color32(255,255,0,255),
            new Color32(170,255,0,255), new Color32(85,255,0,255), new Color32(0,255,0,255), new Color32(0,255,85,255),
            new Color32(0,255,170,255), new Color32(0,255,255,255), new Color32(0,170,255,255), new Color32(0,85,255,255),
            new Color32(0,0,255,255), new Color32(85,0,255,255), new Color32(170,0,255,255), new Color32(255,0,255,255),
            new Color32(255,0,170,255), new Color32(255,0,85,255),
        };

        /// <summary>
        /// Returns <paramref name="frameCount"/> PNGs (canvas-sized, black background with the skeleton). The motion cycle lasts
        /// <paramref name="cycle"/> frames; extra frames beyond it continue the cycle (loops) or hold the final pose (attack).
        /// </summary>
        public static List<byte[]> Generate(string kind, int frameCount, int cycle, PreparedInput input, bool facingLeft)
        {
            var result = new List<byte[]>(frameCount);
            kind = (kind ?? "none").ToLowerInvariant();
            for (int i = 0; i < frameCount; i++)
            {
                var canvas = new Color32[input.Width * input.Height];
                for (int p = 0; p < canvas.Length; p++) canvas[p] = new Color32(0, 0, 0, 255);
                if (kind != "none")
                {
                    Vector2[] kp = SkeletonPoses.Evaluate(kind, i, cycle, input.BodyCenterX, input.BodyTop, input.BodyBottom);
                    if (facingLeft)
                        for (int k = 0; k < kp.Length; k++) kp[k].x = 2f * input.BodyCenterX - kp[k].x;
                    Render(canvas, input.Width, input.Height, kp);
                }
                result.Add(SpriteFrameProcessor.EncodePngTopDown(canvas, input.Width, input.Height));
            }
            return result;
        }

        private static void Render(Color32[] buf, int w, int h, Vector2[] kp)
        {
            int radius = Mathf.Max(3, (int)(h * 0.014f));
            for (int l = 0; l < Limbs.GetLength(0); l++)
            {
                Color32 c = Colors[l];
                var dim = new Color32((byte)(c.r * 0.6f), (byte)(c.g * 0.6f), (byte)(c.b * 0.6f), 255);
                DrawLine(buf, w, h, kp[Limbs[l, 0]], kp[Limbs[l, 1]], radius, dim);
            }
            for (int i = 0; i < kp.Length; i++) DrawDisc(buf, w, h, kp[i], radius, Colors[i]);
        }

        private static void DrawLine(Color32[] buf, int w, int h, Vector2 a, Vector2 b, int radius, Color32 c)
        {
            float len = Vector2.Distance(a, b);
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / (radius * 0.5f)));
            for (int s = 0; s <= steps; s++) DrawDisc(buf, w, h, Vector2.Lerp(a, b, s / (float)steps), radius, c);
        }

        private static void DrawDisc(Color32[] buf, int w, int h, Vector2 p, int radius, Color32 c)
        {
            int x0 = Mathf.Max(0, (int)(p.x - radius)), x1 = Mathf.Min(w - 1, (int)(p.x + radius));
            int y0 = Mathf.Max(0, (int)(p.y - radius)), y1 = Mathf.Min(h - 1, (int)(p.y + radius));
            float r2 = radius * radius;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x - p.x, dy = y - p.y;
                    if (dx * dx + dy * dy <= r2) buf[y * w + x] = c;
                }
        }
    }
}
