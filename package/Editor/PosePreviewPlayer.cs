using System;
using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Plays a pose sequence on the original sprite before an AnimationClip is committed: the frames are rendered by the real rig renderer
    /// from the original pixels (so what you see is what Build Animation produces), with optional bone overlay. UI independent: the Sprite Rig Editor
    /// and the batch tools both use it.
    /// </summary>
    public sealed class PosePreviewPlayer
    {
        /// <summary>Bones drawn between joints (indices into <see cref="RigJoint"/>).</summary>
        public static readonly int[,] Bones =
        {
            { (int)RigJoint.Neck, (int)RigJoint.Hip },
            { (int)RigJoint.ShoulderNear, (int)RigJoint.ElbowNear }, { (int)RigJoint.ElbowNear, (int)RigJoint.HandNear },
            { (int)RigJoint.ShoulderFar, (int)RigJoint.ElbowFar }, { (int)RigJoint.ElbowFar, (int)RigJoint.HandFar },
            { (int)RigJoint.Hip, (int)RigJoint.KneeNear }, { (int)RigJoint.KneeNear, (int)RigJoint.FootNear },
            { (int)RigJoint.Hip, (int)RigJoint.KneeFar }, { (int)RigJoint.KneeFar, (int)RigJoint.FootFar },
            { (int)RigJoint.WeaponPivot, (int)RigJoint.WeaponTip },
        };

        public int Cells { get; private set; }
        public int Fps { get; private set; }
        public int Index { get; private set; }
        public bool Playing { get; set; } = true;
        public int FrameCount => Frames.Count;
        public string Label => $"Frame: {Index + 1} / {Frames.Count}";

        /// <summary>Rendered frames on a square cell grid, bottom-left origin, original pixels only.</summary>
        public List<Color32[]> Frames { get; private set; }
        /// <summary>The unposed sprite on the same grid (the "before").</summary>
        public Color32[] Original { get; private set; }
        /// <summary>Joint positions per frame on the grid (cells, y down from the top of the grid).</summary>
        public List<Vector2[]> Joints { get; private set; }

        private double lastTick;

        public static PosePreviewPlayer Build(RigDefinition rig, Color32[] spritePixels, IReadOnlyList<RigPose> poses, int fps, bool facingLeft)
        {
            int sw = rig.width, sh = rig.height;
            int cells = Mathf.CeilToInt(Mathf.Max(sw, sh) * 1.6f);
            int left = (cells - sw) / 2, bottom = (cells - sh) / 2;
            var player = new PosePreviewPlayer
            {
                Cells = cells,
                Fps = Mathf.Max(1, fps),
                Frames = SpriteRig.Render(rig, spritePixels, poses, cells, left, bottom, facingLeft),
                Original = SpriteRig.Render(rig, spritePixels, new[] { new RigPose() }, cells, left, bottom, facingLeft)[0],
                Joints = new List<Vector2[]>(poses.Count),
            };
            // Grid position of a sprite-space point: the sprite sits at (left, top = cells - bottom - sh); a left-facing sprite is already mirrored by JointPositions.
            int top = cells - bottom - sh;
            foreach (var pose in poses)
            {
                Vector2[] j = RigKinematics.JointPositions(rig, pose, facingLeft, true);
                for (int i = 0; i < j.Length; i++) j[i] = new Vector2(j[i].x + left, j[i].y + top);
                player.Joints.Add(j);
            }
            return player;
        }

        public void Step(int delta)
        {
            if (Frames.Count == 0) return;
            Index = ((Index + delta) % Frames.Count + Frames.Count) % Frames.Count;
        }

        public void Seek(int index) => Index = Mathf.Clamp(index, 0, Mathf.Max(0, Frames.Count - 1));

        /// <summary>Advances at the animation's frame rate while playing. Returns true when the frame changed.</summary>
        public bool Tick(double now)
        {
            if (!Playing || Frames.Count < 2) return false;
            if (now - lastTick < 1.0 / Fps) return false;
            lastTick = now;
            Step(1);
            return true;
        }

        /// <summary>A contact sheet: the original sprite, then every frame, optionally with the bones drawn on top. For batch checks and documentation.</summary>
        public Color32[] RenderSheet(int zoom, bool showBones, out int width, out int height)
        {
            int tiles = Frames.Count + 1, cols = Mathf.Min(tiles, 9), rows = (tiles + cols - 1) / cols;
            int tile = Cells * zoom, gap = 4;
            width = cols * (tile + gap); height = rows * (tile + gap);
            var sheet = new Color32[width * height];
            for (int i = 0; i < sheet.Length; i++) sheet[i] = new Color32(74, 96, 74, 255);
            for (int t = 0; t < tiles; t++)
            {
                int ox = (t % cols) * (tile + gap), oy = (t / cols) * (tile + gap);   // top-left of the tile, rows from the top
                Color32[] src = t == 0 ? Original : Frames[t - 1];
                for (int y = 0; y < Cells; y++)
                    for (int x = 0; x < Cells; x++)
                    {
                        Color32 c = src[(Cells - 1 - y) * Cells + x];
                        if (c.a == 0) continue;
                        for (int dy = 0; dy < zoom; dy++)
                            for (int dx = 0; dx < zoom; dx++)
                                sheet[(height - 1 - (oy + y * zoom + dy)) * width + ox + x * zoom + dx] = c;
                    }
                if (showBones && t > 0)
                    for (int b = 0; b < Bones.GetLength(0); b++)
                    {
                        Vector2 a = Joints[t - 1][Bones[b, 0]], c = Joints[t - 1][Bones[b, 1]];
                        DrawLine(sheet, width, height, ox + (int)(a.x * zoom), oy + (int)(a.y * zoom), ox + (int)(c.x * zoom), oy + (int)(c.y * zoom), new Color32(255, 255, 255, 255));
                    }
            }
            return sheet;   // bottom-left origin like a texture
        }

        private static void DrawLine(Color32[] buf, int w, int h, int x0, int y0, int x1, int y1, Color32 color)
        {
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
            while (true)
            {
                if (x0 >= 0 && x0 < w && y0 >= 0 && y0 < h) buf[(h - 1 - y0) * w + x0] = color;
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }
    }
}
